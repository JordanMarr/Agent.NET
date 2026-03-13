namespace AgentNet.InProcess

open System
open System.Threading.Tasks
open AgentNet
open AgentNet.Interop

// Type aliases to avoid conflicts between AgentNet and MAF
// NOTE: Don't open Microsoft.Agents.AI.Workflows to avoid Executor<,> conflict
type MAFExecutor = Microsoft.Agents.AI.Workflows.Executor
type MAFWorkflow = Microsoft.Agents.AI.Workflows.Workflow
type MAFWorkflowBuilder = Microsoft.Agents.AI.Workflows.WorkflowBuilder
type MAFInProcessExecution = Microsoft.Agents.AI.Workflows.InProcessExecution
type MAFWorkflowOutputEvent = Microsoft.Agents.AI.Workflows.WorkflowOutputEvent

/// Functions for executing workflows in-process
[<RequireQualifiedAccess>]
module Workflow =

    // ============ MAF COMPILATION ============

    /// Converts a PackedTypedStep to a MAF Executor.
    /// The stepIndex is used to ensure unique executor IDs within a workflow.
    let private packedStepToMAFExecutor (stepIndex: int) (packed: PackedTypedStep) : MAFExecutor =
        match packed.Kind with
        | DurableAwaitEvent eventName ->
            failwith $"AwaitEvent '{eventName}' cannot be compiled for in-process execution. Use Workflow.Durable.run instead."
        | DurableDelay duration ->
            failwith $"Delay ({duration}) cannot be compiled for in-process execution. Use Workflow.Durable.run instead."
        | Regular | Resilience _ ->
            // All regular steps and resilience wrappers can use the ExecuteInProcess function
            let executorId = $"{packed.DurableId}_{stepIndex}"
            let fn = Func<obj, Task<obj>>(fun input ->
                let ctx = WorkflowContext.create()
                packed.ExecuteInProcess input ctx)
            Interop.ExecutorFactory.CreateStep(executorId, fn, packed.OutputType)

    /// Like packedStepToMAFExecutor but creates the WorkflowContext with the given CancellationToken.
    let private packedStepToMAFExecutorWithCT (ct: System.Threading.CancellationToken) (stepIndex: int) (packed: PackedTypedStep) : MAFExecutor =
        match packed.Kind with
        | DurableAwaitEvent eventName ->
            failwith $"AwaitEvent '{eventName}' cannot be compiled for in-process execution. Use Workflow.Durable.run instead."
        | DurableDelay duration ->
            failwith $"Delay ({duration}) cannot be compiled for in-process execution. Use Workflow.Durable.run instead."
        | Regular | Resilience _ ->
            let executorId = $"{packed.DurableId}_{stepIndex}"
            let fn = Func<obj, Task<obj>>(fun input ->
                let ctx = WorkflowContext.create() |> WorkflowContext.withCancellation ct
                packed.ExecuteInProcess input ctx)
            Interop.ExecutorFactory.CreateStep(executorId, fn, packed.OutputType)

    /// Like toMAF but seeds each step's WorkflowContext with the given CancellationToken.
    let internal toMAFWithCancellation<'input, 'output, 'error> (ct: System.Threading.CancellationToken) (workflow: WorkflowDef<'input, 'output, 'error>) : MAFWorkflow =
        let name = workflow.Name |> Option.defaultValue "Workflow"
        let packedSteps = workflow.TypedSteps
        match packedSteps with
        | [] -> failwith "Workflow must have at least one step"
        | steps ->
            let executors = steps |> List.mapi (packedStepToMAFExecutorWithCT ct)
            match executors with
            | [] -> failwith "Workflow must have at least one step"
            | firstExecutor :: restExecutors ->
                let mutable builder = MAFWorkflowBuilder(firstExecutor).WithName(name)
                let mutable prev = firstExecutor
                for exec in restExecutors do
                    builder <- builder.AddEdge(prev, exec)
                    prev <- exec
                builder <- builder.WithOutputFrom(prev)
                builder.Build()

    /// Compiles a workflow definition to MAF Workflow using WorkflowBuilder.
    /// Returns a Workflow that can be executed with InProcessExecution.RunAsync.
    /// If no name is set, uses "Workflow" as the default name.
    let internal toMAF<'input, 'output, 'error> (workflow: WorkflowDef<'input, 'output, 'error>) : MAFWorkflow =
        let name = workflow.Name |> Option.defaultValue "Workflow"
        // Use packed steps directly (no compilation to erased types)
        let packedSteps = workflow.TypedSteps
        match packedSteps with
        | [] -> failwith "Workflow must have at least one step"
        | steps ->
            // Create executors for all packed steps with unique indices
            let executors = steps |> List.mapi packedStepToMAFExecutor

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

    /// In-process workflow execution using MAF InProcessExecution.
    /// Use this for testing, simple scenarios, or when you don't need durable suspension.
    module InProcess =

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
                        // This works because F# list is covariant for reference types
                        box converted :?> 'output
                    | _ ->
                        data :?> 'output
                else
                    data :?> 'output

        /// Runs a workflow via MAF InProcessExecution.
        /// The workflow is compiled to MAF format and executed in-process.
        /// DO NOT CHANGE THIS FUNCTION UNLESS EXPLICIT INSTRUCTIONS ARE GIVEN.
        let run<'input, 'output, 'error> (input: 'input) (workflow: WorkflowDef<'input, 'output, 'error>) : Task<'output> =
            task {
                // Compile to MAF workflow
                let mafWorkflow = toMAF workflow

                // Run via Lockstep InProcessExecution (runs all SuperSteps synchronously)
                let! run = MAFInProcessExecution.Lockstep.RunAsync(mafWorkflow, input :> obj, null, System.Threading.CancellationToken.None)

                // Find the WorkflowOutputEvent - the definitive workflow output
                let mutable lastResult: obj option = None
                for evt in run.NewEvents do
                    match evt with
                    | :? MAFWorkflowOutputEvent as output ->
                        lastResult <- Some output.Data
                    | _ -> ()

                match lastResult with
                | Some data -> return convertToOutput<'output> data
                | None -> return failwith "Workflow did not produce output. No WorkflowOutputEvent found."
            }

        /// Runs a workflow with a CancellationToken that flows into every step's WorkflowContext.
        /// Use this to enable cooperative cancellation from an external source (e.g., user-triggered, host shutdown).
        /// Steps and Polly policies receive the token via ctx.CancellationToken.
        let runWithCancellation<'input, 'output, 'error> (ct: System.Threading.CancellationToken) (input: 'input) (workflow: WorkflowDef<'input, 'output, 'error>) : Task<'output> =
            task {
                let mafWorkflow = toMAFWithCancellation ct workflow

                let! run = MAFInProcessExecution.Lockstep.RunAsync(mafWorkflow, input :> obj, null, ct)

                let mutable lastResult: obj option = None
                for evt in run.NewEvents do
                    match evt with
                    | :? MAFWorkflowOutputEvent as output ->
                        lastResult <- Some output.Data
                    | _ -> ()

                match lastResult with
                | Some data -> return convertToOutput<'output> data
                | None -> return failwith "Workflow did not produce output. No WorkflowOutputEvent found."
            }

        // AGENTS: DO NOT CHANGE THIS FUNCTION UNLESS EXPLICIT INSTRUCTIONS ARE GIVEN TO DO SO.
        /// Runs a workflow via MAF InProcessExecution, catching EarlyExitException.
        /// Returns Result<'output, 'error> where Error contains the typed error from tryStep.
        let tryRun<'input, 'output, 'error> (input: 'input) (workflow: WorkflowDef<'input, 'output, 'error>) : Task<Result<'output, 'error>> =
            task {
                let mafWorkflow = toMAF workflow
                let! run = MAFInProcessExecution.Lockstep.RunAsync(mafWorkflow, input :> obj, null, System.Threading.CancellationToken.None)

                let mutable completed = None
                let mutable earlyExit = None

                for evt in run.NewEvents do
                    match evt with
                    | :? MAFWorkflowOutputEvent as output ->
                        completed <- Some output.Data

                    | :? ExecutorEarlyExitEvent as early ->
                        earlyExit <- Some early.Error

                    | _ -> ()

                match completed, earlyExit with
                | _, Some error ->
                    return Error (unbox<'error> error)

                | Some data, _ ->
                    return Ok (convertToOutput<'output> data)

                | None, None ->
                    return failwith "Workflow terminated without success or early exit."

            }

        /// Converts a workflow to an executor (enables workflow composition).
        /// Uses MAF InProcessExecution to run the workflow.
        let toExecutor<'input, 'output, 'error> (name: string) (workflow: WorkflowDef<'input, 'output, 'error>) : Executor<'input, 'output> =
            {
                Name = name
                Execute = fun input _ -> run input workflow
            }
