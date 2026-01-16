namespace AgentNet

open System
open System.Threading.Tasks
open AgentNet.Interop

// Type aliases to avoid conflicts between AgentNet and MAF
// NOTE: Don't open Microsoft.Agents.AI.Workflows to avoid Executor<,> conflict
type MAFExecutor = Microsoft.Agents.AI.Workflows.Executor
type MAFWorkflow = Microsoft.Agents.AI.Workflows.Workflow
type MAFWorkflowBuilder = Microsoft.Agents.AI.Workflows.WorkflowBuilder
type MAFInProcessExecution = Microsoft.Agents.AI.Workflows.InProcessExecution
type MAFExecutorCompletedEvent = Microsoft.Agents.AI.Workflows.ExecutorCompletedEvent

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
and Step<'i, 'o> =
    | TaskStep of ('i -> Task<'o>)
    | AsyncStep of ('i -> Async<'o>)
    | AgentStep of TypedAgent<'i, 'o>
    | ExecutorStep of Executor<'i, 'o>
    | NestedWorkflow of WorkflowDef<'i, 'o>

/// A workflow definition that can be executed
and WorkflowDef<'input, 'output> = {
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
    static member inline ToStep(_: StepConv, wf: WorkflowDef<'i, 'o>) : Step<'i, 'o> = NestedWorkflow wf


/// Internal state carrier that threads type information through the builder
type WorkflowState<'input, 'output> = {
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
            Interop.ExecutorFactory.CreateStepExecutor(durableId, stepIndex, wrappedFn)
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
                Interop.ExecutorFactory.CreateStepExecutor(durableId, stepIndex, wrappedFn)
            {
                DurableId = durableId
                Name = "Route"
                Kind = Regular
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
                Interop.ExecutorFactory.CreateParallelExecutor(parallelId, stepIndex, branchFns)
            {
                DurableId = parallelId
                Name = "Parallel"
                Kind = Regular
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
                Interop.ExecutorFactory.CreateAwaitEventExecutor<'o>(durableId, eventName, stepIndex)
            {
                DurableId = durableId
                Name = $"AwaitEvent({eventName})"
                Kind = DurableAwaitEvent eventName
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
                Interop.ExecutorFactory.CreateDelayExecutor(durableId, duration, stepIndex)
            {
                DurableId = durableId
                Name = $"Delay({duration})"
                Kind = DurableDelay duration
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
                Interop.ExecutorFactory.CreateRetryExecutor($"Retry_{stepIndex}", maxRetries, stepIndex, innerFunc)
            {
                DurableId = $"WithRetry_{innerPacked.DurableId}"
                Name = $"Retry({maxRetries})"
                Kind = Resilience (Retry maxRetries)
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
                Interop.ExecutorFactory.CreateTimeoutExecutor($"Timeout_{stepIndex}", timeout, stepIndex, innerFunc)
            {
                DurableId = $"WithTimeout_{innerPacked.DurableId}"
                Name = $"Timeout({timeout})"
                Kind = Resilience (Timeout timeout)
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
                Interop.ExecutorFactory.CreateFallbackExecutor($"Fallback_{stepIndex}", fallbackId, stepIndex, innerFunc, fallbackFunc)
            {
                DurableId = $"WithFallback_{innerPacked.DurableId}"
                Name = $"Fallback({fallbackId})"
                Kind = Resilience (Fallback fallbackId)
                ExecuteInProcess = inProcessExec
                CreateDurableExecutor = durableExecFactory
                ActivityInfo = innerPacked.ActivityInfo  // Inner step's activity
                InnerStep = Some innerPacked
                FallbackStep = Some fallbackPacked
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


/// Builder for the workflow computation expression
type WorkflowBuilder() =

    // ============ CE CORE MEMBERS (per DESIGN_CE_TYPE_THREADING.md) ============
    // These members implement the spec-compliant type threading invariants.
    // No member may use wildcards (_) or erased types (obj) in WorkflowState.

    /// Identity workflow - produces input unchanged
    member _.Zero() : WorkflowState<'input, 'input> = { Name = None; PackedSteps = [] }

    /// Yields a value - for CE compatibility (workflow uses Zero primarily)
    member _.Yield(_: unit) : WorkflowState<'input, 'input> = { Name = None; PackedSteps = [] }

    /// Delays evaluation - preserves phantom types exactly
    member _.Delay(f: unit -> WorkflowState<'input, 'a>) : WorkflowState<'input, 'a> = f()

    /// Combines two workflow states - output type comes from second state
    member _.Combine(s1: WorkflowState<'input, 'a>, s2: WorkflowState<'input, 'b>) : WorkflowState<'input, 'b> =
        { Name = s2.Name |> Option.orElse s1.Name; PackedSteps = s1.PackedSteps @ s2.PackedSteps }

    /// Sets the name of the workflow (used for MAF compilation and durable function registration)
    [<CustomOperation("name")>]
    member _.Name(state: WorkflowState<'input, 'output>, name: string) : WorkflowState<'input, 'output> =
        { state with Name = Some name }

    // ============ STEP OPERATIONS ============
    // Type threading invariant: WorkflowState<'input,'a> -> Executor<'a,'b> -> WorkflowState<'input,'b>
    // Uses inline SRTP to accept Task fn, Async fn, TypedAgent, Executor, Workflow, or Step directly.
    // All operations create TypedWorkflowStep nodes and pack them for storage.
    // Durable IDs are auto-generated; display names come from Executor.Name if available.
    // NO WILDCARDS OR ERASED TYPES - enforced by single Step member with Zero providing initial state.

    /// Adds a step to the workflow - threads type through from 'a to 'b (uses SRTP for type resolution).
    /// For the first step, 'a unifies with 'input from Zero().
    /// INVARIANT: WorkflowState<'input,'a> + Step<'a,'b> -> WorkflowState<'input,'b>
    [<CustomOperation("step")>]
    member inline _.Step(state: WorkflowState<'input, 'a>, x: ^T) : WorkflowState<'input, 'b> =
        let step : Step<'a, 'b> = ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'a, 'b>) (StepConv, x))
        let durableId = WorkflowInternal.getDurableId step
        let displayName = WorkflowInternal.getDisplayName step
        WorkflowInternal.warnIfLambda step durableId
        let typedStep = WorkflowInternal.toTypedStep durableId displayName step
        { Name = state.Name; PackedSteps = state.PackedSteps @ [PackedTypedStep.pack typedStep] }

    // ============ ROUTING ============

    /// Routes to different executors based on the previous step's output.
    /// Uses SRTP to accept Task fn, Async fn, TypedAgent, Executor, or Step directly.
    /// Durable IDs are auto-generated for each branch.
    [<CustomOperation("route")>]
    member inline _.Route(state: WorkflowState<'input, 'middle>, router: 'middle -> ^T) : WorkflowState<'input, 'output> =
        // Generate a durable ID for the route decision point based on input/output types
        let routeDurableId = DurableId.forRoute router
        // Create a typed router that wraps the user's routing function
        let typedRouterFn = fun (input: 'middle) (ctx: WorkflowContext) ->
            let result = router input
            let step : Step<'middle, 'output> =
                ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'middle, 'output>) (StepConv, result))
            let displayName = WorkflowInternal.getDisplayName step
            WorkflowInternal.warnIfLambda step (WorkflowInternal.getDurableId step)
            let exec = WorkflowInternal.stepToExecutor displayName step
            exec.Execute input ctx
        let typedStep = TypedWorkflowStep.Route (routeDurableId, typedRouterFn)
        { Name = state.Name; PackedSteps = state.PackedSteps @ [PackedTypedStep.pack typedStep] }

    // ============ FANOUT OPERATIONS ============
    // SRTP overloads for 2-5 arguments - no wrapper needed!
    // For 6+ branches, use: fanOut [+fn1; +fn2; ...]
    // Durable IDs are auto-generated for each parallel branch

    /// Runs 2 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, x1: ^A, x2: ^B) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let typedStep = WorkflowInternal.toTypedStepParallel [s1; s2]
        { Name = state.Name; PackedSteps = state.PackedSteps @ [PackedTypedStep.pack typedStep] }

    /// Runs 3 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, x1: ^A, x2: ^B, x3: ^C) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let s3 : Step<'middle, 'o> = ((^C or StepConv) : (static member ToStep: StepConv * ^C -> Step<'middle, 'o>) (StepConv, x3))
        let typedStep = WorkflowInternal.toTypedStepParallel [s1; s2; s3]
        { Name = state.Name; PackedSteps = state.PackedSteps @ [PackedTypedStep.pack typedStep] }

    /// Runs 4 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, x1: ^A, x2: ^B, x3: ^C, x4: ^D) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let s3 : Step<'middle, 'o> = ((^C or StepConv) : (static member ToStep: StepConv * ^C -> Step<'middle, 'o>) (StepConv, x3))
        let s4 : Step<'middle, 'o> = ((^D or StepConv) : (static member ToStep: StepConv * ^D -> Step<'middle, 'o>) (StepConv, x4))
        let typedStep = WorkflowInternal.toTypedStepParallel [s1; s2; s3; s4]
        { Name = state.Name; PackedSteps = state.PackedSteps @ [PackedTypedStep.pack typedStep] }

    /// Runs 5 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, x1: ^A, x2: ^B, x3: ^C, x4: ^D, x5: ^E) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let s3 : Step<'middle, 'o> = ((^C or StepConv) : (static member ToStep: StepConv * ^C -> Step<'middle, 'o>) (StepConv, x3))
        let s4 : Step<'middle, 'o> = ((^D or StepConv) : (static member ToStep: StepConv * ^D -> Step<'middle, 'o>) (StepConv, x4))
        let s5 : Step<'middle, 'o> = ((^E or StepConv) : (static member ToStep: StepConv * ^E -> Step<'middle, 'o>) (StepConv, x5))
        let typedStep = WorkflowInternal.toTypedStepParallel [s1; s2; s3; s4; s5]
        { Name = state.Name; PackedSteps = state.PackedSteps @ [PackedTypedStep.pack typedStep] }

    /// Runs multiple steps in parallel (fan-out) - for 6+ branches, use '+' operator
    [<CustomOperation("fanOut")>]
    member _.FanOut(state: WorkflowState<'input, 'middle>, steps: Step<'middle, 'o> list) : WorkflowState<'input, 'o list> =
        let typedStep = WorkflowInternal.toTypedStepParallel steps
        { Name = state.Name; PackedSteps = state.PackedSteps @ [PackedTypedStep.pack typedStep] }

    // ============ FANIN OPERATIONS ============
    // Uses inline SRTP to accept Task fn, Async fn, TypedAgent, Executor, or Step directly
    // Durable IDs are auto-generated

    /// Aggregates parallel results with any supported step type (uses SRTP for type resolution)
    [<CustomOperation("fanIn")>]
    member inline _.FanIn(state: WorkflowState<'input, 'elem list>, x: ^T) : WorkflowState<'input, 'output> =
        let step : Step<'elem list, 'output> = ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'elem list, 'output>) (StepConv, x))
        let durableId = WorkflowInternal.getDurableId step
        let displayName = WorkflowInternal.getDisplayName step
        WorkflowInternal.warnIfLambda step durableId
        let typedStep = WorkflowInternal.toTypedStepFanIn durableId displayName step
        { Name = state.Name; PackedSteps = state.PackedSteps @ [PackedTypedStep.pack typedStep] }

    // ============ RESILIENCE OPERATIONS ============
    // These wrap the preceding step with retry, timeout, or fallback behavior
    // Resilience wrappers create new packed steps that wrap the inner step's execution

    /// Wraps the previous step with retry logic. On failure, retries up to maxRetries times.
    [<CustomOperation("retry")>]
    member _.Retry(state: WorkflowState<'input, 'output>, maxRetries: int) : WorkflowState<'input, 'output> =
        match state.PackedSteps with
        | [] -> failwith "retry requires a preceding step"
        | packedSteps ->
            let allButLast = packedSteps |> List.take (packedSteps.Length - 1)
            let innerPacked = packedSteps |> List.last
            // Create a wrapped packed step with retry logic
            let wrappedPacked = {
                DurableId = $"WithRetry_{innerPacked.DurableId}"
                Name = $"Retry({maxRetries})"
                Kind = Resilience (Retry maxRetries)
                ExecuteInProcess = fun input ctx ->
                    let rec retry attempt =
                        task {
                            try
                                return! innerPacked.ExecuteInProcess input ctx
                            with _ when attempt < maxRetries ->
                                return! retry (attempt + 1)
                        }
                    retry 0
                CreateDurableExecutor = fun stepIndex ->
                    let innerFunc = Func<obj, Task<obj>>(fun input ->
                        let ctx = WorkflowContext.create()
                        innerPacked.ExecuteInProcess input ctx)
                    Interop.ExecutorFactory.CreateRetryExecutor($"Retry_{stepIndex}", maxRetries, stepIndex, innerFunc)
                ActivityInfo = innerPacked.ActivityInfo
                InnerStep = Some innerPacked
                FallbackStep = None
            }
            { Name = state.Name; PackedSteps = allButLast @ [wrappedPacked] }

    /// Wraps the previous step with a timeout. Fails with TimeoutException if duration exceeded.
    [<CustomOperation("timeout")>]
    member _.Timeout(state: WorkflowState<'input, 'output>, duration: TimeSpan) : WorkflowState<'input, 'output> =
        match state.PackedSteps with
        | [] -> failwith "timeout requires a preceding step"
        | packedSteps ->
            let allButLast = packedSteps |> List.take (packedSteps.Length - 1)
            let innerPacked = packedSteps |> List.last
            // Create a wrapped packed step with timeout logic
            let wrappedPacked = {
                DurableId = $"WithTimeout_{innerPacked.DurableId}"
                Name = $"Timeout({duration})"
                Kind = Resilience (Timeout duration)
                ExecuteInProcess = fun input ctx ->
                    task {
                        let execution = innerPacked.ExecuteInProcess input ctx
                        let timeoutTask = Task.Delay(duration)
                        let! winner = Task.WhenAny(execution, timeoutTask)
                        if obj.ReferenceEquals(winner, timeoutTask) then
                            return raise (TimeoutException($"Step timed out after {duration}"))
                        else
                            return! execution
                    }
                CreateDurableExecutor = fun stepIndex ->
                    let innerFunc = Func<obj, Task<obj>>(fun input ->
                        let ctx = WorkflowContext.create()
                        innerPacked.ExecuteInProcess input ctx)
                    Interop.ExecutorFactory.CreateTimeoutExecutor($"Timeout_{stepIndex}", duration, stepIndex, innerFunc)
                ActivityInfo = innerPacked.ActivityInfo
                InnerStep = Some innerPacked
                FallbackStep = None
            }
            { Name = state.Name; PackedSteps = allButLast @ [wrappedPacked] }

    /// Wraps the previous step with a fallback. On failure, executes the fallback step instead.
    [<CustomOperation("fallback")>]
    member inline _.Fallback(state: WorkflowState<'input, 'output>, x: ^T) : WorkflowState<'input, 'output> =
        let step : Step<'output, 'output> = ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'output, 'output>) (StepConv, x))
        let durableId = WorkflowInternal.getDurableId step
        let displayName = WorkflowInternal.getDisplayName step
        WorkflowInternal.warnIfLambda step durableId
        // Create a typed step for the fallback and pack it
        let fallbackTypedStep = WorkflowInternal.toTypedStep durableId displayName step
        let fallbackPacked = PackedTypedStep.pack fallbackTypedStep
        match state.PackedSteps with
        | [] -> failwith "fallback requires a preceding step"
        | packedSteps ->
            let allButLast = packedSteps |> List.take (packedSteps.Length - 1)
            let innerPacked = packedSteps |> List.last
            // Create a wrapped packed step with fallback logic
            let wrappedPacked = {
                DurableId = $"WithFallback_{innerPacked.DurableId}"
                Name = $"Fallback({durableId})"
                Kind = Resilience (Fallback durableId)
                ExecuteInProcess = fun input ctx ->
                    task {
                        try
                            return! innerPacked.ExecuteInProcess input ctx
                        with _ ->
                            return! fallbackPacked.ExecuteInProcess input ctx
                    }
                CreateDurableExecutor = fun stepIndex ->
                    let innerFunc = Func<obj, Task<obj>>(fun input ->
                        let ctx = WorkflowContext.create()
                        innerPacked.ExecuteInProcess input ctx)
                    let fallbackFunc = Func<obj, Task<obj>>(fun input ->
                        let ctx = WorkflowContext.create()
                        fallbackPacked.ExecuteInProcess input ctx)
                    Interop.ExecutorFactory.CreateFallbackExecutor($"Fallback_{stepIndex}", durableId, stepIndex, innerFunc, fallbackFunc)
                ActivityInfo = innerPacked.ActivityInfo
                InnerStep = Some innerPacked
                FallbackStep = Some fallbackPacked
            }
            { Name = state.Name; PackedSteps = allButLast @ [wrappedPacked] }

    /// Builds the final workflow definition
    member _.Run(state: WorkflowState<'input, 'output>) : WorkflowDef<'input, 'output> =
        { Name = state.Name; TypedSteps = state.PackedSteps }


module Task = 
    /// An convenient alias for Task.FromResult. Wraps a value in a completed Task.
    let fromResult x = Task.FromResult x

/// The workflow computation expression builder instance
[<AutoOpen>]
module WorkflowCE =
    let workflow = WorkflowBuilder()

    /// Prefix operator to convert any supported type to Step<'i, 'o>.
    /// Supports: Task fn, Async fn, TypedAgent, Executor, or Step passthrough.
    /// Used for fanOut lists with 6+ branches: fanOut [+fn1; +fn2; +fn3; +fn4; +fn5; +fn6]
    let inline (~+) (x: ^T) : Step<'i, 'o> =
        ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'i, 'o>) (StepConv, x))


/// Functions for executing workflows
[<RequireQualifiedAccess>]
module Workflow =

    /// Sets the name of a workflow (used for MAF compilation)
    let withName<'input, 'output> (name: string) (workflow: WorkflowDef<'input, 'output>) : WorkflowDef<'input, 'output> =
        { workflow with Name = Some name }

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
            Interop.ExecutorFactory.CreateStep(executorId, fn)

    /// Compiles a workflow definition to MAF Workflow using WorkflowBuilder.
    /// Returns a Workflow that can be executed with InProcessExecution.RunAsync.
    /// If no name is set, uses "Workflow" as the default name.
    let internal toMAF<'input, 'output> (workflow: WorkflowDef<'input, 'output>) : MAFWorkflow =
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
        let run<'input, 'output> (input: 'input) (workflow: WorkflowDef<'input, 'output>) : Task<'output> =
            task {
                // Compile to MAF workflow
                let mafWorkflow = toMAF workflow

                // Run via InProcessExecution
                let! run = MAFInProcessExecution.RunAsync(mafWorkflow, input :> obj)
                use _ = run

                // Find the last ExecutorCompletedEvent - it should be the workflow output
                let mutable lastResult: obj option = None
                for evt in run.NewEvents do
                    match evt with
                    | :? MAFExecutorCompletedEvent as completed ->
                        lastResult <- Some completed.Data
                    | _ -> ()

                match lastResult with
                | Some data -> return convertToOutput<'output> data
                | None -> return failwith "Workflow did not produce output. No ExecutorCompletedEvent found."
            }

        /// Converts a workflow to an executor (enables workflow composition).
        /// Uses MAF InProcessExecution to run the workflow.
        let toExecutor<'input, 'output> (name: string) (workflow: WorkflowDef<'input, 'output>) : Executor<'input, 'output> =
            {
                Name = name
                Execute = fun input _ -> run input workflow
            }
