namespace AgentNet.Durable

open System
open System.Threading.Tasks
open Microsoft.DurableTask
open AgentNet
open AgentNet.Interop

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

        // ============ EXECUTOR SELECTION ============
        // Maps Agent.NET DSL nodes to IExecutor instances.
        // This function is 100% PURE - no durable primitives called here.
        // Executors receive TaskOrchestrationContext at execution time, not during creation.

        /// Wraps an execute function with WorkflowContext creation.
        /// Returns a Func that C# can use directly.
        let private wrapWithContext (execute: obj -> WorkflowContext -> Task<obj>) : Func<obj, Task<obj>> =
            Func<obj, Task<obj>>(fun input ->
                let ctx = WorkflowContext.create()
                execute input ctx)

        /// Extracts and wraps the execute function from a WorkflowStep.
        let rec private extractAndWrapFunc (step: WorkflowStep) : Func<obj, Task<obj>> =
            match step with
            | Step (_, _, execute) -> wrapWithContext execute
            | Route (_, router) -> wrapWithContext router
            | _ -> failwith "Cannot extract execute function from this step type for resilience wrapper"

        /// Pure executor selection - NO durable primitives called here.
        /// Executors receive TaskOrchestrationContext at execution time via ExecuteAsync.
        /// All durable primitive invocation happens inside ExecuteAsync (in C#).
        let rec private selectExecutor<'output>
            (stepIndex: int)
            (step: WorkflowStep)
            : IExecutor =
            match step with
            | Step (durableId, _, execute) ->
                // Wrap with WorkflowContext and pass to C#
                ExecutorFactory.CreateStepExecutor(durableId, stepIndex, wrapWithContext execute)

            | Route (durableId, router) ->
                // Wrap with WorkflowContext and pass to C#
                ExecutorFactory.CreateStepExecutor(durableId, stepIndex, wrapWithContext router)

            | Parallel branches ->
                // Wrap each branch with WorkflowContext and pass to C#
                let branchFns =
                    branches
                    |> List.map (fun (_, exec) -> wrapWithContext exec)
                    |> ResizeArray
                let parallelId = $"Parallel_{stepIndex}_" + (branches |> List.map fst |> String.concat "_")
                ExecutorFactory.CreateParallelExecutor(parallelId, stepIndex, branchFns)

            // Durable operations - durable primitives invoked inside ExecuteAsync
            | AwaitEvent (durableId, eventName) ->
                ExecutorFactory.CreateAwaitEventExecutor<'output>(durableId, eventName, stepIndex)

            | Delay (durableId, duration) ->
                ExecutorFactory.CreateDelayExecutor(durableId, duration, stepIndex)

            // Resilience wrappers
            | WithRetry (inner, maxRetries) ->
                let innerFunc = extractAndWrapFunc inner
                ExecutorFactory.CreateRetryExecutor($"Retry_{stepIndex}", maxRetries, stepIndex, innerFunc)

            | WithTimeout (inner, timeout) ->
                let innerFunc = extractAndWrapFunc inner
                ExecutorFactory.CreateTimeoutExecutor($"Timeout_{stepIndex}", timeout, stepIndex, innerFunc)

            | WithFallback (inner, fallbackId, fallback) ->
                let innerFunc = extractAndWrapFunc inner
                let fallbackFunc = wrapWithContext fallback
                ExecutorFactory.CreateFallbackExecutor($"Fallback_{stepIndex}", fallbackId, stepIndex, innerFunc, fallbackFunc)

        // ============ DURABLE EXECUTION ============

        /// Converts result data to the expected F# output type.
        /// Handles arrays from parallel execution by converting to F# list.
        let private convertToOutput<'output> (data: obj) : 'output =
            // Try direct cast first
            match data with
            | :? 'output as result -> result
            | _ ->
                // Check if we have an array from parallel execution
                // and 'output is an F# list type
                let outputType = typeof<'output>
                if outputType.IsGenericType &&
                   outputType.GetGenericTypeDefinition() = typedefof<_ list> then
                    // 'output is an F# list - convert array to F# list
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

        /// Runs a workflow within a durable orchestration context.
        /// Call this from your [<OrchestrationTrigger>] function.
        /// Executes each step in sequence, passing ctx to each executor's ExecuteAsync.
        let run<'input, 'output>
            (ctx: TaskOrchestrationContext)
            (input: 'input)
            (workflow: WorkflowDef<'input, 'output>)
            : Task<'output> =
            task {
                match workflow.Steps with
                | [] -> return failwith "Workflow must have at least one step"
                | steps ->
                    // Create executors for all steps
                    let executors = steps |> List.mapi (fun i step -> selectExecutor<'output> i step)

                    // Execute each step in sequence, passing ctx at execution time
                    let mutable currentInput: obj = box input
                    for executor in executors do
                        let! result = executor.ExecuteAsync(ctx, currentInput)
                        currentInput <- result

                    // Convert final result to output type
                    return convertToOutput<'output> currentInput
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
