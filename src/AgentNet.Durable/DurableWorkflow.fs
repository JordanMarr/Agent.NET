namespace AgentNet.Durable

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.DurableTask
open AgentNet

/// Durable workflow extensions for WorkflowBuilder.
/// These operations require DurableTask runtime - will fail with runInProcess.
/// Users must `open AgentNet.Durable` to access these extensions.
[<AutoOpen>]
module DurableWorkflowExtensions =

    /// Type witness helper for awaitEvent.
    /// Usage: awaitEvent "ApprovalEvent" eventOf<ApprovalDecision>
    let eventOf<'T> : 'T = Unchecked.defaultof<'T>

    type WorkflowBuilder with
        /// Waits for an external event with the given name and expected type.
        /// The workflow is checkpointed and suspended until the event arrives.
        /// The received event becomes the input for the next step.
        /// Usage: awaitEvent "ApprovalEvent" eventOf<ApprovalDecision>
        /// This operation requires DurableTask runtime - will fail with runInProcess.
        [<CustomOperation("awaitEvent")>]
        member _.AwaitEvent(state: WorkflowState<'input, _>, eventName: string, _witness: 'T) : WorkflowState<'input, 'T> =
            let durableId = $"AwaitEvent_{eventName}_{typeof<'T>.Name}"
            { Name = state.Name; Steps = state.Steps @ [AwaitEvent(durableId, eventName, typeof<'T>)] }

        /// Delays the workflow for the specified duration.
        /// The workflow is checkpointed and suspended during the delay.
        /// This operation requires DurableTask runtime - will fail with runInProcess.
        [<CustomOperation("delayFor")>]
        member _.DelayFor(state: WorkflowState<'input, 'output>, duration: TimeSpan) : WorkflowState<'input, 'output> =
            let durableId = $"Delay_{int duration.TotalMilliseconds}ms"
            { Name = state.Name; Steps = state.Steps @ [Delay(durableId, duration)] }


/// Functions for compiling and running durable workflows.
/// These require Azure Durable Functions / DurableTask runtime.
[<RequireQualifiedAccess>]
module DurableWorkflow =

    /// Checks if a workflow contains durable-only operations.
    let containsDurableOperations (workflow: WorkflowDef<'input, 'output>) : bool =
        let rec hasDurableOps step =
            match step with
            | AwaitEvent _ -> true
            | Delay _ -> true
            | WithRetry (inner, _) -> hasDurableOps inner
            | WithTimeout (inner, _) -> hasDurableOps inner
            | WithFallback (inner, _, _) -> hasDurableOps inner
            | _ -> false
        workflow.Steps |> List.exists hasDurableOps

    /// Validates that a workflow can run in-process (no durable-only operations).
    /// Throws if durable-only operations are detected.
    let validateForInProcess (workflow: WorkflowDef<'input, 'output>) : unit =
        if containsDurableOperations workflow then
            failwith "Workflow contains durable-only operations (awaitEvent, delayFor). Use DurableWorkflow.run for durable hosting."

    // ============ DURABLE RUNNER ============
    // Maps Agent.NET DSL nodes to DTFx primitives.
    // DTFx handles orchestration, replay, determinism, and durable state.

    /// Interprets a single WorkflowStep within a durable orchestration context.
    /// Maps Agent.NET DSL nodes to DTFx primitives.
    let rec private runStep
        (ctx: TaskOrchestrationContext)
        (input: obj)
        (step: WorkflowStep)
        : Task<obj> =
        task {
            match step with
            | Step (durableId, _, _) ->
                // Step functions are registered as activities
                return! ctx.CallActivityAsync<obj>(durableId, input)

            | Route (durableId, _) ->
                // Router is registered as an activity
                return! ctx.CallActivityAsync<obj>(durableId, input)

            | Parallel branches ->
                // Run all branches concurrently as activities
                let! results =
                    branches
                    |> List.map (fun (durableId, _) ->
                        ctx.CallActivityAsync<obj>(durableId, input))
                    |> Task.WhenAll
                return (results |> Array.toList) :> obj

            | AwaitEvent (_, eventName, eventType) ->
                // Call WaitForExternalEvent<T> via reflection for correct type
                let method =
                    typeof<TaskOrchestrationContext>
                        .GetMethod("WaitForExternalEvent", [| typeof<string> |])
                        .MakeGenericMethod(eventType)
                let resultTask = method.Invoke(ctx, [| eventName |]) :?> Task
                do! resultTask
                // Get result via reflection from Task<T>
                let resultProp = resultTask.GetType().GetProperty("Result")
                return resultProp.GetValue(resultTask)

            | Delay (_, duration) ->
                let fireAt = ctx.CurrentUtcDateTime.Add(duration)
                do! ctx.CreateTimer(fireAt, CancellationToken.None)
                return input  // Pass through unchanged

            | WithRetry (inner, maxRetries) ->
                // For Step/Route, use CallActivityAsync with retry options
                match inner with
                | Step (durableId, _, _) ->
                    let options = TaskOptions.FromRetryPolicy(
                        RetryPolicy(maxRetries + 1, TimeSpan.FromSeconds(1.)))
                    return! ctx.CallActivityAsync<obj>(durableId, input, options)
                | Route (durableId, _) ->
                    let options = TaskOptions.FromRetryPolicy(
                        RetryPolicy(maxRetries + 1, TimeSpan.FromSeconds(1.)))
                    return! ctx.CallActivityAsync<obj>(durableId, input, options)
                | _ ->
                    // Retry loop for non-activity steps
                    let rec retry attempt =
                        task {
                            try
                                return! runStep ctx input inner
                            with _ when attempt < maxRetries ->
                                return! retry (attempt + 1)
                        }
                    return! retry 0

            | WithTimeout (inner, timeout) ->
                let fireAt = ctx.CurrentUtcDateTime.Add(timeout)
                let timerTask = ctx.CreateTimer(fireAt, CancellationToken.None)
                let stepTask = runStep ctx input inner
                let! winner = Task.WhenAny(stepTask, timerTask)
                if obj.ReferenceEquals(winner, timerTask) then
                    return raise (TimeoutException($"Step timed out after {timeout}"))
                else
                    return! stepTask

            | WithFallback (inner, fallbackId, _) ->
                try
                    return! runStep ctx input inner
                with _ ->
                    return! ctx.CallActivityAsync<obj>(fallbackId, input)
        }

    /// Runs a workflow within a durable orchestration context.
    /// Call this from your [<OrchestrationTrigger>] function.
    let run<'input, 'output>
        (ctx: TaskOrchestrationContext)
        (input: 'input)
        (workflow: WorkflowDef<'input, 'output>)
        : Task<'output> =
        task {
            let mutable current: obj = input :> obj
            for step in workflow.Steps do
                let! result = runStep ctx current step
                current <- result
            return current :?> 'output
        }

    // ============ ACTIVITY REGISTRATION ============

    /// Collects all step functions that need to be registered as activities.
    let rec private collectActivities (step: WorkflowStep) : (string * (obj -> Task<obj>)) list =
        match step with
        | Step (durableId, _, execute) ->
            [(durableId, fun input -> execute input (WorkflowContext.create()))]
        | Route (durableId, router) ->
            [(durableId, fun input -> router input (WorkflowContext.create()))]
        | Parallel branches ->
            branches |> List.map (fun (id, exec) ->
                (id, fun input -> exec input (WorkflowContext.create())))
        | AwaitEvent _ | Delay _ ->
            []  // Not activities - handled by DTFx primitives
        | WithRetry (inner, _) | WithTimeout (inner, _) ->
            collectActivities inner
        | WithFallback (inner, fallbackId, fallbackFn) ->
            let innerActivities = collectActivities inner
            let fallback = (fallbackId, fun input -> fallbackFn input (WorkflowContext.create()))
            innerActivities @ [fallback]

    /// Gets all activities that need to be registered for a workflow.
    /// Returns a list of (activityName, executeFunction) pairs.
    let getActivities<'input, 'output> (workflow: WorkflowDef<'input, 'output>)
        : (string * (obj -> Task<obj>)) list =
        workflow.Steps
        |> List.collect collectActivities
        |> List.distinctBy fst
