namespace AgentNet.InProcess

open System
open System.Runtime.ExceptionServices
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
    /// `makeCtx` produces the WorkflowContext seeded for this run (cancellation token + DI services).
    /// The stepIndex is used to ensure unique executor IDs within a workflow.
    let private packedStepToMAFExecutor (makeCtx: unit -> WorkflowContext) (stepIndex: int) (packed: PackedTypedStep) : MAFExecutor =
        match packed.Kind with
        | DurableAwaitEvent eventName ->
            failwith $"AwaitEvent '{eventName}' cannot be compiled for in-process execution. Use Workflow.Durable.run instead."
        | DurableDelay duration ->
            failwith $"Delay ({duration}) cannot be compiled for in-process execution. Use Workflow.Durable.run instead."
        | Regular | Resilience _ ->
            // All regular steps and resilience wrappers can use the ExecuteInProcess function
            let executorId = $"{packed.DurableId}_{stepIndex}"
            let fn = Func<obj, Task<obj>>(fun input ->
                let ctx = makeCtx ()
                packed.ExecuteInProcess input ctx)
            Interop.ExecutorFactory.CreateStep(executorId, fn, packed.OutputType)

    /// Core compilation: builds a MAF Workflow from packed steps via WorkflowBuilder, seeding each
    /// step's WorkflowContext with `makeCtx` (cancellation token + DI services).
    let internal toMAFCore<'input, 'output, 'error> (makeCtx: unit -> WorkflowContext) (workflow: WorkflowDef<'input, 'output, 'error>) : MAFWorkflow =
        let name = workflow.Name |> Option.defaultValue "Workflow"
        match workflow.TypedSteps with
        | [] -> failwith "Workflow must have at least one step"
        | steps ->
            let executors = steps |> List.mapi (packedStepToMAFExecutor makeCtx)
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

    /// Like toMAF but seeds each step's WorkflowContext with the given CancellationToken.
    let internal toMAFWithCancellation<'input, 'output, 'error> (ct: System.Threading.CancellationToken) (workflow: WorkflowDef<'input, 'output, 'error>) : MAFWorkflow =
        toMAFCore (fun () -> WorkflowContext.create() |> WorkflowContext.withCancellation ct) workflow

    /// Compiles a workflow definition to MAF Workflow using WorkflowBuilder.
    /// Returns a Workflow that can be executed with InProcessExecution.RunAsync.
    /// If no name is set, uses "Workflow" as the default name.
    let internal toMAF<'input, 'output, 'error> (workflow: WorkflowDef<'input, 'output, 'error>) : MAFWorkflow =
        toMAFCore (fun () -> WorkflowContext.create()) workflow

    /// In-process workflow execution using MAF InProcessExecution.
    /// Use this for testing, simple scenarios, or when you don't need durable suspension.
    module InProcess =

        // ============ MAF IN-PROCESS EXECUTION ============

        /// Wraps each step's ExecuteInProcess to capture exceptions before MAF can swallow them.
        /// MAF's Lockstep.RunAsync catches executor exceptions internally and does not re-throw,
        /// which causes "No WorkflowOutputEvent found" instead of the real error.
        let private wrapStepsWithExceptionCapture (captured: ExceptionDispatchInfo ref) (workflow: WorkflowDef<'i, 'o, 'e>) =
            let wrappedSteps =
                workflow.TypedSteps
                |> List.map (fun step ->
                    { step with
                        ExecuteInProcess = fun input ctx ->
                            task {
                                try
                                    return! step.ExecuteInProcess input ctx
                                with ex ->
                                    System.Threading.Volatile.Write(&captured.contents, ExceptionDispatchInfo.Capture ex)
                                    return raise ex
                            } })
            { workflow with TypedSteps = wrappedSteps }

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

        /// Core in-process run: compiles via MAF and executes with Lockstep, seeding each step's
        /// WorkflowContext via `makeCtx`. `ct` is also passed to MAF's RunAsync.
        let private runCore<'input, 'output, 'error> (makeCtx: unit -> WorkflowContext) (ct: System.Threading.CancellationToken) (input: 'input) (workflow: WorkflowDef<'input, 'output, 'error>) : Task<'output> =
            task {
                // Wrap steps to capture exceptions before MAF swallows them
                let captured = ref Unchecked.defaultof<ExceptionDispatchInfo>
                let wrappedWorkflow = wrapStepsWithExceptionCapture captured workflow

                // Compile to MAF workflow with the seeded context
                let mafWorkflow = toMAFCore makeCtx wrappedWorkflow

                // Run via Lockstep InProcessExecution (runs all SuperSteps synchronously)
                let! run = MAFInProcessExecution.Lockstep.RunAsync(mafWorkflow, input :> obj, null, ct)

                // Re-throw captured exception if MAF swallowed it
                let edi = System.Threading.Volatile.Read(&captured.contents)
                if not (isNull edi) then
                    edi.Throw()

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

        /// Runs a workflow via MAF InProcessExecution.
        /// The workflow is compiled to MAF format and executed in-process.
        let run<'input, 'output, 'error> (input: 'input) (workflow: WorkflowDef<'input, 'output, 'error>) : Task<'output> =
            runCore (fun () -> WorkflowContext.create()) System.Threading.CancellationToken.None input workflow

        /// Runs a workflow with a CancellationToken that flows into every step's WorkflowContext.
        /// Use this to enable cooperative cancellation from an external source (e.g., user-triggered, host shutdown).
        /// Steps and Polly policies receive the token via ctx.CancellationToken.
        let runWithCancellation<'input, 'output, 'error> (ct: System.Threading.CancellationToken) (input: 'input) (workflow: WorkflowDef<'input, 'output, 'error>) : Task<'output> =
            runCore (fun () -> WorkflowContext.create() |> WorkflowContext.withCancellation ct) ct input workflow

        /// Runs a workflow with an IServiceProvider that flows into every step's WorkflowContext.
        /// Steps resolve dependencies at execution time via ctx.Services (see WorkflowContext.getRequiredService).
        let runWithServices<'input, 'output, 'error> (services: IServiceProvider) (input: 'input) (workflow: WorkflowDef<'input, 'output, 'error>) : Task<'output> =
            runCore (fun () -> WorkflowContext.create() |> WorkflowContext.withServices services) System.Threading.CancellationToken.None input workflow

        /// Runs a workflow seeding every step's WorkflowContext with both a CancellationToken and an
        /// IServiceProvider. This is the most general in-process entry point.
        let runWith<'input, 'output, 'error> (services: IServiceProvider) (ct: System.Threading.CancellationToken) (input: 'input) (workflow: WorkflowDef<'input, 'output, 'error>) : Task<'output> =
            runCore (fun () -> WorkflowContext.create() |> WorkflowContext.withCancellation ct |> WorkflowContext.withServices services) ct input workflow

        // AGENTS: DO NOT CHANGE THIS FUNCTION UNLESS EXPLICIT INSTRUCTIONS ARE GIVEN TO DO SO.
        /// Runs a workflow via MAF InProcessExecution, catching EarlyExitException.
        /// Returns Result<'output, 'error> where Error contains the typed error from tryStep.
        let tryRun<'input, 'output, 'error> (input: 'input) (workflow: WorkflowDef<'input, 'output, 'error>) : Task<Result<'output, 'error>> =
            task {
                let captured = ref Unchecked.defaultof<ExceptionDispatchInfo>
                let wrappedWorkflow = wrapStepsWithExceptionCapture captured workflow

                let mafWorkflow = toMAF wrappedWorkflow
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
                    // Re-throw captured exception if MAF swallowed it
                    let edi = System.Threading.Volatile.Read(&captured.contents)
                    if not (isNull edi) then
                        edi.Throw()

                    return failwith "Workflow terminated without success or early exit."

            }

        /// Converts a workflow to an executor (enables workflow composition).
        /// Uses MAF InProcessExecution to run the workflow, propagating the caller's
        /// DI services and CancellationToken into the nested workflow's steps.
        let toExecutor<'input, 'output, 'error> (name: string) (workflow: WorkflowDef<'input, 'output, 'error>) : Executor<'input, 'output> =
            {
                Name = name
                Execute = fun input ctx -> runWith ctx.Services ctx.CancellationToken input workflow
            }
