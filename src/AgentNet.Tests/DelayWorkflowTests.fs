/// Tests for in-process `delayFor`. Under durable hosting the delay is checkpointed around;
/// in-process it is a cooperative Task.Delay that completes and forwards its input unchanged.
module AgentNet.Tests.DelayWorkflowTests

open System
open System.Diagnostics
open System.Threading
open NUnit.Framework
open Swensen.Unquote
open AgentNet
open AgentNet.InProcess

[<Test>]
let ``delayFor waits then forwards input unchanged``() =
    let wf = workflow {
        step (fun (x: int) -> Task.fromResult (x + 1))
        delayFor (TimeSpan.FromMilliseconds 60.0)
        step (fun (x: int) -> Task.fromResult (x * 2))
    }

    let sw = Stopwatch.StartNew()
    let result = (wf |> Workflow.InProcess.run 10).GetAwaiter().GetResult()
    sw.Stop()

    result =! 22                                      // (10 + 1) = 11, delay, * 2 = 22
    test <@ sw.Elapsed.TotalMilliseconds >= 50.0 @>   // genuinely waited (~60ms requested)

[<Test>]
[<Timeout(5000)>]
let ``delayFor observes the cancellation token``() =
    // A long delay that must be aborted by the token rather than waited out.
    let wf = workflow {
        step (fun (x: int) -> Task.fromResult x)
        delayFor (TimeSpan.FromSeconds 30.0)
        step (fun (x: int) -> Task.fromResult x)
    }

    use cts = new CancellationTokenSource()
    cts.CancelAfter(TimeSpan.FromMilliseconds 50.0)

    let sw = Stopwatch.StartNew()
    // The run must fault (not succeed) ...
    raises<exn> <@ (wf |> Workflow.InProcess.runWithCancellation cts.Token 1).GetAwaiter().GetResult() @>
    sw.Stop()
    // ... and must do so promptly, proving the token reached Task.Delay (the [<Timeout>] guards a hang).
    test <@ sw.Elapsed.TotalSeconds < 5.0 @>
