/// Tests for the "awaitEvent works in-process" capability: a workflow that suspends at awaitEvent can
/// be driven to completion in-process by supplying responses via runWithResponses. This is the
/// in-process counterpart to durable resume (no checkpointing required).
module AgentNet.Tests.AwaitEventInProcessTests

open System
open NUnit.Framework
open Swensen.Unquote
open AgentNet
open AgentNet.InProcess

// Public event payload type (awaitEvent requires a public, non-abstract type).
type ApprovalDecision = { Approved: bool; Approver: string }

[<Test>]
let ``awaitEvent completes in-process via runWithResponses``() =
    let wf = workflow {
        step (fun (_req: string) -> Task.fromResult ())          // produce unit (event boundary)
        awaitEvent "Approval" eventOf<ApprovalDecision>           // unit -> ApprovalDecision
        step (fun (d: ApprovalDecision) ->
            Task.fromResult (if d.Approved then $"approved by {d.Approver}" else "rejected"))
    }

    let respond (req: PendingRequest) : obj =
        req.EventName =! "Approval"                              // responder sees the awaited event name
        box { Approved = true; Approver = "alice" }

    let result = (wf |> Workflow.InProcess.runWithResponses respond "trade-123").GetAwaiter().GetResult()

    result =! "approved by alice"

[<Test>]
let ``unit-emitting intermediate step routes to the next step``() =
    // Regression guard for the WorkflowUnit boundary: F# unit boxes to null and MAF drops null
    // messages, so a unit-returning intermediate step must be surfaced as a non-null surrogate.
    let wf = workflow {
        step (fun (_x: int) -> Task.fromResult ())
        step (fun (_u: unit) -> Task.fromResult "done")
    }
    let result = (wf |> Workflow.InProcess.run 5).GetAwaiter().GetResult()
    result =! "done"

[<Test>]
let ``runWithResponses also runs a normal (no awaitEvent) workflow``() =
    let wf = workflow {
        step (fun (x: int) -> Task.fromResult (x + 1))
        step (fun (x: int) -> Task.fromResult (x * 2))
    }

    // No awaited events, so the responder is never invoked.
    let respond (_req: PendingRequest) : obj = failwith "should not be called"

    let result = (wf |> Workflow.InProcess.runWithResponses respond 10).GetAwaiter().GetResult()

    result =! 22
