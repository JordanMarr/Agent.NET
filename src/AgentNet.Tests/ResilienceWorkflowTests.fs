/// Tests for resilience operations (retry, timeout, fallback, policy)
module AgentNet.Tests.ResilienceWorkflowTests

open System
open NUnit.Framework
open Swensen.Unquote
open Polly
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

[<Test>]
let ``Policy retries via Polly pipeline``() =
    // Arrange: A step that fails twice then succeeds, with a Polly retry policy
    let mutable attempts = 0
    let unreliableStep (x: int) =
        attempts <- attempts + 1
        if attempts < 3 then
            failwith "Transient error"
        else
            x * 2 |> Task.fromResult

    let retryPipeline =
        ResiliencePipelineBuilder()
            .AddRetry(Retry.RetryStrategyOptions(MaxRetryAttempts = 3))
            .Build()

    let resilientWorkflow = workflow {
        step unreliableStep
        policy retryPipeline
    }

    // Act
    let result = (resilientWorkflow |> Workflow.InProcess.run 5).GetAwaiter().GetResult()

    // Assert
    result =! 10
    attempts =! 3

[<Test>]
let ``Policy fails when Polly retries exhausted``() =
    // Arrange: A step that always fails
    let alwaysFails (x: int) =
        failwith "Permanent error"
        x |> Task.fromResult

    let retryPipeline =
        ResiliencePipelineBuilder()
            .AddRetry(Retry.RetryStrategyOptions(MaxRetryAttempts = 2, Delay = TimeSpan.Zero))
            .Build()

    let resilientWorkflow = workflow {
        step alwaysFails
        policy retryPipeline
    }

    // Act & Assert
    Assert.Catch(fun () ->
        (resilientWorkflow |> Workflow.InProcess.run 5).GetAwaiter().GetResult() |> ignore) |> ignore

[<Test>]
let ``Policy passes through on success``() =
    // Arrange: A step that always succeeds — policy should be a no-op
    let successStep (x: int) = x * 3 |> Task.fromResult

    let retryPipeline =
        ResiliencePipelineBuilder()
            .AddRetry(Retry.RetryStrategyOptions(MaxRetryAttempts = 2))
            .Build()

    let resilientWorkflow = workflow {
        step successStep
        policy retryPipeline
    }

    // Act
    let result = (resilientWorkflow |> Workflow.InProcess.run 7).GetAwaiter().GetResult()

    // Assert
    result =! 21

[<Test>]
let ``Policy forwards CancellationToken to step via WorkflowContext``() =
    // Arrange: An executor that reads the cancellation token from the context
    let mutable tokenWasCancelled = false
    let cancellableExecutor = Executor.create "Cancellable" (fun (x: int) (ctx: WorkflowContext) -> task {
        // Polly timeout will cancel this token
        try
            do! System.Threading.Tasks.Task.Delay(5000, ctx.CancellationToken)
            return x * 2
        with :? System.OperationCanceledException ->
            tokenWasCancelled <- true
            return raise (System.OperationCanceledException())
    })

    let timeoutPipeline =
        ResiliencePipelineBuilder()
            .AddTimeout(Timeout.TimeoutStrategyOptions(Timeout = TimeSpan.FromMilliseconds(100.)))
            .Build()

    let timedWorkflow = workflow {
        step cancellableExecutor
        policy timeoutPipeline
    }

    // Act & Assert - Polly should cancel the token, causing the step to abort
    Assert.Catch(fun () ->
        (timedWorkflow |> Workflow.InProcess.run 5).GetAwaiter().GetResult() |> ignore) |> ignore
    tokenWasCancelled =! true

[<Test>]
let ``Policy forwards CancellationToken through TypedAgent to ChatAgent``() =
    // Arrange: A ChatAgent whose Chat function observes the cancellation token
    let mutable tokenWasCancelled = false
    
    let config = { Name = Some "TestAgent"; Instructions = "test"; Tools = [] }

    let chatFn msg ct = task {
        try
            do! System.Threading.Tasks.Task.Delay(5000, ct)
            return "response"
        with :? System.OperationCanceledException ->
            tokenWasCancelled <- true
            return raise (System.OperationCanceledException())
    }

    let chatFullFn msg ct = { Text = "response"; Messages = [] } |> Task.fromResult
    let chatStreamFn msg = Unchecked.defaultof<_>
    
    let slowChatAgent = ChatAgent(config, chatFn, chatFullFn, chatStreamFn)

    let typedAgent = 
        TypedAgent.create
            (fun (x: int) -> $"process {x}")
            (fun _ response -> response)
            slowChatAgent

    let timeoutPipeline =
        ResiliencePipelineBuilder()
            .AddTimeout(Timeout.TimeoutStrategyOptions(Timeout = TimeSpan.FromMilliseconds(100.)))
            .Build()

    let timedWorkflow = workflow {
        step typedAgent
        policy timeoutPipeline
    }

    // Act & Assert - Polly timeout should cancel the token, which flows through TypedAgent to ChatAgent.Chat
    Assert.Catch(fun () ->
        (timedWorkflow |> Workflow.InProcess.run 42).GetAwaiter().GetResult() |> ignore) |> ignore
    tokenWasCancelled =! true

[<Test>]
let ``External CancellationToken via runWithCancellation flows through Polly policies``() =
    // Arrange: External token source that we cancel after 150ms
    use cts = new System.Threading.CancellationTokenSource()
    let mutable step1Completed = false
    let mutable step2Cancelled = false
    let mutable step3Ran = false

    // Polly retry policy — the external CT flows into pipeline.ExecuteAsync via ctx.CancellationToken
    let retryPipeline =
        ResiliencePipelineBuilder()
            .AddRetry(Retry.RetryStrategyOptions(MaxRetryAttempts = 3, Delay = TimeSpan.Zero))
            .Build()

    // Step 1: Fast, completes before cancellation
    let step1 = Executor.create "Step1" (fun (x: int) (ctx: WorkflowContext) -> task {
        do! System.Threading.Tasks.Task.Delay(50, ctx.CancellationToken)
        step1Completed <- true
        return x * 2
    })

    // Step 2: Slow, will be running when external token is cancelled.
    // Polly receives the external CT and propagates cancellation even during a retry attempt.
    let step2 = Executor.create "Step2" (fun (x: int) (ctx: WorkflowContext) -> task {
        try
            do! System.Threading.Tasks.Task.Delay(5000, ctx.CancellationToken)
            return x + 1
        with :? System.OperationCanceledException ->
            step2Cancelled <- true
            return raise (System.OperationCanceledException())
    })

    // Step 3: Should never run
    let step3 = Executor.create "Step3" (fun (x: int) (_ctx: WorkflowContext) -> task {
        step3Ran <- true
        return x * 10
    })

    let myWorkflow = workflow {
        step step1
        policy retryPipeline
        step step2
        policy retryPipeline
        step step3
        policy retryPipeline
    }

    // Schedule cancellation after 150ms (step 1 finishes in ~50ms, step 2 is in progress)
    cts.CancelAfter(150)

    // Act & Assert — external CT seeds ctx.CancellationToken, which Polly receives
    Assert.Catch(fun () ->
        (myWorkflow |> Workflow.InProcess.runWithCancellation cts.Token 5).GetAwaiter().GetResult() |> ignore) |> ignore
    step1Completed =! true   // Step 1 finished before cancellation
    step2Cancelled =! true   // Step 2 was cancelled mid-flight (Polly propagated the external CT)
    step3Ran =! false        // Step 3 never ran
