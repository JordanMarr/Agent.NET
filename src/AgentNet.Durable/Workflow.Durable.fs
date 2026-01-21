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
        /// Uses packed steps directly - no compilation to erased types.
        let containsDurableOperations<'input, 'output, 'error> (workflow: WorkflowDef<'input, 'output, 'error>) : bool =
            workflow.TypedSteps |> List.exists PackedTypedStep.isDurableOnly

        /// Validates that a workflow can run in-process (no durable-only operations).
        /// Throws if durable-only operations are detected.
        let validateForInProcess<'input, 'output, 'error> (workflow: WorkflowDef<'input, 'output, 'error>) : unit =
            if containsDurableOperations workflow then
                failwith "Workflow contains durable-only operations (awaitEvent, delayFor). Use Workflow.Durable.run for durable hosting."

        // ============ ACTIVITY REGISTRATION ============

        /// Gets all activities that need to be registered for a workflow.
        /// Returns a list of (activityName, executeFunction) pairs.
        /// Uses packed steps directly - no compilation to erased types.
        let getActivities<'input, 'output, 'error> (workflow: WorkflowDef<'input, 'output, 'error>)
            : (string * (obj -> Task<obj>)) list =
            workflow.TypedSteps
            |> List.collect PackedTypedStep.collectActivities
            |> List.distinctBy fst

        // ============ DURABLE EXECUTION ============
        // Uses packed steps directly with pre-constructed durable executors.
        // No reflection needed - type parameters were captured at pack time.

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
        ///
        /// KEY PHASE 3 CHANGE: Uses packed steps directly with their pre-constructed
        /// durable executors. No compilation to erased types, no reflection for AwaitEvent.
        let run<'input, 'output, 'error>
            (ctx: TaskOrchestrationContext)
            (input: 'input)
            (workflow: WorkflowDef<'input, 'output, 'error>)
            : Task<'output> =
            task {
                let packedSteps = workflow.TypedSteps
                match packedSteps with
                | [] -> return failwith "Workflow must have at least one step"
                | steps ->
                    // Create durable executors directly from packed steps
                    // No reflection needed - CreateDurableExecutor was set up at pack time
                    // with the correct type parameters captured in the closure
                    let executors = steps |> List.mapi (fun i packed -> packed.CreateDurableExecutor i)

                    // Execute each step in sequence, passing ctx at execution time
                    let mutable currentInput: obj = box input
                    for executor in executors do
                        let! result = executor.ExecuteAsync(ctx, currentInput)
                        currentInput <- result

                    // Convert final result to output type
                    return convertToOutput<'output> currentInput
            }

        // AGENTS: DO NOT CHANGE THIS FUNCTION UNLESS EXPLICIT INSTRUCTIONS ARE GIVEN TO DO SO.
        /// Runs a workflow within a durable orchestration context, catching EarlyExitException.
        /// Returns Result<'output, 'error> where Error contains the typed error from tryStep.
        let tryRun<'input, 'output, 'error>
            (ctx: TaskOrchestrationContext)
            (input: 'input)
            (workflow: WorkflowDef<'input, 'output, 'error>)
            : Task<Result<'output, 'error>> =
            let rec runSteps (currentInput: obj) (remaining: IExecutor list) =
                task {
                    match remaining with
                    | [] ->
                        // No more steps → success
                        return Ok (convertToOutput<'output> currentInput)

                    | executor :: rest ->
                        let! result = executor.ExecuteAsync(ctx, currentInput)

                        match result with
                        | :? EarlyExitReturn as early ->
                            // Stop immediately
                            return Error (unbox<'error> early.Error)

                        | output ->
                            // Continue with next step
                            return! runSteps output rest
                }

            task {
                match workflow.TypedSteps with
                | [] ->
                    return failwith "Workflow must have at least one step"

                | steps ->
                    let executors =
                        steps |> List.mapi (fun i packed -> packed.CreateDurableExecutor i)

                    return! runSteps (box input) executors
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
