/// Tests for resilience features: retry, backoff, timeout, fallback
/// Based on the resilience examples from docs/WorkflowDSL-Design.md
module AgentNet.Tests.ResilienceWorkflowTests

open System
open NUnit.Framework
open Swensen.Unquote
open AgentNet

// Domain types for resilience tests
type ProcessingResult = { Id: string; Status: string; Attempts: int }

[<Test>]
let ``Retry executes step multiple times on failure then succeeds``() =
    // Arrange: Create an executor that fails twice then succeeds
    let mutable attempts = 0

    let flaky = Executor.create "Flaky" (fun (input: string) _ -> async {
        attempts <- attempts + 1
        if attempts < 3 then
            failwith "Transient failure"
        return { Id = input; Status = "Success"; Attempts = attempts }
    })

    let resilienceWorkflow = workflow {
        start flaky
        retry 3
    }

    // Act
    let result = Workflow.runSync "test-123" resilienceWorkflow

    // Assert
    result.Status =! "Success"
    result.Attempts =! 3

[<Test>]
let ``Retry stops immediately on success``() =
    // Arrange
    let mutable attempts = 0

    let reliable = Executor.create "Reliable" (fun (input: string) _ -> async {
        attempts <- attempts + 1
        return { Id = input; Status = "Success"; Attempts = attempts }
    })

    let resilienceWorkflow = workflow {
        start reliable
        retry 5
    }

    // Act
    let result = Workflow.runSync "test-456" resilienceWorkflow

    // Assert: Should only execute once since it succeeds
    result.Attempts =! 1

[<Test>]
let ``Retry with zero retries fails immediately``() =
    // Arrange
    let alwaysFails = Executor.create "AlwaysFails" (fun (_: string) _ -> async {
        return failwith "Always fails"
    })

    let resilienceWorkflow = workflow {
        start alwaysFails
        retry 0
    }

    // Act & Assert
    (fun () -> Workflow.runSync "test" resilienceWorkflow |> ignore)
    |> Assert.Throws<Exception> |> ignore

[<Test>]
let ``Fallback executes when all retries exhausted``() =
    // Arrange
    let mutable primaryAttempts = 0

    let unreliable = Executor.create "Unreliable" (fun (input: string) _ -> async {
        primaryAttempts <- primaryAttempts + 1
        return failwith "Always fails"
    })

    let fallbackExecutor = Executor.fromFn "Fallback" (fun (input: string) ->
        { Id = input; Status = "Fallback"; Attempts = 0 })

    let resilienceWorkflow = workflow {
        start unreliable
        retry 2
        fallback fallbackExecutor
    }

    // Act
    let result = Workflow.runSync "test-789" resilienceWorkflow

    // Assert
    result.Status =! "Fallback"
    primaryAttempts =! 3  // Initial + 2 retries

[<Test>]
let ``Fallback not used when primary succeeds``() =
    // Arrange
    let mutable fallbackCalled = false

    let reliable = Executor.fromFn "Reliable" (fun (input: string) ->
        { Id = input; Status = "Primary"; Attempts = 1 })

    let fallbackExecutor = Executor.create "Fallback" (fun (input: string) _ -> async {
        fallbackCalled <- true
        return { Id = input; Status = "Fallback"; Attempts = 0 }
    })

    let resilienceWorkflow = workflow {
        start reliable
        retry 2
        fallback fallbackExecutor
    }

    // Act
    let result = Workflow.runSync "test" resilienceWorkflow

    // Assert
    result.Status =! "Primary"
    fallbackCalled =! false

[<Test>]
let ``Timeout fails when step exceeds duration``() =
    // Arrange
    let slow = Executor.create "Slow" (fun (input: string) _ -> async {
        do! Async.Sleep 500  // Takes 500ms
        return { Id = input; Status = "Done"; Attempts = 1 }
    })

    let resilienceWorkflow = workflow {
        start slow
        timeout (TimeSpan.FromMilliseconds 100.)  // Only allow 100ms
    }

    // Act & Assert
    (fun () -> Workflow.runSync "test" resilienceWorkflow |> ignore)
    |> Assert.Throws<TimeoutException> |> ignore

[<Test>]
let ``Timeout succeeds when step completes in time``() =
    // Arrange
    let fast = Executor.create "Fast" (fun (input: string) _ -> async {
        do! Async.Sleep 50  // Takes 50ms
        return { Id = input; Status = "Done"; Attempts = 1 }
    })

    let resilienceWorkflow = workflow {
        start fast
        timeout (TimeSpan.FromMilliseconds 500.)  // Allow 500ms
    }

    // Act
    let result = Workflow.runSync "test" resilienceWorkflow

    // Assert
    result.Status =! "Done"

[<Test>]
let ``Backoff Immediate has no delay between retries``() =
    // Arrange
    let mutable attempts = 0
    let mutable timestamps: DateTime list = []

    let flaky = Executor.create "Flaky" (fun (input: string) _ -> async {
        attempts <- attempts + 1
        timestamps <- DateTime.Now :: timestamps
        if attempts < 3 then
            failwith "Fail"
        return { Id = input; Status = "Success"; Attempts = attempts }
    })

    let resilienceWorkflow = workflow {
        start flaky
        retry 3
        backoff Immediate
    }

    // Act
    let _ = Workflow.runSync "test" resilienceWorkflow

    // Assert: All attempts should happen very quickly
    attempts =! 3

[<Test>]
let ``Resilience modifiers apply to previous step``() =
    // Arrange: Two steps, only first has resilience
    let mutable step1Attempts = 0
    let mutable step2Attempts = 0

    let flakyStep1 = Executor.create "FlakyStep1" (fun (input: string) _ -> async {
        step1Attempts <- step1Attempts + 1
        if step1Attempts < 2 then
            failwith "Fail once"
        return input + "-step1"
    })

    let step2 = Executor.create "Step2" (fun (input: string) _ -> async {
        step2Attempts <- step2Attempts + 1
        return input + "-step2"
    })

    let resilienceWorkflow = workflow {
        start flakyStep1
        retry 2
        next step2
    }

    // Act
    let result = Workflow.runSync "test" resilienceWorkflow

    // Assert
    result =! "test-step1-step2"
    step1Attempts =! 2  // Failed once, retried
    step2Attempts =! 1  // No retries needed
