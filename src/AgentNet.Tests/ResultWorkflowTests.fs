/// Tests for result workflows with error short-circuiting (railway-oriented programming)
/// Based on the result workflow examples from docs/WorkflowDSL-Design.md
module AgentNet.Tests.ResultWorkflowTests

open System.Threading.Tasks
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
let ``Result workflow succeeds when all steps return Ok``() = task {
    // Arrange
    let parse = ResultExecutor.map "Parse" (fun (raw: string) ->
        { Id = "doc-1"; Content = raw })

    let validate = ResultExecutor.map "Validate" (fun (doc: Document) ->
        { Doc = doc; IsValid = true; Errors = [] })

    let processDoc = ResultExecutor.map "Process" (fun (validated: ValidatedDoc) ->
        let words = validated.Doc.Content.Split(' ').Length
        { Doc = validated; WordCount = words; Summary = $"Document with {words} words" })

    let resultWf = resultWorkflow {
        step parse
        step validate
        step processDoc
    }

    // Act
    let! result = ResultWorkflow.InProcess.run "Hello world from F#" resultWf
    // Assert
    match result with
    | Ok processed ->
        processed.WordCount =! 4
        processed.Summary =! "Document with 4 words"
        processed.Doc.IsValid =! true
    | Error _ ->
        Assert.Fail "Expected Ok result"
}

[<Test>]
let ``Result workflow short-circuits on first Error``() = task {
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
        step step1
        step step2
        step step3
    }

    // Act
    let! result = ResultWorkflow.InProcess.run "test input" resultWf
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
}

[<Test>]
let ``ResultExecutor_map wraps return value in Ok``() = task {
    // Arrange
    let doubler = ResultExecutor.map "Doubler" (fun (x: int) -> x * 2)

    let resultWf = resultWorkflow {
        step doubler
    }

    // Act
    let! result = ResultWorkflow.InProcess.run 21 resultWf
    // Assert
    result =! (Ok 42)
}

[<Test>]
let ``ResultExecutor_bind passes Result through unchanged``() = task {
    // Arrange: bind returns the Result directly
    let validator = ResultExecutor.bind "Validator" (fun (x: int) ->
        if x > 0 then Ok $"Valid: {x}"
        else Error (ParseError "Value must be positive"))

    let positiveWf = resultWorkflow { step validator }
    let negativeWf = resultWorkflow { step validator }

    // Act & Assert: Positive case
    let! positiveResult = ResultWorkflow.InProcess.run 10 positiveWf
    match positiveResult with
    | Ok value -> value =! "Valid: 10"
    | Error _ -> Assert.Fail "Expected Ok result"

    // Act & Assert: Negative case
    let! negativeResult = ResultWorkflow.InProcess.run -5 negativeWf
    match negativeResult with
    | Error (ParseError msg) -> msg =! "Value must be positive"
    | _ -> Assert.Fail "Expected ParseError"
}

[<Test>]
let ``ResultExecutor_mapTask wraps task result in Ok``() = task {
    // Arrange
    let taskFetcher = ResultExecutor.mapTask "TaskFetcher" (fun (id: string) -> task {
        do! Task.Delay 10  // Simulate async work
        return { Id = id; Content = $"Content for {id}" }
    })

    let resultWf = resultWorkflow {
        step taskFetcher
    }

    // Act
    let! result = ResultWorkflow.InProcess.run "doc-123" resultWf
    // Assert
    match result with
    | Ok doc ->
        doc.Id =! "doc-123"
        doc.Content =! "Content for doc-123"
    | Error _ ->
        Assert.Fail "Expected Ok result"
}

[<Test>]
let ``ResultExecutor_bindTask passes task Result through``() = task {
    // Arrange
    let taskValidator = ResultExecutor.bindTask "TaskValidator" (fun (doc: Document) -> task {
        do! Task.Delay 10
        if doc.Content.Length > 5 then
            return Ok { Doc = doc; IsValid = true; Errors = [] }
        else
            return Error (ValidationError ("content", "Content too short"))
    })

    let longDocWf = resultWorkflow { step taskValidator }
    let shortDocWf = resultWorkflow { step taskValidator }

    // Act & Assert: Long content passes
    let! longResult = ResultWorkflow.InProcess.run { Id = "1"; Content = "This is long enough" } longDocWf
    match longResult with
    | Ok validated -> validated.IsValid =! true
    | Error _ -> Assert.Fail "Expected Ok for long content"

    // Act & Assert: Short content fails
    let! shortResult = ResultWorkflow.InProcess.run { Id = "2"; Content = "Hi" } shortDocWf
    match shortResult with
    | Error (ValidationError (_, reason)) -> reason =! "Content too short"
    | _ -> Assert.Fail "Expected ValidationError for short content"
}

[<Test>]
let ``Result workflow with multiple error types in chain``() = task {
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
        step parse
        step validate
        step save
    }

    // Act & Assert: ParseError case
    let! parseResult = ResultWorkflow.InProcess.run "" resultWf
    match parseResult with
    | Error (ParseError _) -> ()
    | _ -> Assert.Fail "Expected ParseError"

    // Act & Assert: ValidationError case (note: "bad content" does not contain "valid")
    let! validateResult = ResultWorkflow.InProcess.run "bad content" resultWf
    match validateResult with
    | Error (ValidationError _) -> ()
    | _ -> Assert.Fail "Expected ValidationError"

    // Act & Assert: Success case
    let! successResult = ResultWorkflow.InProcess.run "valid content" resultWf
    match successResult with
    | Ok processed -> processed.Summary =! "Saved"
    | _ -> Assert.Fail "Expected Ok"
}

[<Test>]
let ``Result workflow output type is correctly inferred``() = task {
    // Arrange
    let stringToInt = ResultExecutor.map "StringToInt" (fun (s: string) -> s.Length)
    let intToBool = ResultExecutor.map "IntToBool" (fun (n: int) -> n > 5)
    let boolToDoc = ResultExecutor.map "BoolToDoc" (fun (b: bool) ->
        { Id = "result"; Content = if b then "Long" else "Short" })

    let resultWf = resultWorkflow {
        step stringToInt
        step intToBool
        step boolToDoc
    }

    // Act: Type inference should work - resultWf : ResultWorkflowDef<string, Document, 'error>
    let! result = ResultWorkflow.InProcess.run "Hello World!" resultWf
    // Assert
    match result with
    | Ok doc ->
        doc.Id =! "result"
        doc.Content =! "Long"  // "Hello World!" has 12 chars > 5
    | Error _ ->
        Assert.Fail "Expected Ok result"
}

[<Test>]
let ``Result workflow can be composed using toExecutor``() = task {
    // Arrange: Create an inner workflow
    let innerParse = ResultExecutor.map "InnerParse" (fun (raw: string) ->
        { Id = "inner"; Content = raw.ToUpper() })

    let innerValidate = ResultExecutor.map "InnerValidate" (fun (doc: Document) ->
        { Doc = doc; IsValid = true; Errors = [] })

    let innerWorkflow = resultWorkflow {
        step innerParse
        step innerValidate
    }

    // Convert inner workflow to an executor
    let innerAsExecutor = ResultWorkflow.InProcess.toExecutor "InnerWorkflow" innerWorkflow

    // Create outer workflow that uses the inner workflow
    let processStep = ResultExecutor.map "Process" (fun (validated: ValidatedDoc) ->
        { Doc = validated; WordCount = validated.Doc.Content.Split(' ').Length; Summary = "Processed" })

    let outerWorkflow = resultWorkflow {
        step innerAsExecutor
        step processStep
    }

    // Act
    let! result = ResultWorkflow.InProcess.run "hello world" outerWorkflow
    // Assert
    match result with
    | Ok processed ->
        processed.Doc.Doc.Content =! "HELLO WORLD"  // Inner workflow uppercased
        processed.Summary =! "Processed"
    | Error _ ->
        Assert.Fail "Expected Ok result"
}

// =============================================================================
// SRTP-based tests (new syntax without explicit ResultExecutor wrappers)
// =============================================================================

[<Test>]
let ``SRTP: Result workflow with direct Task<Result> functions``() = task {
    // Arrange - direct functions, no ResultExecutor wrapper needed
    let validate (input: string) : Task<Result<Document, ProcessingError>> = task {
        if input.Length > 0
        then return Ok { Id = "doc-1"; Content = input }
        else return Error (ParseError "Empty input")
    }

    let processDoc (doc: Document) : Task<Result<ValidatedDoc, ProcessingError>> = task {
        return Ok { Doc = doc; IsValid = true; Errors = [] }
    }

    let resultWf = resultWorkflow {
        step "Validate" validate
        step "ProcessDoc" processDoc
    }

    // Act
    let! result = ResultWorkflow.InProcess.run "Hello world" resultWf
    // Assert
    match result with
    | Ok validated -> validated.IsValid =! true
    | Error _ -> Assert.Fail "Expected Ok result"
}

[<Test>]
let ``SRTP: Result workflow short-circuits on error``() = task {
    // Arrange
    let mutable step2Called = false

    let step1 (input: string) : Task<Result<Document, ProcessingError>> = task {
        return Error (ParseError "Intentional failure")
    }

    let step2 (doc: Document) : Task<Result<ValidatedDoc, ProcessingError>> = task {
        step2Called <- true
        return Ok { Doc = doc; IsValid = true; Errors = [] }
    }

    let resultWf = resultWorkflow {
        step step1
        step step2
    }

    // Act
    let! result = ResultWorkflow.InProcess.run "test" resultWf
    // Assert
    step2Called =! false  // Should NOT be called due to short-circuit
    match result with
    | Error (ParseError _) -> ()
    | _ -> Assert.Fail "Expected ParseError"
}

[<Test>]
let ``SRTP: Result workflow with Async<Result> functions``() = task {
    // Arrange
    let validateAsync (input: string) : Async<Result<Document, ProcessingError>> = async {
        if input.Length > 3 then
            return Ok { Id = "async-doc"; Content = input }
        else
            return Error (ValidationError ("input", "Too short"))
    }

    let resultWf = resultWorkflow {
        step validateAsync
    }

    // Act
    let! successResult = ResultWorkflow.InProcess.run "Hello" resultWf
    let! failResult = ResultWorkflow.InProcess.run "Hi" resultWf
    // Assert
    match successResult with
    | Ok doc -> doc.Id =! "async-doc"
    | Error _ -> Assert.Fail "Expected Ok"

    match failResult with
    | Error (ValidationError _) -> ()
    | _ -> Assert.Fail "Expected ValidationError"
}

[<Test>]
let ``SRTP: ok wrapper for map semantics with Task functions``() = task {
    // Arrange - non-Result-returning functions wrapped with 'ok'
    let fetchData (id: string) =
        if id = "doc-123"
        then Ok { Id = id; Content = $"Content for {id}" }
        else Error (ParseError "Not found")
        |> Task.FromResult

    let transform (doc: Document) : Task<ValidatedDoc> =
        { Doc = doc; IsValid = true; Errors = [] } |> Task.FromResult

    let resultWf = resultWorkflow {
        step fetchData      // Task<string> -> Task<Result<Document, 'e>>
        step (ok transform) // Task<Document> -> Task<Result<ValidatedDoc, 'e>>
    }

    // Act
    let! result = ResultWorkflow.InProcess.run "doc-123" resultWf
    // Assert
    match result with
    | Ok validated ->
        validated.Doc.Id =! "doc-123"
        validated.IsValid =! true
    | Error _ -> Assert.Fail "Expected Ok result"
}

// Same as above test but with Error condition
[<Test>]
let ``SRTP: ok wrapper for map semantics with Task functions - Error case``() = task {
    // Arrange - non-Result-returning functions wrapped with 'ok'
    let fetchData (id: string) =
        if id = "doc-123"
        then Ok { Id = id; Content = $"Content for {id}" }
        else Error (ParseError "Not found")
        |> Task.FromResult
    let transform (doc: Document) : Task<ValidatedDoc> =
        { Doc = doc; IsValid = true; Errors = [] } |> Task.FromResult
    let resultWf = resultWorkflow {
        step fetchData      // Task<string> -> Task<Result<Document, 'e>>
        step (ok transform) // Task<Document> -> Task<Result<ValidatedDoc, 'e>>
    }
    // Act
    let! result = ResultWorkflow.InProcess.run "unknown-doc" resultWf
    // Assert
    match result with
    | Error (ParseError msg) ->
        msg =! "Not found"
    | _ ->
        Assert.Fail "Expected ParseError"
}

[<Test>]
let ``SRTP: ok wrapper for map semantics with Async functions``() = task {
    // Arrange - Async functions wrapped with 'ok'
    let fetchAsync (id: string) : Async<Document> = async {
        return { Id = id; Content = $"Async content for {id}" }
    }

    let resultWf = resultWorkflow {
        step (ok fetchAsync)
    }

    // Act
    let! result = ResultWorkflow.InProcess.run "async-doc" resultWf
    // Assert
    match result with
    | Ok doc ->
        doc.Id =! "async-doc"
        doc.Content =! "Async content for async-doc"
    | Error _ -> Assert.Fail "Expected Ok result"
}

[<Test>]
let ``SRTP: Mixed types in result workflow``() = task {
    // Arrange - mix of Task<Result>, Async<Result>, and ResultExecutor
    let taskStep (input: string) : Task<Result<Document, ProcessingError>> = task {
        return Ok { Id = "1"; Content = input }
    }

    let executorStep = ResultExecutor.bind "Validate" (fun (doc: Document) ->
        if doc.Content.Length > 0 then Ok { Doc = doc; IsValid = true; Errors = [] }
        else Error (ValidationError ("content", "Empty")))

    let asyncStep (validated: ValidatedDoc) : Async<Result<ProcessedDoc, ProcessingError>> = async {
        return Ok { Doc = validated; WordCount = 5; Summary = "Done" }
    }

    let resultWf = resultWorkflow {
        step taskStep           // Task<Result>
        step executorStep       // ResultExecutor (backwards compat)
        step asyncStep          // Async<Result>
    }

    // Act
    let! result = ResultWorkflow.InProcess.run "test input" resultWf
    // Assert
    match result with
    | Ok processed -> processed.Summary =! "Done"
    | Error _ -> Assert.Fail "Expected Ok"
}

[<Test>]
let ``Backwards compat: Existing ResultExecutor syntax still works``() = task {
    // Arrange - existing API should continue to work
    let parse = ResultExecutor.map "Parse" (fun (raw: string) ->
        { Id = "doc-1"; Content = raw })

    let validate = ResultExecutor.bind "Validate" (fun (doc: Document) ->
        Ok { Doc = doc; IsValid = true; Errors = [] })

    let resultWf = resultWorkflow {
        step parse
        step validate
    }

    // Act
    let! result = ResultWorkflow.InProcess.run "test" resultWf
    // Assert
    match result with
    | Ok validated -> validated.IsValid =! true
    | Error _ -> Assert.Fail "Expected Ok"
}
