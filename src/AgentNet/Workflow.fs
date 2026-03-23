namespace AgentNet

open System
open System.Threading.Tasks
open AgentNet.Interop

/// A typed workflow step that preserves input/output type information.
/// This is the single source of truth for both definition and execution.
/// No erased types - all type information is preserved.
type TypedWorkflowStep<'input, 'output> =
    | Step of durableId: string * name: string * execute: ('input -> WorkflowContext -> Task<'output>)
    | Route of durableId: string * router: ('input -> WorkflowContext -> Task<'output>)
    | Parallel of branches: (string * TypedWorkflowStep<'input, 'output>) list
    | AwaitEvent of durableId: string * eventName: string   // 'input = unit, 'output = 'event
    | Delay of durableId: string * duration: TimeSpan       // 'input = unit, 'output = unit
    | WithRetry of inner: TypedWorkflowStep<'input, 'output> * maxRetries: int
    | WithTimeout of inner: TypedWorkflowStep<'input, 'output> * timeout: TimeSpan
    | WithFallback of inner: TypedWorkflowStep<'input, 'output> * fallbackId: string * fallback: TypedWorkflowStep<'input, 'output>
    /// Step that returns Result<'output, obj> and may early-exit the workflow.
    /// On Ok value, produces 'output. On Error, throws EarlyExitException with boxed error.
    | TryStep of durableId: string * name: string * execute: ('input -> WorkflowContext -> Task<Result<'output, obj>>)

/// Metadata about a packed step for execution purposes.
type StepKind =
    | Regular
    | DurableAwaitEvent of eventName: string
    | DurableDelay of duration: TimeSpan
    | Resilience of kind: ResilienceKind

and ResilienceKind =
    | Retry of maxRetries: int
    | Timeout of timeout: TimeSpan
    | Fallback of fallbackId: string

/// Wrapper that captures typed execution functions for heterogeneous storage.
/// The packed step holds closures that capture the concrete type parameters,
/// enabling typed execution without reflection at runtime.
type PackedTypedStep = {
    /// Unique durable ID for this step
    DurableId: string
    /// Display name for this step
    Name: string
    /// The kind of step (for execution routing)
    Kind: StepKind
    /// The concrete output type of this step (for MAF protocol declaration)
    OutputType: System.Type
    /// Execute this step in-process (types captured in closure)
    ExecuteInProcess: obj -> WorkflowContext -> Task<obj>
    /// Factory to create a durable executor (types captured in closure, no reflection needed)
    CreateDurableExecutor: int -> Interop.IExecutor
    /// Activity info for registration: (durableId, executeFunc)
    /// None for durable-only operations (AwaitEvent, Delay)
    ActivityInfo: (string * (obj -> Task<obj>)) option
    /// Inner packed step for resilience wrappers
    InnerStep: PackedTypedStep option
    /// Fallback step for WithFallback
    FallbackStep: PackedTypedStep option
}

/// A workflow step type that unifies Task functions, Async functions, TypedAgents, Executors, and nested Workflows.
/// This enables clean workflow syntax and mixed-type fanOut operations.
/// Note: NestedWorkflow uses obj for error type since Step doesn't track errors (they're handled via exceptions).
and Step<'i, 'o> =
    | TaskStep of ('i -> Task<'o>)
    | AsyncStep of ('i -> Async<'o>)
    | AgentStep of TypedAgent<'i, 'o>
    | ExecutorStep of Executor<'i, 'o>
    | NestedWorkflow of WorkflowDef<'i, 'o, obj>

/// A workflow definition that can be executed.
/// 'error is a phantom type for tracking error types from tryStep operations.
/// Workflows with no tryStep have 'error = unit.
and WorkflowDef<'input, 'output, 'error> = {
    /// Optional name for the workflow (required for MAF compilation)
    Name: string option
    /// The typed steps in the workflow (packed for heterogeneous storage)
    TypedSteps: PackedTypedStep list
}


/// SRTP witness type for converting various types to Step.
/// Uses the type class pattern to enable inline resolution at call sites.
type StepConv = StepConv with
    static member inline ToStep(_: StepConv, fn: 'i -> Task<'o>) : Step<'i, 'o> = TaskStep fn
    static member inline ToStep(_: StepConv, fn: 'i -> Async<'o>) : Step<'i, 'o> = AsyncStep fn
    static member inline ToStep(_: StepConv, agent: TypedAgent<'i, 'o>) : Step<'i, 'o> = AgentStep agent
    static member inline ToStep(_: StepConv, exec: Executor<'i, 'o>) : Step<'i, 'o> = ExecutorStep exec
    static member inline ToStep(_: StepConv, step: Step<'i, 'o>) : Step<'i, 'o> = step  // Passthrough
    // Nested workflows have their error type erased to obj since Step doesn't track errors
    static member inline ToStep(_: StepConv, wf: WorkflowDef<'i, 'o, 'e>) : Step<'i, 'o> =
        NestedWorkflow { Name = wf.Name; TypedSteps = wf.TypedSteps }


/// Internal state carrier that threads type information through the builder.
/// 'error is a phantom type that accumulates error types from tryStep operations.
type WorkflowState<'input, 'output, 'error> = {
    Name: string option
    /// Packed typed steps - each captures its compile function
    PackedSteps: PackedTypedStep list
}


/// Functions for packing typed steps into execution wrappers.
/// This module captures type parameters at pack time, eliminating the need for
/// reflection at execution time. The packed steps contain fully typed closures.
module PackedTypedStep =

    /// Creates execution functions for a step by wrapping with obj boundaries.
    /// The inner execution remains fully typed; only the boundaries use boxing.
    let private createExecuteFuncs<'i, 'o> (execute: 'i -> WorkflowContext -> Task<'o>) =
        let inProcessExec = fun (input: obj) (ctx: WorkflowContext) -> task {
            let typedInput = input :?> 'i
            let! result = execute typedInput ctx
            return result :> obj
        }
        let durableExecFactory = fun (stepIndex: int) (durableId: string) ->
            let wrappedFn = Func<obj, Task<obj>>(fun input ->
                let ctx = WorkflowContext.create()
                task {
                    let typedInput = input :?> 'i
                    let! result = execute typedInput ctx
                    return result :> obj
                })
            Interop.DurableExecutorFactory.CreateStepExecutor(durableId, stepIndex, wrappedFn)
        (inProcessExec, durableExecFactory)

    /// Packs a TypedWorkflowStep into a PackedTypedStep, capturing type parameters.
    /// This is called at workflow construction time when types are known.
    /// No reflection is needed at execution time.
    let rec pack<'i, 'o> (typedStep: TypedWorkflowStep<'i, 'o>) : PackedTypedStep =
        match typedStep with
        | TypedWorkflowStep.Step (durableId, name, execute) ->
            let (inProcessExec, durableFactory) = createExecuteFuncs execute
            {
                DurableId = durableId
                Name = name
                Kind = Regular
                OutputType = typeof<'o>
                ExecuteInProcess = inProcessExec
                CreateDurableExecutor = fun stepIndex -> durableFactory stepIndex durableId
                ActivityInfo = Some (durableId, fun input ->
                    let ctx = WorkflowContext.create()
                    task {
                        let typedInput = input :?> 'i
                        let! result = execute typedInput ctx
                        return result :> obj
                    })
                InnerStep = None
                FallbackStep = None
            }

        | TypedWorkflowStep.Route (durableId, router) ->
            let inProcessExec = fun (input: obj) (ctx: WorkflowContext) -> task {
                let typedInput = input :?> 'i
                let! result = router typedInput ctx
                return result :> obj
            }
            let durableExecFactory = fun (stepIndex: int) ->
                let wrappedFn = Func<obj, Task<obj>>(fun input ->
                    let ctx = WorkflowContext.create()
                    task {
                        let typedInput = input :?> 'i
                        let! result = router typedInput ctx
                        return result :> obj
                    })
                Interop.DurableExecutorFactory.CreateStepExecutor(durableId, stepIndex, wrappedFn)
            {
                DurableId = durableId
                Name = "Route"
                Kind = Regular
                OutputType = typeof<'o>
                ExecuteInProcess = inProcessExec
                CreateDurableExecutor = durableExecFactory
                ActivityInfo = Some (durableId, fun input ->
                    let ctx = WorkflowContext.create()
                    task {
                        let typedInput = input :?> 'i
                        let! result = router typedInput ctx
                        return result :> obj
                    })
                InnerStep = None
                FallbackStep = None
            }

        | TypedWorkflowStep.Parallel branches ->
            let branchExecs =
                branches
                |> List.map (fun (branchId, branchStep) ->
                    match branchStep with
                    | TypedWorkflowStep.Step (_, _, execute) ->
                        let exec = fun (input: obj) (ctx: WorkflowContext) -> task {
                            let typedInput = input :?> 'i
                            let! result = execute typedInput ctx
                            return result :> obj
                        }
                        (branchId, exec)
                    | _ -> failwith "Parallel branches must be Step nodes")
            let parallelId = "Parallel_" + (branches |> List.map fst |> String.concat "_")
            let inProcessExec = fun (input: obj) (ctx: WorkflowContext) -> task {
                let! results =
                    branchExecs
                    |> List.map (fun (_, exec) -> exec input ctx)
                    |> Task.WhenAll
                return (results |> Array.toList) :> obj
            }
            let durableExecFactory = fun (stepIndex: int) ->
                let branchFns =
                    branchExecs
                    |> List.map (fun (_, exec) ->
                        Func<obj, Task<obj>>(fun input ->
                            let ctx = WorkflowContext.create()
                            exec input ctx))
                    |> ResizeArray
                Interop.DurableExecutorFactory.CreateParallelExecutor(parallelId, stepIndex, branchFns)
            {
                DurableId = parallelId
                Name = "Parallel"
                Kind = Regular
                OutputType = typeof<'o>
                ExecuteInProcess = inProcessExec
                CreateDurableExecutor = durableExecFactory
                ActivityInfo = None  // Parallel doesn't register as a single activity
                InnerStep = None
                FallbackStep = None
            }

        | TypedWorkflowStep.AwaitEvent (durableId, eventName) ->
            // Type parameter 'o is the event type - capture it at pack time!
            // This eliminates the need for reflection at execution time.
            let inProcessExec = fun (_: obj) (_: WorkflowContext) ->
                Task.FromException<obj>(exn $"AwaitEvent '{durableId}' requires durable runtime. Use Workflow.Durable.run instead of Workflow.InProcess.run.")
            let durableExecFactory = fun (stepIndex: int) ->
                // Call the generic method with 'o captured at pack time - NO REFLECTION!
                Interop.DurableExecutorFactory.CreateAwaitEventExecutor<'o>(durableId, eventName, stepIndex)
            {
                DurableId = durableId
                Name = $"AwaitEvent({eventName})"
                Kind = DurableAwaitEvent eventName
                OutputType = typeof<'o>
                ExecuteInProcess = inProcessExec
                CreateDurableExecutor = durableExecFactory
                ActivityInfo = None  // AwaitEvent is not an activity
                InnerStep = None
                FallbackStep = None
            }

        | TypedWorkflowStep.Delay (durableId, duration) ->
            let inProcessExec = fun (_: obj) (_: WorkflowContext) ->
                Task.FromException<obj>(exn $"Delay ({duration}) requires durable runtime. Use Workflow.Durable.run instead of Workflow.InProcess.run.")
            let durableExecFactory = fun (stepIndex: int) ->
                Interop.DurableExecutorFactory.CreateDelayExecutor(durableId, duration, stepIndex)
            {
                DurableId = durableId
                Name = $"Delay({duration})"
                Kind = DurableDelay duration
                OutputType = typeof<'o>
                ExecuteInProcess = inProcessExec
                CreateDurableExecutor = durableExecFactory
                ActivityInfo = None  // Delay is not an activity
                InnerStep = None
                FallbackStep = None
            }

        | TypedWorkflowStep.WithRetry (inner, maxRetries) ->
            let innerPacked = pack inner
            let inProcessExec = fun (input: obj) (ctx: WorkflowContext) ->
                let rec retry attempt =
                    task {
                        try
                            return! innerPacked.ExecuteInProcess input ctx
                        with ex when attempt < maxRetries ->
                            return! retry (attempt + 1)
                    }
                retry 0
            let durableExecFactory = fun (stepIndex: int) ->
                let innerFunc = Func<obj, Task<obj>>(fun input ->
                    let ctx = WorkflowContext.create()
                    innerPacked.ExecuteInProcess input ctx)
                Interop.DurableExecutorFactory.CreateRetryExecutor($"Retry_{stepIndex}", maxRetries, stepIndex, innerFunc)
            {
                DurableId = $"WithRetry_{innerPacked.DurableId}"
                Name = $"Retry({maxRetries})"
                Kind = Resilience (Retry maxRetries)
                OutputType = typeof<'o>
                ExecuteInProcess = inProcessExec
                CreateDurableExecutor = durableExecFactory
                ActivityInfo = innerPacked.ActivityInfo  // Inner step's activity
                InnerStep = Some innerPacked
                FallbackStep = None
            }

        | TypedWorkflowStep.WithTimeout (inner, timeout) ->
            let innerPacked = pack inner
            let inProcessExec = fun (input: obj) (ctx: WorkflowContext) ->
                task {
                    let execution = innerPacked.ExecuteInProcess input ctx
                    let timeoutTask = Task.Delay(timeout)
                    let! winner = Task.WhenAny(execution, timeoutTask)
                    if obj.ReferenceEquals(winner, timeoutTask) then
                        return raise (TimeoutException($"Step timed out after {timeout}"))
                    else
                        return! execution
                }
            let durableExecFactory = fun (stepIndex: int) ->
                let innerFunc = Func<obj, Task<obj>>(fun input ->
                    let ctx = WorkflowContext.create()
                    innerPacked.ExecuteInProcess input ctx)
                Interop.DurableExecutorFactory.CreateTimeoutExecutor($"Timeout_{stepIndex}", timeout, stepIndex, innerFunc)
            {
                DurableId = $"WithTimeout_{innerPacked.DurableId}"
                Name = $"Timeout({timeout})"
                Kind = Resilience (Timeout timeout)
                OutputType = typeof<'o>
                ExecuteInProcess = inProcessExec
                CreateDurableExecutor = durableExecFactory
                ActivityInfo = innerPacked.ActivityInfo  // Inner step's activity
                InnerStep = Some innerPacked
                FallbackStep = None
            }

        | TypedWorkflowStep.WithFallback (inner, fallbackId, fallbackStep) ->
            let innerPacked = pack inner
            let fallbackPacked = pack fallbackStep
            let inProcessExec = fun (input: obj) (ctx: WorkflowContext) ->
                task {
                    try
                        return! innerPacked.ExecuteInProcess input ctx
                    with _ ->
                        return! fallbackPacked.ExecuteInProcess input ctx
                }
            let durableExecFactory = fun (stepIndex: int) ->
                let innerFunc = Func<obj, Task<obj>>(fun input ->
                    let ctx = WorkflowContext.create()
                    innerPacked.ExecuteInProcess input ctx)
                let fallbackFunc = Func<obj, Task<obj>>(fun input ->
                    let ctx = WorkflowContext.create()
                    fallbackPacked.ExecuteInProcess input ctx)
                Interop.DurableExecutorFactory.CreateFallbackExecutor($"Fallback_{stepIndex}", fallbackId, stepIndex, innerFunc, fallbackFunc)
            {
                DurableId = $"WithFallback_{innerPacked.DurableId}"
                Name = $"Fallback({fallbackId})"
                Kind = Resilience (Fallback fallbackId)
                OutputType = typeof<'o>
                ExecuteInProcess = inProcessExec
                CreateDurableExecutor = durableExecFactory
                ActivityInfo = innerPacked.ActivityInfo  // Inner step's activity
                InnerStep = Some innerPacked
                FallbackStep = Some fallbackPacked
            }

        | TypedWorkflowStep.TryStep (durableId, name, execute) ->
            let inProcessExec = fun (input: obj) (ctx: WorkflowContext) -> task {
                let typedInput = input :?> 'i
                let! result = execute typedInput ctx
                match result with
                | Ok value ->
                    return value :> obj
                | Error error ->
                    // Signal early-exit with structured error payload
                    return EarlyExitSignal error :> obj
            }
            let durableExecFactory = fun (stepIndex: int) ->
                let wrappedFn = Func<obj, Task<obj>>(fun input ->
                    let ctx = WorkflowContext.create()
                    task {
                        let typedInput = input :?> 'i
                        let! result = execute typedInput ctx
                        match result with
                        | Ok value ->
                            return value :> obj
                        | Error error ->
                            // Signal early-exit with structured error payload
                            return EarlyExitSignal error :> obj
                    })
                Interop.DurableExecutorFactory.CreateStepExecutor(durableId, stepIndex, wrappedFn)
            {
                DurableId = durableId
                Name = name
                Kind = Regular
                OutputType = typeof<'o>
                ExecuteInProcess = inProcessExec
                CreateDurableExecutor = durableExecFactory
                ActivityInfo = Some (durableId, fun input ->
                    let ctx = WorkflowContext.create()
                    task {
                        let typedInput = input :?> 'i
                        let! result = execute typedInput ctx
                        match result with
                        | Ok value -> return value :> obj
                        | Error error -> return EarlyExitSignal error :> obj
                    })
                InnerStep = None
                FallbackStep = None
            }

    /// Checks if a packed step contains durable-only operations
    let rec isDurableOnly (packed: PackedTypedStep) : bool =
        match packed.Kind with
        | DurableAwaitEvent _ -> true
        | DurableDelay _ -> true
        | Resilience _ ->
            // Check inner step for durable ops
            packed.InnerStep |> Option.map isDurableOnly |> Option.defaultValue false
        | Regular -> false

    /// Collects all activities from a packed step (for durable registration)
    let rec collectActivities (packed: PackedTypedStep) : (string * (obj -> Task<obj>)) list =
        match packed.ActivityInfo with
        | Some info -> [info]
        | None ->
            match packed.InnerStep with
            | Some inner -> collectActivities inner
            | None -> []
        @
        match packed.FallbackStep with
        | Some fallback -> collectActivities fallback
        | None -> []


/// Module for workflow building internals (public for inline SRTP support)
[<AutoOpen>]
module WorkflowInternal =

    /// Executes a packed step in-process
    let executePackedStep (packed: PackedTypedStep) (input: obj) (ctx: WorkflowContext) : Task<obj> =
        packed.ExecuteInProcess input ctx

    /// Generates a stable durable ID from a Step (always auto-generated, never from Executor.Name)
    let getDurableId<'i, 'o> (step: Step<'i, 'o>) : string =
        match step with
        | TaskStep fn -> DurableId.forStep fn
        | AsyncStep fn -> DurableId.forStep fn
        | AgentStep agent -> DurableId.forAgent agent
        | ExecutorStep exec -> DurableId.forExecutor exec
        | NestedWorkflow _ -> DurableId.forWorkflow<'i, 'o> ()

    /// Gets the display name for a Step (Executor.Name if available, otherwise uses function name)
    let getDisplayName<'i, 'o> (step: Step<'i, 'o>) : string =
        match step with
        | ExecutorStep exec -> exec.Name  // Use explicit display name from Executor
        | TaskStep fn -> DurableId.getDisplayName fn  // Use function name
        | AsyncStep fn -> DurableId.getDisplayName fn  // Use function name
        | AgentStep _ -> "Agent"  // TypedAgent doesn't have a name property
        | NestedWorkflow _ -> "Workflow"  // Nested workflow

    /// Checks if a Step uses a lambda and logs a warning
    let warnIfLambda<'i, 'o> (step: Step<'i, 'o>) (id: string) : unit =
        match step with
        | TaskStep fn -> DurableId.warnIfLambda fn id
        | AsyncStep fn -> DurableId.warnIfLambda fn id
        | _ -> ()

    /// Converts a Step<'i, 'o> to an Executor<'i, 'o>
    /// Note: NestedWorkflow is handled separately
    let stepToExecutor<'i, 'o> (name: string) (step: Step<'i, 'o>) : Executor<'i, 'o> =
        match step with
        | TaskStep fn -> Executor.fromTask name fn
        | AsyncStep fn -> Executor.fromAsync name fn
        | AgentStep agent -> Executor.fromTypedAgent name agent
        | ExecutorStep exec -> { exec with Name = name }
        | NestedWorkflow _ -> failwith "NestedWorkflow should be handled separately, not in stepToExecutor"

    // ============ TYPED STEP CONVERSION ============
    // These functions convert Step<'i,'o> to TypedWorkflowStep<'i,'o>
    // The typed steps are packed for heterogeneous storage and direct execution

    /// Converts a Step<'i, 'o> to a TypedWorkflowStep<'i, 'o>
    let toTypedStep<'i, 'o> (durableId: string) (name: string) (step: Step<'i, 'o>) : TypedWorkflowStep<'i, 'o> =
        match step with
        | NestedWorkflow wf ->
            // Execute all nested workflow steps in sequence
            TypedWorkflowStep.Step (durableId, name, fun input ctx -> task {
                let mutable current: obj = box input
                // Execute nested packed steps directly (no compilation needed)
                for packedStep in wf.TypedSteps do
                    let! result = packedStep.ExecuteInProcess current ctx
                    current <- result
                return current :?> 'o
            })
        | _ ->
            let exec = stepToExecutor name step
            TypedWorkflowStep.Step (durableId, name, fun input ctx -> exec.Execute input ctx)

    /// Converts a list of Steps to a TypedWorkflowStep.Parallel
    let toTypedStepParallel<'i, 'o> (steps: Step<'i, 'o> list) : TypedWorkflowStep<'i, 'o list> =
        let branches =
            steps
            |> List.map (fun step ->
                let durableId = getDurableId step
                let displayName = getDisplayName step
                warnIfLambda step durableId
                let typedStep = toTypedStep durableId displayName step
                (durableId, typedStep))
        // Create a parallel step that returns a list of results
        let durableId = "Parallel_" + (branches |> List.map fst |> String.concat "_")
        TypedWorkflowStep.Step (durableId, "Parallel", fun input ctx -> task {
            let! results =
                branches
                |> List.map (fun (_, typedStep) ->
                    match typedStep with
                    | TypedWorkflowStep.Step (_, _, exec) -> exec input ctx
                    | _ -> failwith "Expected Step in parallel branch")
                |> Task.WhenAll
            return results |> Array.toList
        })

    /// Converts a Step to a TypedWorkflowStep for fan-in (aggregating parallel results)
    let toTypedStepFanIn<'elem, 'o> (durableId: string) (name: string) (step: Step<'elem list, 'o>) : TypedWorkflowStep<'elem list, 'o> =
        let exec = stepToExecutor name step
        TypedWorkflowStep.Step (durableId, name, fun input ctx -> exec.Execute input ctx)

    /// Converts a router function to a TypedWorkflowStep.Route
    let toTypedRouter<'a, 'b> (durableId: string) (router: 'a -> WorkflowContext -> Task<'b>) : TypedWorkflowStep<'a, 'b> =
        TypedWorkflowStep.Route (durableId, router)


/// Functions for executing workflows
[<RequireQualifiedAccess>]
module Workflow =

    /// Sets the name of a workflow (used for MAF compilation)
    let withName<'input, 'output, 'error> (name: string) (workflow: WorkflowDef<'input, 'output, 'error>) : WorkflowDef<'input, 'output, 'error> =
        { workflow with Name = Some name }


module Task = 
    /// An convenient alias for Task.FromResult. Wraps a value in a completed Task.
    let fromResult x = Task.FromResult x

