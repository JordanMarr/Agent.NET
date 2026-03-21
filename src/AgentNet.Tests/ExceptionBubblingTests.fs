/// Regression tests for exception bubbling through MAF.
/// MAF's Lockstep.RunAsync catches step exceptions internally and does not re-throw,
/// causing "No WorkflowOutputEvent found" instead of the real error. The fix wraps steps
/// with ExceptionDispatchInfo capture to preserve and re-throw the original exception
/// with its stack trace after MAF completes.
module AgentNet.Tests.ExceptionBubblingTests

open System
open NUnit.Framework
open Swensen.Unquote
open AgentNet
open AgentNet.InProcess

[<Test>]
let ``Exception thrown in step bubbles up to caller``() =
    let failingStep (x: int) =
        raise (InvalidOperationException "Step failed!")
        x |> Task.fromResult

    let wf = workflow {
        step failingStep
    }

    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            (wf |> Workflow.InProcess.run 42).GetAwaiter().GetResult() |> ignore)

    ex.Message =! "Step failed!"

[<Test>]
let ``Exception thrown in middle step of multi-step workflow bubbles up``() =
    let step1 (x: int) = x + 1 |> Task.fromResult

    let step2 (x: int) =
        raise (ArgumentException "Bad argument in step 2")
        x |> Task.fromResult

    let step3 (x: int) = x * 10 |> Task.fromResult

    let wf = workflow {
        step step1
        step step2
        step step3
    }

    let ex =
        Assert.Throws<ArgumentException>(fun () ->
            (wf |> Workflow.InProcess.run 1).GetAwaiter().GetResult() |> ignore)

    ex.Message =! "Bad argument in step 2"

[<Test>]
let ``Custom exception type bubbles up from workflow``() =
    let failingStep (_: string) =
        raise (NotSupportedException "Custom failure")
        "" |> Task.fromResult

    let wf = workflow {
        step failingStep
    }

    let ex =
        Assert.Throws<NotSupportedException>(fun () ->
            (wf |> Workflow.InProcess.run "input").GetAwaiter().GetResult() |> ignore)

    ex.Message =! "Custom failure"

[<Test>]
let ``Exception in async step bubbles up from workflow``() =
    let failingAsyncStep (_: int) = task {
        do! System.Threading.Tasks.Task.Delay(10)
        return raise (TimeoutException "Async step timed out")
    }

    let wf = workflow {
        step failingAsyncStep
    }

    let ex =
        Assert.Throws<TimeoutException>(fun () ->
            (wf |> Workflow.InProcess.run 1).GetAwaiter().GetResult() |> ignore)

    ex.Message =! "Async step timed out"
