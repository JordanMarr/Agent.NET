/// Tests for result workflows with error short-circuiting (railway-oriented programming)
/// Based on the result workflow examples from docs/WorkflowDSL-Design.md
module AgentNet.Tests.ResultWorkflowTests

open NUnit.Framework
open Swensen.Unquote
open AgentNet

// Domain types for result workflow tests
type Document = { Id: string; Content: string }

type ValidatedDoc = { Doc: Document; IsValid: bool; Errors: string list }

type ProcessedDoc = { Doc: ValidatedDoc; WordCount: int; Summary: string }

type ProcessingError =
    | ParseError of message: string
    | ValidationError of field: string * reason: string
    | SaveError of code: int

[<Test>]
let ``Result workflow succeeds when all steps return Ok``() =
    // Arrange
    let parse = ResultExecutor.map "Parse" (fun (raw: string) ->
        { Id = "doc-1"; Content = raw })

    let validate = ResultExecutor.map "Validate" (fun (doc: Document) ->
        { Doc = doc; IsValid = true; Errors = [] })

    let processDoc = ResultExecutor.map "Process" (fun (validated: ValidatedDoc) ->
        let words = validated.Doc.Content.Split(' ').Length
        { Doc = validated; WordCount = words; Summary = $"Document with {words} words" })

    let resultWf = resultWorkflow {
        start parse
        next validate
        next processDoc
    }

    // Act
    let result = ResultWorkflow.runSync "Hello world from F#" resultWf

    // Assert
    match result with
    | Ok processed ->
        processed.WordCount =! 4
        processed.Summary =! "Document with 4 words"
        processed.Doc.IsValid =! true
    | Error _ ->
        Assert.Fail "Expected Ok result"

[<Test>]
let ``Result workflow short-circuits on first Error``() =
    // Arrange
    let mutable step1Called = false
    let mutable step2Called = false
    let mutable step3Called = false

    let step1 = ResultExecutor.bind "Step1" (fun (input: string) ->
        step1Called <- true
        Ok { Id = "1"; Content = input })

    let step2 = ResultExecutor.bind "Step2" (fun (_: Document) ->
        step2Called <- true
        Error (ValidationError ("content", "Content is invalid")))

    let step3 = ResultExecutor.bind "Step3" (fun (doc: Document) ->
        step3Called <- true
        Ok { Doc = { Doc = doc; IsValid = true; Errors = [] }; WordCount = 0; Summary = "" })

    let resultWf = resultWorkflow {
        start step1
        next step2
        next step3
    }

    // Act
    let result = ResultWorkflow.runSync "test input" resultWf

    // Assert
    step1Called =! true
    step2Called =! true
    step3Called =! false  // Should NOT be called due to short-circuit

    match result with
    | Error (ValidationError (field, reason)) ->
        field =! "content"
        reason =! "Content is invalid"
    | _ ->
        Assert.Fail "Expected ValidationError"

[<Test>]
let ``ResultExecutor.map wraps return value in Ok``() =
    // Arrange
    let doubler = ResultExecutor.map "Doubler" (fun (x: int) -> x * 2)

    let resultWf = resultWorkflow {
        start doubler
    }

    // Act
    let result = ResultWorkflow.runSync 21 resultWf

    // Assert
    result =! (Ok 42)

[<Test>]
let ``ResultExecutor.bind passes Result through unchanged``() =
    // Arrange: bind returns the Result directly
    let validator = ResultExecutor.bind "Validator" (fun (x: int) ->
        if x > 0 then Ok $"Valid: {x}"
        else Error (ParseError "Value must be positive"))

    let positiveWf = resultWorkflow { start validator }
    let negativeWf = resultWorkflow { start validator }

    // Act & Assert: Positive case
    let positiveResult = ResultWorkflow.runSync 10 positiveWf
    match positiveResult with
    | Ok value -> value =! "Valid: 10"
    | Error _ -> Assert.Fail "Expected Ok result"

    // Act & Assert: Negative case
    let negativeResult = ResultWorkflow.runSync -5 negativeWf
    match negativeResult with
    | Error (ParseError msg) -> msg =! "Value must be positive"
    | _ -> Assert.Fail "Expected ParseError"

[<Test>]
let ``ResultExecutor.mapAsync wraps async result in Ok``() =
    // Arrange
    let asyncFetcher = ResultExecutor.mapAsync "AsyncFetcher" (fun (id: string) -> async {
        do! Async.Sleep 10  // Simulate async work
        return { Id = id; Content = $"Content for {id}" }
    })

    let resultWf = resultWorkflow {
        start asyncFetcher
    }

    // Act
    let result = ResultWorkflow.runSync "doc-123" resultWf

    // Assert
    match result with
    | Ok doc ->
        doc.Id =! "doc-123"
        doc.Content =! "Content for doc-123"
    | Error _ ->
        Assert.Fail "Expected Ok result"

[<Test>]
let ``ResultExecutor.bindAsync passes async Result through``() =
    // Arrange
    let asyncValidator = ResultExecutor.bindAsync "AsyncValidator" (fun (doc: Document) -> async {
        do! Async.Sleep 10
        if doc.Content.Length > 5 then
            return Ok { Doc = doc; IsValid = true; Errors = [] }
        else
            return Error (ValidationError ("content", "Content too short"))
    })

    let longDocWf = resultWorkflow { start asyncValidator }
    let shortDocWf = resultWorkflow { start asyncValidator }

    // Act & Assert: Long content passes
    let longResult = ResultWorkflow.runSync { Id = "1"; Content = "This is long enough" } longDocWf
    match longResult with
    | Ok validated -> validated.IsValid =! true
    | Error _ -> Assert.Fail "Expected Ok for long content"

    // Act & Assert: Short content fails
    let shortResult = ResultWorkflow.runSync { Id = "2"; Content = "Hi" } shortDocWf
    match shortResult with
    | Error (ValidationError (_, reason)) -> reason =! "Content too short"
    | _ -> Assert.Fail "Expected ValidationError for short content"

[<Test>]
let ``Result workflow with multiple error types in chain``() =
    // Arrange: Each step can fail with different error types
    let parse = ResultExecutor.bind "Parse" (fun (raw: string) ->
        if raw.Length > 0 then Ok { Id = "doc"; Content = raw }
        else Error (ParseError "Empty input"))

    let validate = ResultExecutor.bind "Validate" (fun (doc: Document) ->
        if doc.Content.Contains("valid") then Ok { Doc = doc; IsValid = true; Errors = [] }
        else Error (ValidationError ("content", "Missing 'valid' keyword")))

    let save = ResultExecutor.bind "Save" (fun (validated: ValidatedDoc) ->
        if validated.Doc.Content.Length < 100 then
            Ok { Doc = validated; WordCount = 1; Summary = "Saved" }
        else Error (SaveError 500))

    let resultWf = resultWorkflow {
        start parse
        next validate
        next save
    }

    // Act & Assert: ParseError case
    let parseResult = ResultWorkflow.runSync "" resultWf
    match parseResult with
    | Error (ParseError _) -> ()
    | _ -> Assert.Fail "Expected ParseError"

    // Act & Assert: ValidationError case (note: "bad content" does not contain "valid")
    let validateResult = ResultWorkflow.runSync "bad content" resultWf
    match validateResult with
    | Error (ValidationError _) -> ()
    | _ -> Assert.Fail "Expected ValidationError"

    // Act & Assert: Success case
    let successResult = ResultWorkflow.runSync "valid content" resultWf
    match successResult with
    | Ok processed -> processed.Summary =! "Saved"
    | _ -> Assert.Fail "Expected Ok"

[<Test>]
let ``Result workflow output type is correctly inferred``() =
    // Arrange
    let stringToInt = ResultExecutor.map "StringToInt" (fun (s: string) -> s.Length)
    let intToBool = ResultExecutor.map "IntToBool" (fun (n: int) -> n > 5)
    let boolToDoc = ResultExecutor.map "BoolToDoc" (fun (b: bool) ->
        { Id = "result"; Content = if b then "Long" else "Short" })

    let resultWf = resultWorkflow {
        start stringToInt
        next intToBool
        next boolToDoc
    }

    // Act: Type inference should work - resultWf : ResultWorkflowDef<string, Document, 'error>
    let result: Result<Document, ProcessingError> = ResultWorkflow.runSync "Hello World!" resultWf

    // Assert
    match result with
    | Ok doc ->
        doc.Id =! "result"
        doc.Content =! "Long"  // "Hello World!" has 12 chars > 5
    | Error _ ->
        Assert.Fail "Expected Ok result"

[<Test>]
let ``Result workflow can be composed using toExecutor``() =
    // Arrange: Create an inner workflow
    let innerParse = ResultExecutor.map "InnerParse" (fun (raw: string) ->
        { Id = "inner"; Content = raw.ToUpper() })

    let innerValidate = ResultExecutor.map "InnerValidate" (fun (doc: Document) ->
        { Doc = doc; IsValid = true; Errors = [] })

    let innerWorkflow = resultWorkflow {
        start innerParse
        next innerValidate
    }

    // Convert inner workflow to an executor
    let innerAsExecutor = ResultWorkflow.toExecutor "InnerWorkflow" innerWorkflow

    // Create outer workflow that uses the inner workflow
    let processStep = ResultExecutor.map "Process" (fun (validated: ValidatedDoc) ->
        { Doc = validated; WordCount = validated.Doc.Content.Split(' ').Length; Summary = "Processed" })

    let outerWorkflow = resultWorkflow {
        start innerAsExecutor
        next processStep
    }

    // Act
    let result = ResultWorkflow.runSync "hello world" outerWorkflow

    // Assert
    match result with
    | Ok processed ->
        processed.Doc.Doc.Content =! "HELLO WORLD"  // Inner workflow uppercased
        processed.Summary =! "Processed"
    | Error _ ->
        Assert.Fail "Expected Ok result"

