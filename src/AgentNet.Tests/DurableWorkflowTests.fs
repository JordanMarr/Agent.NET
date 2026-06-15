/// Tests for the durable-operation DSL (awaitEvent, delayFor): construction, in-process behavior,
/// and resilience. (MAF-native checkpoint start/resume is covered by DurableCheckpointTests.)
module AgentNet.Tests.DurableWorkflowTests

open System
open NUnit.Framework
open Swensen.Unquote
open AgentNet
open AgentNet.InProcess

// Domain types for durable workflow tests
type ApprovalDecision = { Approved: bool; Reason: string }

[<Test>]
let ``awaitEvent creates AwaitEvent step in workflow``() =
    // using type witness pattern; awaitEvent requires the preceding step to return unit (event boundary)
    let durableWorkflow = workflow {
        step (fun (x: string) -> () |> Task.fromResult)  // Must return unit before awaitEvent
        awaitEvent "ApprovalDecision" eventOf<ApprovalDecision>
    }

    durableWorkflow.TypedSteps.Length =! 2

[<Test>]
let ``delay creates Delay step in workflow``() =
    let durableWorkflow = workflow {
        step (fun (x: int) -> x * 2 |> Task.fromResult)
        delayFor (TimeSpan.FromHours 1.)
    }

    durableWorkflow.TypedSteps.Length =! 2

[<Test>]
let ``awaitEvent and delay can be combined``() =
    // delayFor preserves the output type, so we need unit before awaitEvent
    let durableWorkflow = workflow {
        step (fun (x: string) -> () |> Task.fromResult)  // Return unit
        delayFor (TimeSpan.FromMinutes 5.)               // Preserves unit
        awaitEvent "HumanReview" eventOf<ApprovalDecision>
    }

    durableWorkflow.TypedSteps.Length =! 3

[<Test>]
let ``runInProcess fails for workflow with awaitEvent``() =
    let durableWorkflow = workflow {
        step (fun (x: string) -> () |> Task.fromResult)  // Must return unit before awaitEvent
        awaitEvent "ApprovalDecision" eventOf<ApprovalDecision>
    }

    // awaitEvent suspends the workflow, which the plain in-process runner cannot complete.
    let ex = Assert.Throws<Exception>(fun () ->
        (durableWorkflow |> Workflow.InProcess.run "test").GetAwaiter().GetResult() |> ignore)
    test <@ ex.Message.Contains("AwaitEvent") @>
    test <@ ex.Message.Contains("suspend") @>

[<Test>]
let ``runInProcess now supports delayFor (runs the delay in-process)``() =
    // delayFor is no longer durable-only: in-process it runs as a cooperative delay and forwards its
    // input unchanged. (DelayWorkflowTests covers timing and cancellation behavior.)
    let durableWorkflow = workflow {
        step (fun (x: int) -> x * 2 |> Task.fromResult)
        delayFor (TimeSpan.FromMilliseconds 10.)
    }

    let result = (durableWorkflow |> Workflow.InProcess.run 5).GetAwaiter().GetResult()
    result =! 10

[<Test>]
let ``awaitEvent type flows to next step``() =
    // event type becomes input for next step
    let sendApprovalEmail (decision: ApprovalDecision) =
        $"Email sent: {decision.Reason}" |> Task.fromResult

    let durableWorkflow = workflow {
        step (fun (x: string) -> () |> Task.fromResult)  // Must return unit before awaitEvent
        awaitEvent "ApprovalDecision" eventOf<ApprovalDecision>
        step sendApprovalEmail
    }

    durableWorkflow.TypedSteps.Length =! 3

[<Test>]
let ``Resilience ops work fine without durable ops via runInProcess``() =
    let mutable attempts = 0
    let unreliable (x: int) =
        attempts <- attempts + 1
        if attempts < 2 then failwith "Fail"
        x * 2 |> Task.fromResult

    let resilientWorkflow = workflow {
        step unreliable
        retry 3
    }

    let result = (resilientWorkflow |> Workflow.InProcess.run 5).GetAwaiter().GetResult()

    result =! 10

/// The compiler rejects workflows where awaitEvent follows a non-unit step (event boundary invariant).
/// Uncomment to verify that the following DOES NOT COMPILE:
// [<Test>]
// let ``awaitEvent rejects non-unit output - THIS SHOULD NOT COMPILE``() =
//     let invalidWorkflow = workflow {
//         step (fun (x: string) -> x.ToUpper() |> Task.fromResult)  // Returns string, not unit
//         awaitEvent "Event" eventOf<ApprovalDecision>  // Should fail to compile!
//     }
//     ()
