// ============================================================================
// WARNING: Declarative Layer Only
// ----------------------------------------------------------------------------
// This file defines WorkflowBuilder custom operations. These operations must be
// 100% declarative. They may NOT:
//   - call DurableTask APIs
//   - capture TaskOrchestrationContext
//   - construct lambdas that will run at execution time
//   - create or await Tasks
//   - perform I/O or side effects
//
// All durable primitives MUST be implemented in the execution layer
// (toMAFExecutor), NOT here.
// ============================================================================

/// Durable workflow extensions for WorkflowBuilder.
/// These operations require DurableTask runtime - will fail with runInProcess.
/// Users must `open AgentNet.Durable` to access these extensions.
[<AutoOpen>]
module AgentNet.Durable.WorkflowExtensions

open System
open AgentNet

/// Type witness helper for awaitEvent.
/// Usage: awaitEvent "ApprovalEvent" eventOf<ApprovalDecision>
let eventOf<'T> : 'T = Unchecked.defaultof<'T>

type WorkflowBuilder with
    /// Waits for an external event with the given name and expected type.
    /// The workflow is checkpointed and suspended until the event arrives.
    /// The received event becomes the input for the next step.
    /// Usage: awaitEvent "ApprovalEvent" eventOf<ApprovalDecision>
    /// This operation requires DurableTask runtime - will fail with runInProcess.
    ///
    /// EVENT BOUNDARY INVARIANT (per DESIGN_CE_TYPE_THREADING.md):
    /// Input state MUST be WorkflowState<'input, unit>.
    /// This enforces that all data needed after the event must be stored in context
    /// before the event boundary. The step before awaitEvent must return unit.
    [<CustomOperation("awaitEvent")>]
    member _.AwaitEvent(state: WorkflowState<'input, unit>, eventName: string, _witness: 'event) : WorkflowState<'input, 'event> =
        // Early validation - fail fast at workflow construction time
        if String.IsNullOrWhiteSpace(eventName) then
            failwith "awaitEvent: event name cannot be null or empty"

        let eventType = typeof<'event>
        if not (eventType.IsPublic || eventType.IsNestedPublic) then
            failwith $"awaitEvent: event type '{eventType.FullName}' must be public"

        if eventType.IsAbstract then
            failwith $"awaitEvent: event type '{eventType.FullName}' cannot be abstract"

        let durableId = $"AwaitEvent_{eventName}_{eventType.Name}"

        // Store metadata only - durable primitive is invoked in toMAFExecutor
        { Name = state.Name; Steps = state.Steps @ [AwaitEvent(durableId, eventName)] }

    /// Delays the workflow for the specified duration.
    /// The workflow is checkpointed and suspended during the delay.
    /// This operation requires DurableTask runtime - will fail with runInProcess.
    [<CustomOperation("delayFor")>]
    member _.DelayFor(state: WorkflowState<'input, 'output>, duration: TimeSpan) : WorkflowState<'input, 'output> =
        let durableId = $"Delay_{int duration.TotalMilliseconds}ms"
        { Name = state.Name; Steps = state.Steps @ [Delay(durableId, duration)] }
