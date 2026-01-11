namespace AgentNet.Durable

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.DurableTask
open AgentNet
open AgentNet.Interop

// Type aliases to avoid conflicts between AgentNet and MAF
type MAFExecutor = Microsoft.Agents.AI.Workflows.Executor
type MAFWorkflow = Microsoft.Agents.AI.Workflows.Workflow
type MAFWorkflowBuilder = Microsoft.Agents.AI.Workflows.WorkflowBuilder
type MAFInProcessExecution = Microsoft.Agents.AI.Workflows.InProcessExecution
type MAFExecutorCompletedEvent = Microsoft.Agents.AI.Workflows.ExecutorCompletedEvent

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
            // Early validation - fail fast at workflow construction time
            if String.IsNullOrWhiteSpace(eventName) then
                failwith "awaitEvent: event name cannot be null or empty"

            let eventType = typeof<'T>
            if not (eventType.IsPublic || eventType.IsNestedPublic) then
                failwith $"awaitEvent: event type '{eventType.FullName}' must be public"

            if eventType.IsAbstract then
                failwith $"awaitEvent: event type '{eventType.FullName}' cannot be abstract"

            let durableId = $"AwaitEvent_{eventName}_{eventType.Name}"

            // Produce the durable primitive directly - no awaiting, no wrapping
            // This allows DurableTask to suspend/resume the orchestrator correctly
            let invoke = fun (ctxObj: obj) ->
                let ctx = ctxObj :?> TaskOrchestrationContext
                ctx.WaitForExternalEvent<'T>(eventName) :> Task

            { Name = state.Name; Steps = state.Steps @ [AwaitEvent(durableId, invoke)] }

        /// Delays the workflow for the specified duration.
        /// The workflow is checkpointed and suspended during the delay.
        /// This operation requires DurableTask runtime - will fail with runInProcess.
        [<CustomOperation("delayFor")>]
        member _.DelayFor(state: WorkflowState<'input, 'output>, duration: TimeSpan) : WorkflowState<'input, 'output> =
            let durableId = $"Delay_{int duration.TotalMilliseconds}ms"
            { Name = state.Name; Steps = state.Steps @ [Delay(durableId, duration)] }


/// Functions for compiling and running durable workflows.
/// These require Azure Durable Functions / DurableTask runtime.
module Workflow =

    module Durable =

        // ============ UTILITY FUNCTIONS ============

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
                failwith "Workflow contains durable-only operations (awaitEvent, delayFor). Use Workflow.Durable.run for durable hosting."

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

        // ============ MAF COMPILATION FOR DURABLE EXECUTION ============
        // Maps Agent.NET DSL nodes to MAF executors with DTFx primitives for durable operations.
        // MAF handles orchestration; DTFx provides durable suspension (events, timers).

        /// Converts an AgentNet WorkflowStep to a MAF Executor within a durable orchestration context.
        /// Steps are pure .NET functions; durable operations call DTFx primitives.
        let rec private toMAFExecutor
            (ctx: TaskOrchestrationContext)
            (stepIndex: int)
            (step: WorkflowStep)
            : MAFExecutor =
            match step with
            | Step (durableId, _, execute) ->
                // Pure .NET function - executed directly by MAF
                let executorId = $"{durableId}_{stepIndex}"
                let fn = Func<obj, Task<obj>>(fun input ->
                    let workflowCtx = WorkflowContext.create()
                    execute input workflowCtx)
                ExecutorFactory.CreateStep(executorId, fn)

            | Route (durableId, router) ->
                // Router selects and executes the appropriate branch - pure .NET
                let executorId = $"{durableId}_{stepIndex}"
                let fn = Func<obj, Task<obj>>(fun input ->
                    let workflowCtx = WorkflowContext.create()
                    router input workflowCtx)
                ExecutorFactory.CreateStep(executorId, fn)

            | Parallel branches ->
                // Create parallel executor from branches - pure .NET functions
                let branchFns =
                    branches
                    |> List.map (fun (id, exec) ->
                        Func<obj, Task<obj>>(fun input ->
                            let workflowCtx = WorkflowContext.create()
                            exec input workflowCtx))
                    |> ResizeArray
                let parallelId = $"Parallel_{stepIndex}_" + (branches |> List.map fst |> String.concat "_")
                ExecutorFactory.CreateParallel(parallelId, branchFns)

            // Durable operations - return the durable primitive directly (no awaiting)
            | AwaitEvent (durableId, invoke) ->
                let executorId = $"{durableId}_{stepIndex}"
                // Return the durable primitive directly to DTFx - no wrapping, no awaiting
                let fn = Func<obj, Task<obj>>(fun _ ->
                    // invoke returns Task (the durable primitive)
                    // We need to convert to Task<obj> for MAF, but without awaiting
                    let durableTask = invoke ctx
                    // Create a continuation that extracts the result after DTFx resumes
                    durableTask.ContinueWith(fun (t: Task) ->
                        // The original task is Task<'T>, get the result
                        let resultProp = t.GetType().GetProperty("Result")
                        if resultProp <> null then
                            resultProp.GetValue(t)
                        else
                            null))
                ExecutorFactory.CreateStep(executorId, fn)

            | Delay (durableId, duration) ->
                let executorId = $"{durableId}_{stepIndex}"
                let fn = Func<obj, Task<obj>>(fun input -> task {
                    let fireAt = ctx.CurrentUtcDateTime.Add(duration)
                    do! ctx.CreateTimer(fireAt, CancellationToken.None)
                    return input  // Pass through unchanged
                })
                ExecutorFactory.CreateStep(executorId, fn)

            // Resilience wrappers
            | WithRetry (inner, maxRetries) ->
                let executorId = $"WithRetry_{stepIndex}_{maxRetries}"
                let fn = Func<obj, Task<obj>>(fun input ->
                    let workflowCtx = WorkflowContext.create()
                    let rec retry attempt =
                        task {
                            try
                                return! executeStep inner input workflowCtx
                            with ex when attempt < maxRetries ->
                                return! retry (attempt + 1)
                        }
                    retry 0)
                ExecutorFactory.CreateStep(executorId, fn)

            | WithTimeout (inner, timeout) ->
                let executorId = $"WithTimeout_{stepIndex}_{int timeout.TotalMilliseconds}ms"
                let fn = Func<obj, Task<obj>>(fun input -> task {
                    // Use durable timer for timeout
                    let fireAt = ctx.CurrentUtcDateTime.Add(timeout)
                    let timerTask = ctx.CreateTimer(fireAt, CancellationToken.None)
                    let workflowCtx = WorkflowContext.create()
                    let stepTask = executeStep inner input workflowCtx
                    let! winner = Task.WhenAny(stepTask, timerTask)
                    if obj.ReferenceEquals(winner, timerTask) then
                        return raise (TimeoutException($"Step timed out after {timeout}"))
                    else
                        return! stepTask
                })
                ExecutorFactory.CreateStep(executorId, fn)

            | WithFallback (inner, fallbackId, fallback) ->
                let executorId = $"WithFallback_{stepIndex}_{fallbackId}"
                let fn = Func<obj, Task<obj>>(fun input -> task {
                    let workflowCtx = WorkflowContext.create()
                    try
                        return! executeStep inner input workflowCtx
                    with _ ->
                        return! fallback input workflowCtx
                })
                ExecutorFactory.CreateStep(executorId, fn)

        /// Compiles a workflow definition to MAF Workflow for durable execution.
        /// The TaskOrchestrationContext is captured in closures for durable operations.
        /// Returns a Workflow that can be executed with InProcessExecution.RunAsync.
        let toMAF<'input, 'output>
            (ctx: TaskOrchestrationContext)
            (workflow: WorkflowDef<'input, 'output>)
            : MAFWorkflow =
            let name = workflow.Name |> Option.defaultValue "Workflow"
            match workflow.Steps with
            | [] -> failwith "Workflow must have at least one step"
            | steps ->
                // Create executors for all steps with unique indices
                let executors = steps |> List.mapi (fun i step -> toMAFExecutor ctx i step)

                match executors with
                | [] -> failwith "Workflow must have at least one step"
                | firstExecutor :: restExecutors ->
                    // Build workflow using MAFWorkflowBuilder
                    let mutable builder = MAFWorkflowBuilder(firstExecutor).WithName(name)

                    // Add edges between consecutive executors
                    let mutable prev = firstExecutor
                    for exec in restExecutors do
                        builder <- builder.AddEdge(prev, exec)
                        prev <- exec

                    // Mark the last executor as output
                    builder <- builder.WithOutputFrom(prev)

                    // Build and return the workflow
                    builder.Build()

        // ============ MAF IN-PROCESS EXECUTION ============

        /// Converts MAF result data to the expected F# output type.
        /// Handles List<object> from parallel execution by converting to F# list.
        let private convertToOutput<'output> (data: obj) : 'output =
            // Try direct cast first
            match data with
            | :? 'output as result -> result
            | _ ->
                // Check if we have a List<object> from parallel execution
                // and 'output is an F# list type
                let outputType = typeof<'output>
                if outputType.IsGenericType &&
                   outputType.GetGenericTypeDefinition() = typedefof<_ list> then
                    // 'output is an F# list - convert List<object> to F# list
                    match data with
                    | :? System.Collections.IList as objList ->
                        // Convert to F# list by unboxing each element
                        let converted =
                            objList
                            |> Seq.cast<obj>
                            |> Seq.toList
                        // Box as obj list, then cast to 'output
                        box converted :?> 'output
                    | _ ->
                        data :?> 'output
                else
                    data :?> 'output

        /// Executes a single workflow step within the durable orchestration context.
        /// Uses DTFx primitives directly for durable operations.
        let rec private executeStepDurable
            (ctx: TaskOrchestrationContext)
            (input: obj)
            (step: WorkflowStep)
            : Task<obj> =
            match step with
            | Step (_, _, execute) ->
                // Regular step - execute directly (pure .NET function)
                let workflowCtx = WorkflowContext.create()
                execute input workflowCtx

            | Route (_, router) ->
                // Router - execute directly (pure .NET function)
                let workflowCtx = WorkflowContext.create()
                router input workflowCtx

            | Parallel branches ->
                // Execute branches in parallel
                task {
                    let tasks =
                        branches
                        |> List.map (fun (_, exec) ->
                            let workflowCtx = WorkflowContext.create()
                            exec input workflowCtx)
                    let! results = Task.WhenAll(tasks)
                    return (results |> Array.toList) :> obj
                }

            | AwaitEvent (_, invoke) ->
                // Durable operation - return the primitive directly
                // invoke returns Task<'T> (as Task), we need to get the result
                task {
                    let durableTask = invoke ctx
                    do! durableTask
                    // Extract the result from the completed Task<'T>
                    let resultProp = durableTask.GetType().GetProperty("Result")
                    return resultProp.GetValue(durableTask)
                }

            | Delay (_, duration) ->
                // Durable timer
                task {
                    let fireAt = ctx.CurrentUtcDateTime.Add(duration)
                    do! ctx.CreateTimer(fireAt, CancellationToken.None)
                    return input  // Pass through unchanged
                }

            | WithRetry (inner, maxRetries) ->
                let workflowCtx = WorkflowContext.create()
                let rec retry attempt =
                    task {
                        try
                            return! executeStep inner input workflowCtx
                        with _ when attempt < maxRetries ->
                            return! retry (attempt + 1)
                    }
                retry 0

            | WithTimeout (inner, timeout) ->
                task {
                    let fireAt = ctx.CurrentUtcDateTime.Add(timeout)
                    let timerTask = ctx.CreateTimer(fireAt, CancellationToken.None)
                    let workflowCtx = WorkflowContext.create()
                    let stepTask = executeStep inner input workflowCtx
                    let! winner = Task.WhenAny(stepTask, timerTask)
                    if obj.ReferenceEquals(winner, timerTask) then
                        return raise (TimeoutException($"Step timed out after {timeout}"))
                    else
                        return! stepTask
                }

            | WithFallback (inner, _, fallback) ->
                task {
                    let workflowCtx = WorkflowContext.create()
                    try
                        return! executeStep inner input workflowCtx
                    with _ ->
                        return! fallback input workflowCtx
                }

        /// Runs a workflow within a durable orchestration context.
        /// Call this from your [<OrchestrationTrigger>] function.
        /// Executes steps directly using DTFx primitives for durable operations.
        let run<'input, 'output>
            (ctx: TaskOrchestrationContext)
            (input: 'input)
            (workflow: WorkflowDef<'input, 'output>)
            : Task<'output> =
            task {
                // Execute steps sequentially, threading output to input
                let mutable current: obj = input :> obj

                for step in workflow.Steps do
                    let! result = executeStepDurable ctx current step
                    current <- result

                return current :?> 'output
            }


// ============ BACKWARD COMPATIBILITY ALIAS ============
// For existing code that uses DurableWorkflow.xxx

/// Backward compatibility alias for Workflow.Durable module.
/// Existing code using DurableWorkflow.containsDurableOperations etc. will continue to work.
[<RequireQualifiedAccess>]
module DurableWorkflow =

    /// Checks if a workflow contains durable-only operations.
    let containsDurableOperations workflow = Workflow.Durable.containsDurableOperations workflow

    /// Validates that a workflow can run in-process (no durable-only operations).
    let validateForInProcess workflow = Workflow.Durable.validateForInProcess workflow

    /// Gets all activities that need to be registered for a workflow.
    let getActivities workflow = Workflow.Durable.getActivities workflow

    /// Runs a workflow within a durable orchestration context.
    let run ctx input workflow = Workflow.Durable.run ctx input workflow
