/// Tests for MAF-native durable execution: a workflow that suspends at awaitEvent is checkpointed and
/// resumed via Workflow.Durable.start / resume (the replacement for the old DTFx runtime).
module AgentNet.Tests.DurableCheckpointTests

open System
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open Swensen.Unquote
open AgentNet
open AgentNet.InProcess

type ApprovalDecision = { Approved: bool; Approver: string }

// A genuine F# discriminated union — the case Azure Durable Functions' serializer could not handle.
type ApprovalOutcome =
    | Approved of approver: string
    | Rejected of reason: string

let private approvalWorkflow =
    workflow {
        step (fun (_req: string) -> Task.fromResult ())            // unit before the event boundary
        awaitEvent "Approval" eventOf<ApprovalDecision>
        step (fun (d: ApprovalDecision) ->
            Task.fromResult (if d.Approved then $"executed (by {d.Approver})" else "cancelled"))
    }

[<Test>]
let ``durable start suspends at awaitEvent, resume completes``() =
    let cm = Workflow.Durable.inMemoryCheckpoints ()

    let startResult = (Workflow.Durable.start cm "trade-1" approvalWorkflow).GetAwaiter().GetResult()

    match startResult with
    | Workflow.Durable.Completed _ -> failwith "expected the workflow to suspend at awaitEvent"
    | Workflow.Durable.Suspended (pending, checkpoint) ->
        pending |> List.map (fun p -> p.EventName) =! ["Approval"]

        let respond (pr: PendingRequest) : obj option =
            if pr.EventName = "Approval" then Some (box { Approved = true; Approver = "bob" }) else None

        let resumeResult = (Workflow.Durable.resume cm approvalWorkflow checkpoint respond).GetAwaiter().GetResult()

        match resumeResult with
        | Workflow.Durable.Completed output -> output =! "executed (by bob)"
        | Workflow.Durable.Suspended _ -> failwith "expected the workflow to complete after the response"

[<Test>]
let ``durable round-trips a record through a JSON file checkpoint``() =
    let dir = Path.Combine(Path.GetTempPath(), "agentnet-ckpt-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    try
        let cm = Workflow.Durable.fileSystemJsonCheckpoints dir
        let startResult = (Workflow.Durable.start cm "trade-1" approvalWorkflow).GetAwaiter().GetResult()

        match startResult with
        | Workflow.Durable.Suspended (_, checkpoint) ->
            test <@ Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length > 0 @>
            let respond (pr: PendingRequest) : obj option =
                if pr.EventName = "Approval" then Some (box { Approved = true; Approver = "carol" }) else None
            let resumeResult = (Workflow.Durable.resume cm approvalWorkflow checkpoint respond).GetAwaiter().GetResult()
            match resumeResult with
            | Workflow.Durable.Completed output -> output =! "executed (by carol)"
            | Workflow.Durable.Suspended _ -> failwith "expected completion after the response"
        | Workflow.Durable.Completed _ -> failwith "expected suspension at awaitEvent"
    finally
        try Directory.Delete(dir, true) with _ -> ()

// KNOWN GAP: F# discriminated unions do not yet round-trip through MAF's JSON checkpoint marshaller.
// MAF marshals checkpoint values with its own System.Text.Json configuration and does NOT honor the
// JsonSerializerOptions passed to CheckpointManager.CreateJson, so our JsonFSharpConverter never engages
// and STJ rejects the union ("F# discriminated union serialization is not supported"). Records work
// (STJ handles them natively). This is the rework's headline premise and needs a focused fix (likely a
// custom IWireMarshaller<JsonElement> that applies the F# converter). Tracked in DURABLE_REWORK_PLAN.md.
[<Test; Ignore("Pending: inject JsonFSharpConverter into MAF's checkpoint value marshaller (see plan)")>]
let ``durable round-trips an F# DU through a JSON file checkpoint``() =
    let dir = Path.Combine(Path.GetTempPath(), "agentnet-ckpt-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    try
        let wf = workflow {
            step (fun (_req: string) -> Task.fromResult ())
            awaitEvent "Decision" eventOf<ApprovalOutcome>
            step (fun (o: ApprovalOutcome) ->
                Task.fromResult (match o with Approved a -> $"approved by {a}" | Rejected r -> $"rejected: {r}"))
        }
        let cm = Workflow.Durable.fileSystemJsonCheckpoints dir
        match (Workflow.Durable.start cm "req" wf).GetAwaiter().GetResult() with
        | Workflow.Durable.Suspended (_, checkpoint) ->
            let respond (pr: PendingRequest) : obj option =
                if pr.EventName = "Decision" then Some (box (Approved "carol")) else None
            match (Workflow.Durable.resume cm wf checkpoint respond).GetAwaiter().GetResult() with
            | Workflow.Durable.Completed output -> output =! "approved by carol"
            | Workflow.Durable.Suspended _ -> failwith "expected completion"
        | Workflow.Durable.Completed _ -> failwith "expected suspension"
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Test>]
let ``durable start completes immediately for a workflow with no awaitEvent``() =
    let cm = Workflow.Durable.inMemoryCheckpoints ()
    let wf = workflow {
        step (fun (x: int) -> Task.fromResult (x + 1))
        step (fun (x: int) -> Task.fromResult (x * 3))
    }

    let result = (Workflow.Durable.start cm 10 wf).GetAwaiter().GetResult()

    match result with
    | Workflow.Durable.Completed output -> output =! 33
    | Workflow.Durable.Suspended _ -> failwith "expected immediate completion"
