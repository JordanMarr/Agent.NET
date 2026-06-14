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
type MAFExecutorBinding = Microsoft.Agents.AI.Workflows.ExecutorBinding
type MAFRequestPort = Microsoft.Agents.AI.Workflows.RequestPort
type MAFRequestInfoEvent = Microsoft.Agents.AI.Workflows.RequestInfoEvent
type MAFExternalRequest = Microsoft.Agents.AI.Workflows.ExternalRequest
type MAFExternalResponse = Microsoft.Agents.AI.Workflows.ExternalResponse

/// Information about a pending external event (awaitEvent) that a suspended workflow is awaiting.
/// Surfaced to the in-process responder so it can supply the event payload.
type PendingRequest = {
    /// The MAF request-port id (encodes the event name and type).
    PortId: string
    /// The awaited event name (from awaitEvent).
    EventName: string
}

/// Functions for executing workflows in-process
[<RequireQualifiedAccess>]
module Workflow =

    // ============ MAF COMPILATION ============

    /// The MAF request-port id for an awaitEvent packed step at a given position.
    /// Must be recomputed identically by the responder/resume side to match suspension events.
    let internal awaitPortId (stepIndex: int) (packed: PackedTypedStep) = $"{packed.DurableId}_{stepIndex}"

    /// Maps an F# type to the type MAF sees at the boundary: unit -> WorkflowUnit (see WorkflowUnit).
    let private mafType (t: System.Type) = if t = typeof<unit> then typeof<WorkflowUnit> else t

    /// A RequestPort forwards its response downstream wrapped as an ExternalResponse; the next step
    /// expects the unwrapped payload. Unwrap it here at the MAF->step boundary.
    let private unwrapInput (input: obj) : obj =
        match input with
        | :? MAFExternalResponse as r -> r.Data.As<obj>()
        | _ -> input

    /// Converts a PackedTypedStep to a MAF graph node (as an ExecutorBinding so regular executors and
    /// request ports compose uniformly). `makeCtx` produces the WorkflowContext seeded for this run.
    /// - awaitEvent -> a MAF RequestPort (request = unit per the event-boundary invariant, response = 'event)
    /// - delayFor   -> an in-process delay executor
    /// - everything else -> a step executor over ExecuteInProcess
    let private packedStepToBinding (makeCtx: unit -> WorkflowContext) (stepIndex: int) (packed: PackedTypedStep) : MAFExecutorBinding =
        match packed.Kind with
        | DurableAwaitEvent _ ->
            // Request = unit (per the event-boundary invariant) -> WorkflowUnit at the MAF boundary.
            let port = MAFRequestPort(awaitPortId stepIndex packed, typeof<WorkflowUnit>, mafType packed.OutputType)
            MAFExecutorBinding.op_Implicit(port)
        | DurableDelay duration ->
            // In-process delay: cooperatively wait, then forward the input unchanged.
            let executorId = $"{packed.DurableId}_{stepIndex}"
            let fn = Func<obj, Task<obj>>(fun input ->
                let ctx = makeCtx ()
                task {
                    do! Task.Delay(duration, ctx.CancellationToken)
                    return unwrapInput input
                })
            MAFExecutorBinding.op_Implicit(Interop.ExecutorFactory.CreateStep(executorId, fn, mafType packed.OutputType))
        | Regular | Resilience _ ->
            // All regular steps and resilience wrappers can use the ExecuteInProcess function
            let executorId = $"{packed.DurableId}_{stepIndex}"
            let fn = Func<obj, Task<obj>>(fun input ->
                let ctx = makeCtx ()
                packed.ExecuteInProcess (unwrapInput input) ctx)
            MAFExecutorBinding.op_Implicit(Interop.ExecutorFactory.CreateStep(executorId, fn, mafType packed.OutputType))

    /// True if the workflow contains an awaitEvent (i.e., it suspends and needs a responder/durable host).
    let internal hasAwaitEvent (workflow: WorkflowDef<'i, 'o, 'e>) : bool =
        workflow.TypedSteps |> List.exists (fun p -> match p.Kind with DurableAwaitEvent _ -> true | _ -> false)

    /// Core compilation: builds a MAF Workflow from packed steps via WorkflowBuilder, seeding each
    /// step's WorkflowContext with `makeCtx` (cancellation token + DI services).
    let internal toMAFCore<'input, 'output, 'error> (makeCtx: unit -> WorkflowContext) (workflow: WorkflowDef<'input, 'output, 'error>) : MAFWorkflow =
        let name = workflow.Name |> Option.defaultValue "Workflow"
        match workflow.TypedSteps with
        | [] -> failwith "Workflow must have at least one step"
        | steps ->
            let bindings = steps |> List.mapi (packedStepToBinding makeCtx)
            match bindings with
            | [] -> failwith "Workflow must have at least one step"
            | first :: rest ->
                let mutable builder = MAFWorkflowBuilder(first).WithName(name)
                let mutable prev = first
                for b in rest do
                    builder <- builder.AddEdge(prev, b)
                    prev <- b
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
                // awaitEvent suspends the workflow at a RequestPort; the plain runner cannot complete it.
                if hasAwaitEvent workflow then
                    failwith "AwaitEvent suspends execution; the plain in-process runner cannot complete a suspending workflow. Use Workflow.InProcess.runWithResponses (in-process) or the durable runner."

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

        /// Runs a workflow in-process, supplying responses to awaitEvent suspension points via `respond`.
        /// `respond` receives info about each awaited event and returns the response value (the event
        /// payload, boxed). This is the in-process counterpart to durable resume: it lets awaitEvent
        /// workflows run end-to-end without durable hosting. Works for workflows with no awaitEvent too.
        let runWithResponses<'input, 'output, 'error> (respond: PendingRequest -> obj) (input: 'input) (workflow: WorkflowDef<'input, 'output, 'error>) : Task<'output> =
            task {
                let captured = ref Unchecked.defaultof<ExceptionDispatchInfo>
                let wrappedWorkflow = wrapStepsWithExceptionCapture captured workflow
                let mafWorkflow = toMAFCore (fun () -> WorkflowContext.create()) wrappedWorkflow

                // Recompute portId -> eventName (same ids used during compilation) so the responder
                // can identify which event it is answering.
                let eventNames =
                    workflow.TypedSteps
                    |> List.mapi (fun i p ->
                        match p.Kind with
                        | DurableAwaitEvent name -> Some (awaitPortId i p, name)
                        | _ -> None)
                    |> List.choose id
                    |> Map.ofList

                let! streamingRun = MAFInProcessExecution.RunStreamingAsync(mafWorkflow, input :> obj, null, System.Threading.CancellationToken.None)

                let mutable lastResult: obj option = None
                let e = streamingRun.WatchStreamAsync(System.Threading.CancellationToken.None).GetAsyncEnumerator(System.Threading.CancellationToken.None)
                let mutable go = true
                while go do
                    let! has = e.MoveNextAsync()
                    if not has then
                        go <- false
                    else
                        match box e.Current with
                        | :? MAFRequestInfoEvent as ri ->
                            let req = ri.Request
                            let portId = req.PortInfo.PortId
                            let pending = { PortId = portId; EventName = (eventNames |> Map.tryFind portId |> Option.defaultValue portId) }
                            let response = req.CreateResponse(respond pending)
                            do! streamingRun.SendResponseAsync(response)
                        | :? MAFWorkflowOutputEvent as output ->
                            lastResult <- Some output.Data
                        | _ -> ()
                do! e.DisposeAsync()

                let edi = System.Threading.Volatile.Read(&captured.contents)
                if not (isNull edi) then
                    edi.Throw()

                match lastResult with
                | Some data -> return convertToOutput<'output> data
                | None -> return failwith "Workflow did not produce output. No WorkflowOutputEvent found (was every awaitEvent answered?)."
            }

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
