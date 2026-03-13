/// Tests for Polly resilience pipeline decorators
module AgentNet.Tests.PollyInProcessTests

open System
open System.Threading.Tasks
open NUnit.Framework
open Swensen.Unquote
open Polly
open AgentNet
open AgentNet.InProcess
open AgentNet.InProcess.Polly
open PollyDecorators

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
        decorate (pipeline retryPipeline)
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
        decorate (pipeline retryPipeline)
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
        decorate (PollyDecorators.pipeline retryPipeline)
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
            do! Task.Delay(5000, ctx.CancellationToken)
            return x * 2
        with :? OperationCanceledException ->
            tokenWasCancelled <- true
            return raise (OperationCanceledException())
    })

    let timeoutPipeline =
        ResiliencePipelineBuilder()
            .AddTimeout(Timeout.TimeoutStrategyOptions(Timeout = TimeSpan.FromMilliseconds(100.)))
            .Build()

    let timedWorkflow = workflow {
        step cancellableExecutor
        decorate (PollyDecorators.pipeline timeoutPipeline)
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
            do! Task.Delay(5000, ct)
            return "response"
        with :? OperationCanceledException ->
            tokenWasCancelled <- true
            return raise (OperationCanceledException())
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
        decorate (PollyDecorators.pipeline timeoutPipeline)
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

    // Polly retry policy
    let retryPipeline =
        ResiliencePipelineBuilder()
            .AddRetry(Retry.RetryStrategyOptions(MaxRetryAttempts = 3, Delay = TimeSpan.Zero))
            .Build()

    // Step 1: Fast, completes before cancellation
    let step1 = Executor.create "Step1" (fun (x: int) (ctx: WorkflowContext) -> task {
        do! Task.Delay(50, ctx.CancellationToken)
        step1Completed <- true
        return x * 2
    })

    // Step 2: Slow, will be running when external token is cancelled.
    let step2 = Executor.create "Step2" (fun (x: int) (ctx: WorkflowContext) -> task {
        try
            do! Task.Delay(5000, ctx.CancellationToken)
            return x + 1
        with :? OperationCanceledException ->
            step2Cancelled <- true
            return raise (OperationCanceledException())
    })

    // Step 3: Should never run
    let step3 = Executor.create "Step3" (fun (x: int) (_ctx: WorkflowContext) -> task {
        step3Ran <- true
        return x * 10
    })

    let myWorkflow = workflow {
        step step1
        decorate (pipeline retryPipeline)
        step step2
        decorate (pipeline retryPipeline)
        step step3
        decorate (pipeline retryPipeline)
    }

    // Schedule cancellation after 150ms (step 1 finishes in ~50ms, step 2 is in progress)
    cts.CancelAfter(150)

    // Act & Assert — external CT seeds ctx.CancellationToken, which Polly receives
    Assert.Catch(fun () ->
        (myWorkflow |> Workflow.InProcess.runWithCancellation cts.Token 5).GetAwaiter().GetResult() |> ignore) |> ignore
    step1Completed =! true   // Step 1 finished before cancellation
    step2Cancelled =! true   // Step 2 was cancelled mid-flight
    step3Ran =! false        // Step 3 never ran
