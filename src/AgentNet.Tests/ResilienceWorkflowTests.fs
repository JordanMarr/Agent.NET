/// Tests for resilience operations (retry, timeout, fallback)
module AgentNet.Tests.ResilienceWorkflowTests

open System
open NUnit.Framework
open Swensen.Unquote
open AgentNet

[<Test>]
let ``Retry succeeds after transient failure``() =
    // Arrange: A step that fails twice then succeeds
    let mutable attempts = 0
    let unreliableStep (x: int) =
        attempts <- attempts + 1
        if attempts < 3 then
            failwith "Transient error"
        else
            x * 2 |> Task.fromResult

    let resilientWorkflow = workflow {
        step unreliableStep
        retry 3
    }

    // Act
    let result = (resilientWorkflow |> Workflow.InProcess.run 5).GetAwaiter().GetResult()

    // Assert
    result =! 10
    attempts =! 3

[<Test>]
let ``Retry fails after max retries exceeded``() =
    // Arrange: A step that always fails
    let alwaysFails (x: int) =
        failwith "Permanent error"
        x |> Task.fromResult

    let resilientWorkflow = workflow {
        step alwaysFails
        retry 2
    }

    // Act & Assert - workflow should fail (exception type depends on MAF error handling)
    Assert.Catch(fun () ->
        (resilientWorkflow |> Workflow.InProcess.run 5).GetAwaiter().GetResult() |> ignore) |> ignore

[<Test>]
let ``Timeout completes within duration``() =
    // Arrange: A fast step
    let fastStep (x: int) =
        task {
            do! System.Threading.Tasks.Task.Delay(10)
            return x * 2
        }

    let timedWorkflow = workflow {
        step fastStep
        timeout (TimeSpan.FromSeconds 5.)
    }

    // Act
    let result = (timedWorkflow |> Workflow.InProcess.run 5).GetAwaiter().GetResult()

    // Assert
    result =! 10

[<Test>]
let ``Timeout throws when duration exceeded``() =
    // Arrange: A slow step
    let slowStep (x: int) =
        task {
            do! System.Threading.Tasks.Task.Delay(5000)
            return x * 2
        }

    let timedWorkflow = workflow {
        step slowStep
        timeout (TimeSpan.FromMilliseconds 50.)
    }

    // Act & Assert - workflow should fail when timeout is exceeded
    Assert.Catch(fun () ->
        (timedWorkflow |> Workflow.InProcess.run 5).GetAwaiter().GetResult() |> ignore) |> ignore

[<Test>]
let ``Fallback executes on failure``() =
    // Arrange
    let failingStep (x: int) =
        failwith "Primary failed"
        x |> Task.fromResult

    let fallbackStep (x: int) =
        x * 10 |> Task.fromResult

    let resilientWorkflow = workflow {
        step failingStep
        fallback fallbackStep
    }

    // Act
    let result = (resilientWorkflow |> Workflow.InProcess.run 5).GetAwaiter().GetResult()

    // Assert
    result =! 50

[<Test>]
let ``Fallback not executed when primary succeeds``() =
    // Arrange
    let mutable fallbackCalled = false
    let successStep (x: int) = x * 2 |> Task.fromResult
    let fallbackStep (x: int) =
        fallbackCalled <- true
        x * 10 |> Task.fromResult

    let resilientWorkflow = workflow {
        step successStep
        fallback fallbackStep
    }

    // Act
    let result = (resilientWorkflow |> Workflow.InProcess.run 5).GetAwaiter().GetResult()

    // Assert
    result =! 10
    fallbackCalled =! false

[<Test>]
let ``Retry and fallback can be combined``() =
    // Arrange: A step that always fails, with retry and fallback
    let mutable attempts = 0
    let alwaysFails (x: int) =
        attempts <- attempts + 1
        failwith "Always fails"
        x |> Task.fromResult

    let fallbackStep (x: int) = x * 100 |> Task.fromResult

    let resilientWorkflow = workflow {
        step alwaysFails
        retry 3
        fallback fallbackStep
    }

    // Act
    let result = (resilientWorkflow |> Workflow.InProcess.run 5).GetAwaiter().GetResult()

    // Assert
    result =! 500  // Fallback was used after all retries failed
    attempts =! 4  // 1 initial + 3 retries

[<Test>]
let ``Resilience operations work with named executors``() =
    // Arrange
    let mutable attempts = 0
    let unreliableExecutor = Executor.fromFn "Unreliable" (fun (x: int) ->
        attempts <- attempts + 1
        if attempts < 2 then failwith "Transient"
        x * 2)

    let resilientWorkflow = workflow {
        step unreliableExecutor
        retry 2
    }

    // Act
    let result = (resilientWorkflow |> Workflow.InProcess.run 5).GetAwaiter().GetResult()

    // Assert
    result =! 10
    attempts =! 2

[<Test>]
let ``Timeout on parallel fanOut step``() =
    // Arrange: Fast parallel steps
    let addOne (x: int) = task { return x + 1 }
    let addTwo (x: int) = task { return x + 2 }
    let sum (xs: int list) = xs |> List.sum |> Task.fromResult

    let parallelWorkflow = workflow {
        step (fun x -> x |> Task.fromResult)
        fanOut addOne addTwo
        timeout (TimeSpan.FromSeconds 5.)
        fanIn sum
    }

    // Act
    let result = (parallelWorkflow |> Workflow.InProcess.run 10).GetAwaiter().GetResult()

    // Assert
    result =! 23  // (10+1) + (10+2) = 23
