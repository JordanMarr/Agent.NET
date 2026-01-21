/// Tests for tryStep workflow operations with error short-circuiting (railway-oriented programming)
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


// =============================================================================
// tryStep tests - short-circuiting on Error
// =============================================================================

[<Test>]
let ``tryStep succeeds when all steps return Ok``() = task {
    // Arrange - all trySteps return Ok
    let parse (raw: string) : Task<Result<Document, ProcessingError>> = task {
        return Ok { Id = "doc-1"; Content = raw }
    }

    let validate (doc: Document) : Task<Result<ValidatedDoc, ProcessingError>> = task {
        return Ok { Doc = doc; IsValid = true; Errors = [] }
    }

    let processDoc (validated: ValidatedDoc) : Task<Result<ProcessedDoc, ProcessingError>> = task {
        let words = validated.Doc.Content.Split(' ').Length
        return Ok { Doc = validated; WordCount = words; Summary = $"Document with {words} words" }
    }

    let wf = workflow {
        tryStep parse
        tryStep validate
        tryStep processDoc
    }

    // Act
    let! result = Workflow.InProcess.tryRun "Hello world from F#" wf

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
let ``tryStep short-circuits on first Error``() = task {
    // Arrange
    let mutable step1Called = false
    let mutable step2Called = false
    let mutable step3Called = false

    let step1 (input: string) : Task<Result<Document, ProcessingError>> = task {
        step1Called <- true
        return Ok { Id = "1"; Content = input }
    }

    let step2 (doc: Document) : Task<Result<ValidatedDoc, ProcessingError>> = task {
        step2Called <- true
        return Error (ValidationError ("content", "Content is invalid"))
    }

    let step3 (validated: ValidatedDoc) : Task<Result<ProcessedDoc, ProcessingError>> = task {
        step3Called <- true
        return Ok { Doc = validated; WordCount = 0; Summary = "" }
    }

    let wf = workflow {
        tryStep step1
        tryStep step2
        tryStep step3
    }

    // Act
    let! result = Workflow.InProcess.tryRun "test input" wf

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
let ``tryStep with multiple error types in chain - all same error type``() = task {
    // Arrange: Each step can fail with different error cases, but same error type
    let parse (raw: string) : Task<Result<Document, ProcessingError>> = task {
        if raw.Length > 0 then return Ok { Id = "doc"; Content = raw }
        else return Error (ParseError "Empty input")
    }

    let validate (doc: Document) : Task<Result<ValidatedDoc, ProcessingError>> = task {
        if doc.Content.Contains("valid") then return Ok { Doc = doc; IsValid = true; Errors = [] }
        else return Error (ValidationError ("content", "Missing 'valid' keyword"))
    }

    let save (validated: ValidatedDoc) : Task<Result<ProcessedDoc, ProcessingError>> = task {
        if validated.Doc.Content.Length < 100 then
            return Ok { Doc = validated; WordCount = 1; Summary = "Saved" }
        else return Error (SaveError 500)
    }

    let wf = workflow {
        tryStep parse
        tryStep validate
        tryStep save
    }

    // Act & Assert: ParseError case
    let! parseResult = Workflow.InProcess.tryRun "" wf
    match parseResult with
    | Error (ParseError _) -> ()
    | _ -> Assert.Fail "Expected ParseError"

    // Act & Assert: ValidationError case (note: "bad content" does not contain "valid")
    let! validateResult = Workflow.InProcess.tryRun "bad content" wf
    match validateResult with
    | Error (ValidationError _) -> ()
    | _ -> Assert.Fail "Expected ValidationError"

    // Act & Assert: Success case
    let! successResult = Workflow.InProcess.tryRun "valid content" wf
    match successResult with
    | Ok processed -> processed.Summary =! "Saved"
    | _ -> Assert.Fail "Expected Ok"
}


// =============================================================================
// Mixed step/tryStep tests
// =============================================================================

[<Test>]
let ``step followed by tryStep works correctly``() = task {
    // Arrange - step for transformation, tryStep for validation
    let transform (input: string) : Task<Document> = task {
        return { Id = "doc-1"; Content = input.ToUpper() }
    }

    let validate (doc: Document) : Task<Result<ValidatedDoc, ProcessingError>> = 
        if doc.Content.Length > 5 
        then Ok { Doc = doc; IsValid = true; Errors = [] }
        else Error (ValidationError ("content", "Content too short"))
        |> Task.fromResult
    

    let wf = workflow {
        step transform
        tryStep validate
    }

    // Act - long input
    //let! longResult = Workflow.InProcess.tryRun "Hello World" wf
    //match longResult with
    //| Ok validated ->
    //    validated.Doc.Content =! "HELLO WORLD"
    //    validated.IsValid =! true
    //| Error _ -> Assert.Fail "Expected Ok for long content"

    // Act - short input
    let! shortResult = Workflow.InProcess.tryRun "Hi" wf
    match shortResult with
    | Error (ValidationError (_, reason)) -> reason =! "Content too short"
    | _ -> Assert.Fail "Expected ValidationError for short content"
}

[<Test>]
let ``tryStep followed by step short-circuits correctly``() = task {
    // Arrange
    let mutable stepCalled = false

    let validate (input: string) : Task<Result<Document, ProcessingError>> = task {
        if input.Length > 0 then return Ok { Id = "1"; Content = input }
        else return Error (ParseError "Empty input")
    }

    let transform (doc: Document) : Task<ProcessedDoc> = task {
        stepCalled <- true
        return { Doc = { Doc = doc; IsValid = true; Errors = [] }; WordCount = 1; Summary = "Done" }
    }

    let wf = workflow {
        tryStep validate
        step transform
    }

    // Act - success case
    let! successResult = Workflow.InProcess.tryRun "test" wf
    stepCalled =! true
    match successResult with
    | Ok processed -> processed.Summary =! "Done"
    | Error _ -> Assert.Fail "Expected Ok"

    // Reset and test error case
    stepCalled <- false
    let! errorResult = Workflow.InProcess.tryRun "" wf
    stepCalled =! false  // step should NOT be called
    match errorResult with
    | Error (ParseError _) -> ()
    | _ -> Assert.Fail "Expected ParseError"
}


// =============================================================================
// Async support tests
// =============================================================================

[<Test>]
let ``tryStep works with Async Result functions``() = task {
    // Arrange
    let validateAsync (input: string) : Async<Result<Document, ProcessingError>> = async {
        if input.Length > 3 then
            return Ok { Id = "async-doc"; Content = input }
        else
            return Error (ValidationError ("input", "Too short"))
    }

    let wf = workflow { tryStep validateAsync }

    // Act
    let! successResult = Workflow.InProcess.tryRun "Hello" wf
    let! failResult = Workflow.InProcess.tryRun "Hi" wf

    // Assert
    match successResult with
    | Ok doc -> doc.Id =! "async-doc"
    | Error _ -> Assert.Fail "Expected Ok"

    match failResult with
    | Error (ValidationError _) -> ()
    | _ -> Assert.Fail "Expected ValidationError"
}


// =============================================================================
// tryRun vs run behavior
//// =============================================================================

[<Test>]
let ``tryRun returns Ok when workflow succeeds``() = task {
    // Arrange
    let successStep (x: int) : Task<Result<string, ProcessingError>> = task {
        return Ok $"Value: {x}"
    }

    let wf = workflow { tryStep successStep }

    // Act
    let! result = Workflow.InProcess.tryRun 42 wf

    // Assert
    result =! (Ok "Value: 42")
}

// =============================================================================
// Workflow composition tests
// =============================================================================

[<Test>]
let ``Nested workflow with tryStep can be composed``() = task {
    // Arrange - inner workflow
    let parse (raw: string) : Task<Result<Document, ProcessingError>> = task {
        return Ok { Id = "inner"; Content = raw.ToUpper() }
    }

    let validate (doc: Document) : Task<Result<ValidatedDoc, ProcessingError>> = task {
        return Ok { Doc = doc; IsValid = true; Errors = [] }
    }

    let innerWorkflow = workflow {
        tryStep parse
        tryStep validate
    }

    // Convert inner workflow to an executor
    let innerAsExecutor = Workflow.InProcess.toExecutor "InnerWorkflow" innerWorkflow

    // Create outer workflow that wraps inner result
    let processStep (validated: ValidatedDoc) : Task<ProcessedDoc> = task {
        return { Doc = validated; WordCount = validated.Doc.Content.Split(' ').Length; Summary = "Processed" }
    }

    let outerWorkflow = workflow {
        step innerAsExecutor  // ValidatedDoc output (run doesn't return Result)
        step processStep
    }

    // Act
    let! result = Workflow.InProcess.run "hello world" outerWorkflow

    // Assert
    result.Doc.Doc.Content =! "HELLO WORLD"
    result.Summary =! "Processed"
}


// =============================================================================
// Type inference tests
// =============================================================================

[<Test>]
let ``tryStep error type is correctly unified across steps``() = task {
    // This test verifies that all trySteps must use the same error type
    // (enforced at compile time by the two TryStep overloads)

    let step1 (x: int) : Task<Result<string, ProcessingError>> = task {
        return Ok $"Step1: {x}"
    }

    let step2 (s: string) : Task<Result<Document, ProcessingError>> = task {
        return Ok { Id = "1"; Content = s }
    }

    let step3 (doc: Document) : Task<Result<ValidatedDoc, ProcessingError>> = task {
        return Ok { Doc = doc; IsValid = true; Errors = [] }
    }

    // All three steps use ProcessingError - compiles correctly
    let wf = workflow {
        tryStep step1
        tryStep step2
        tryStep step3
    }

    // Act
    let! result = Workflow.InProcess.tryRun 42 wf

    // Assert
    match result with
    | Ok validated -> validated.IsValid =! true
    | Error _ -> Assert.Fail "Expected Ok"
}

// Note: The following would NOT compile due to the two TryStep overloads:
// let step1 (x: int) : Task<Result<string, ErrorTypeA>> = ...
// let step2 (s: string) : Task<Result<int, ErrorTypeB>> = ...  // Different error type!
// workflow {
//     tryStep step1
//     tryStep step2  // Compile error: ErrorTypeB doesn't match ErrorTypeA
// }
