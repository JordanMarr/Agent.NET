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

/// A step in a workflow pipeline (untyped, internal - used by execution layer)
type WorkflowStep =
    | Step of durableId: string * name: string * execute: (obj -> WorkflowContext -> Task<obj>)
    | Route of durableId: string * router: (obj -> WorkflowContext -> Task<obj>)
    | Parallel of branches: (string * (obj -> WorkflowContext -> Task<obj>)) list  // (durableId, executor) pairs
    // Durable-only operations (require DurableTask runtime)
    // Stores metadata only - durable primitives are invoked in the execution layer
    | AwaitEvent of durableId: string * eventName: string * eventType: Type
    | Delay of durableId: string * duration: TimeSpan
    // Resilience wrappers (wrap the preceding step)
    | WithRetry of inner: WorkflowStep * maxRetries: int
    | WithTimeout of inner: WorkflowStep * timeout: TimeSpan
    | WithFallback of inner: WorkflowStep * fallbackId: string * fallback: (obj -> WorkflowContext -> Task<obj>)

/// A typed workflow step that preserves input/output type information.
/// This is the primary AST emitted by the CE builder.
/// The execution layer compiles this to WorkflowStep before running.
type TypedWorkflowStep<'input, 'output> =
    | Step of durableId: string * name: string * execute: ('input -> WorkflowContext -> Task<'output>)
    | Route of durableId: string * router: ('input -> WorkflowContext -> Task<'output>)
    | Parallel of branches: (string * TypedWorkflowStep<'input, 'output>) list
    | AwaitEvent of durableId: string * eventName: string   // 'input = unit, 'output = 'event
    | Delay of durableId: string * duration: TimeSpan       // 'input = unit, 'output = unit
    | WithRetry of inner: TypedWorkflowStep<'input, 'output> * maxRetries: int
    | WithTimeout of inner: TypedWorkflowStep<'input, 'output> * timeout: TimeSpan
    | WithFallback of inner: TypedWorkflowStep<'input, 'output> * fallbackId: string * fallback: TypedWorkflowStep<'input, 'output>

/// Wrapper that captures a typed step and its compile function.
/// This allows storing heterogeneous TypedWorkflowStep<'i,'o> values in a list.
type PackedTypedStep = {
    _compile: unit -> WorkflowStep
}
with
    member this.Compile() = this._compile()

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


/// Compiles TypedWorkflowStep to erased WorkflowStep for execution.
/// This is the bridge between the typed CE output and the existing execution layer.
module TypedWorkflowCompiler =

    /// Compiles a single TypedWorkflowStep to an erased WorkflowStep.
    /// Uses type information to eliminate reflection where possible.
    let rec compileStep<'input, 'output> (typedStep: TypedWorkflowStep<'input, 'output>) : WorkflowStep =
        match typedStep with
        | TypedWorkflowStep.Step (durableId, name, execute) ->
            // Wrap the typed execute function with obj erasure
            WorkflowStep.Step (durableId, name, fun input ctx -> task {
                let typedInput = input :?> 'input
                let! result = execute typedInput ctx
                return result :> obj
            })

        | TypedWorkflowStep.Route (durableId, router) ->
            // Wrap the typed router with obj erasure
            WorkflowStep.Route (durableId, fun input ctx -> task {
                let typedInput = input :?> 'input
                let! result = router typedInput ctx
                return result :> obj
            })

        | TypedWorkflowStep.Parallel branches ->
            // Compile each branch and create erased parallel step
            let erasedBranches =
                branches
                |> List.map (fun (branchId, branchStep) ->
                    match branchStep with
                    | TypedWorkflowStep.Step (_, _, execute) ->
                        let executor = fun (input: obj) (ctx: WorkflowContext) -> task {
                            let typedInput = input :?> 'input
                            let! result = execute typedInput ctx
                            return result :> obj
                        }
                        (branchId, executor)
                    | _ ->
                        failwith "Parallel branches must be Step nodes")
            WorkflowStep.Parallel erasedBranches

        | TypedWorkflowStep.AwaitEvent (durableId, eventName) ->
            // Use compile-time type info to create the erased step with the correct event type
            // This eliminates reflection - we know 'output is the event type
            WorkflowStep.AwaitEvent (durableId, eventName, typeof<'output>)

        | TypedWorkflowStep.Delay (durableId, duration) ->
            WorkflowStep.Delay (durableId, duration)

        | TypedWorkflowStep.WithRetry (inner, maxRetries) ->
            let compiledInner = compileStep inner
            WorkflowStep.WithRetry (compiledInner, maxRetries)

        | TypedWorkflowStep.WithTimeout (inner, timeout) ->
            let compiledInner = compileStep inner
            WorkflowStep.WithTimeout (compiledInner, timeout)

        | TypedWorkflowStep.WithFallback (inner, fallbackId, fallbackStep) ->
            let compiledInner = compileStep inner
            let compiledFallback = compileStep fallbackStep
            // Extract the fallback executor function from the compiled step
            match compiledFallback with
            | WorkflowStep.Step (_, _, fallbackExec) ->
                WorkflowStep.WithFallback (compiledInner, fallbackId, fallbackExec)
            | _ ->
                failwith "Fallback must compile to a Step"

    /// Compiles a list of TypedWorkflowSteps to erased WorkflowSteps.
    /// This is the main entry point for the compiler.
    let compile<'input, 'output> (typedSteps: TypedWorkflowStep<'input, 'output> list) : WorkflowStep list =
        typedSteps |> List.map compileStep


/// Functions for packing typed steps into existential wrappers
module PackedTypedStep =
    /// Packs a TypedWorkflowStep into a PackedTypedStep, capturing its compile function.
    let pack<'i, 'o> (typedStep: TypedWorkflowStep<'i, 'o>) : PackedTypedStep =
        { _compile = fun () -> TypedWorkflowCompiler.compileStep typedStep }

    /// Compiles a packed step to an erased WorkflowStep
    let compile (packed: PackedTypedStep) : WorkflowStep =
        packed.Compile()

    /// Compiles a list of packed steps to erased WorkflowSteps
    let compileAll (packedSteps: PackedTypedStep list) : WorkflowStep list =
        packedSteps |> List.map compile


/// Module for workflow building internals (public for inline SRTP support)
[<AutoOpen>]
module WorkflowInternal =

    /// Executes a single workflow step (used by nested workflows and in-process execution)
    let rec executeStep (step: WorkflowStep) (input: obj) (ctx: WorkflowContext) : Task<obj> =
        task {
            match step with
            | WorkflowStep.Step (_, _, execute) ->
                return! execute input ctx
            | WorkflowStep.Route (_, router) ->
                return! router input ctx
            | WorkflowStep.Parallel branches ->
                let! results =
                    branches
                    |> List.map (fun (_, exec) -> exec input ctx)
                    |> Task.WhenAll
                return (results |> Array.toList) :> obj
            // Durable-only operations - fail in in-process execution
            | WorkflowStep.AwaitEvent (durableId, _, _) ->
                return failwith $"AwaitEvent '{durableId}' requires durable runtime. Use Workflow.Durable.run instead of Workflow.InProcess.run."
            | WorkflowStep.Delay (_, duration) ->
                return failwith $"Delay ({duration}) requires durable runtime. Use Workflow.Durable.run instead of Workflow.InProcess.run."
            // Resilience wrappers - work in in-process execution
            | WorkflowStep.WithRetry (inner, maxRetries) ->
                let rec retry attempt =
                    task {
                        try
                            return! executeStep inner input ctx
                        with ex when attempt < maxRetries ->
                            return! retry (attempt + 1)
                    }
                return! retry 0
            | WorkflowStep.WithTimeout (inner, timeout) ->
                let execution = executeStep inner input ctx
                let timeoutTask = Task.Delay(timeout)
                let! winner = Task.WhenAny(execution, timeoutTask)
                if obj.ReferenceEquals(winner, timeoutTask) then
                    return raise (TimeoutException($"Step timed out after {timeout}"))
                else
                    return! execution
            | WorkflowStep.WithFallback (inner, _, fallback) ->
                try
                    return! executeStep inner input ctx
                with _ ->
                    return! fallback input ctx
        }

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
    // The typed steps are later compiled to erased WorkflowStep for execution

    /// Converts a Step<'i, 'o> to a TypedWorkflowStep<'i, 'o>
    let toTypedStep<'i, 'o> (durableId: string) (name: string) (step: Step<'i, 'o>) : TypedWorkflowStep<'i, 'o> =
        match step with
        | NestedWorkflow wf ->
            // Execute all nested workflow steps in sequence
            TypedWorkflowStep.Step (durableId, name, fun input ctx -> task {
                let mutable current: obj = box input
                // Compile nested typed steps to erased for execution
                let compiledSteps = PackedTypedStep.compileAll wf.TypedSteps
                for nestedStep in compiledSteps do
                    let! result = executeStep nestedStep current ctx
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
    // Resilience wrappers modify the last packed step to include the wrapping

    /// Wraps the previous step with retry logic. On failure, retries up to maxRetries times.
    [<CustomOperation("retry")>]
    member _.Retry(state: WorkflowState<'input, 'output>, maxRetries: int) : WorkflowState<'input, 'output> =
        match state.PackedSteps with
        | [] -> failwith "retry requires a preceding step"
        | packedSteps ->
            let allButLast = packedSteps |> List.take (packedSteps.Length - 1)
            let lastPacked = packedSteps |> List.last
            // Create a new packed step that wraps the compiled inner step with retry
            let wrappedPacked = { _compile = fun () -> WorkflowStep.WithRetry(lastPacked.Compile(), maxRetries) }
            { Name = state.Name; PackedSteps = allButLast @ [wrappedPacked] }

    /// Wraps the previous step with a timeout. Fails with TimeoutException if duration exceeded.
    [<CustomOperation("timeout")>]
    member _.Timeout(state: WorkflowState<'input, 'output>, duration: TimeSpan) : WorkflowState<'input, 'output> =
        match state.PackedSteps with
        | [] -> failwith "timeout requires a preceding step"
        | packedSteps ->
            let allButLast = packedSteps |> List.take (packedSteps.Length - 1)
            let lastPacked = packedSteps |> List.last
            // Create a new packed step that wraps the compiled inner step with timeout
            let wrappedPacked = { _compile = fun () -> WorkflowStep.WithTimeout(lastPacked.Compile(), duration) }
            { Name = state.Name; PackedSteps = allButLast @ [wrappedPacked] }

    /// Wraps the previous step with a fallback. On failure, executes the fallback step instead.
    [<CustomOperation("fallback")>]
    member inline _.Fallback(state: WorkflowState<'input, 'output>, x: ^T) : WorkflowState<'input, 'output> =
        let step : Step<'output, 'output> = ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'output, 'output>) (StepConv, x))
        let durableId = WorkflowInternal.getDurableId step
        let displayName = WorkflowInternal.getDisplayName step
        WorkflowInternal.warnIfLambda step durableId
        let fallbackExec = fun (input: obj) (ctx: WorkflowContext) ->
            task {
                let typedInput = input :?> 'output
                let exec = WorkflowInternal.stepToExecutor displayName step
                let! result = exec.Execute typedInput ctx
                return result :> obj
            }
        match state.PackedSteps with
        | [] -> failwith "fallback requires a preceding step"
        | packedSteps ->
            let allButLast = packedSteps |> List.take (packedSteps.Length - 1)
            let lastPacked = packedSteps |> List.last
            // Create a new packed step that wraps the compiled inner step with fallback
            let wrappedPacked = { _compile = fun () -> WorkflowStep.WithFallback(lastPacked.Compile(), durableId, fallbackExec) }
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

    /// In-process workflow execution using MAF InProcessExecution.
    /// Use this for testing, simple scenarios, or when you don't need durable suspension.
    module InProcess =

        // ============ MAF COMPILATION ============

        /// Converts an AgentNet WorkflowStep to a MAF Executor.
        /// The stepIndex is used to ensure unique executor IDs within a workflow.
        let rec private toMAFExecutor (stepIndex: int) (step: WorkflowStep) : MAFExecutor =
            match step with
            | WorkflowStep.Step (durableId, _, execute) ->
                // Wrap the execute function, creating an AgentNet context for execution
                // Combine durableId with stepIndex to ensure uniqueness within workflow
                let executorId = $"{durableId}_{stepIndex}"
                let fn = Func<obj, Task<obj>>(fun input ->
                    let ctx = WorkflowContext.create()
                    execute input ctx)
                ExecutorFactory.CreateStep(executorId, fn)

            | WorkflowStep.Route (durableId, router) ->
                // Router selects and executes the appropriate branch
                // Combine durableId with stepIndex to ensure uniqueness
                let executorId = $"{durableId}_{stepIndex}"
                let fn = Func<obj, Task<obj>>(fun input ->
                    let ctx = WorkflowContext.create()
                    router input ctx)
                ExecutorFactory.CreateStep(executorId, fn)

            | WorkflowStep.Parallel branches ->
                // Create parallel executor from branches
                let branchFns =
                    branches
                    |> List.map (fun (id, exec) ->
                        Func<obj, Task<obj>>(fun input ->
                            let ctx = WorkflowContext.create()
                            exec input ctx))
                    |> ResizeArray
                // Generate unique ID combining branch IDs and step index
                let parallelId = $"Parallel_{stepIndex}_" + (branches |> List.map fst |> String.concat "_")
                ExecutorFactory.CreateParallel(parallelId, branchFns)

            // Durable-only operations - cannot be compiled for in-process MAF execution
            | WorkflowStep.AwaitEvent (durableId, _, _) ->
                failwith $"AwaitEvent '{durableId}' cannot be compiled for in-process execution. Use Workflow.Durable.run instead."
            | WorkflowStep.Delay (_, duration) ->
                failwith $"Delay ({duration}) cannot be compiled for in-process execution. Use Workflow.Durable.run instead."

            // Resilience wrappers - compile by wrapping the inner step execution
            | WorkflowStep.WithRetry (inner, maxRetries) ->
                let executorId = $"WithRetry_{stepIndex}_{maxRetries}"
                let fn = Func<obj, Task<obj>>(fun input ->
                    let ctx = WorkflowContext.create()
                    let rec retry attempt =
                        task {
                            try
                                return! executeStep inner input ctx
                            with ex when attempt < maxRetries ->
                                return! retry (attempt + 1)
                        }
                    retry 0)
                ExecutorFactory.CreateStep(executorId, fn)

            | WorkflowStep.WithTimeout (inner, timeout) ->
                let executorId = $"WithTimeout_{stepIndex}_{int timeout.TotalMilliseconds}ms"
                let fn = Func<obj, Task<obj>>(fun input ->
                    task {
                        let ctx = WorkflowContext.create()
                        let execution = executeStep inner input ctx
                        let timeoutTask = Task.Delay(timeout)
                        let! winner = Task.WhenAny(execution, timeoutTask)
                        if obj.ReferenceEquals(winner, timeoutTask) then
                            return raise (TimeoutException($"Step timed out after {timeout}"))
                        else
                            return! execution
                    })
                ExecutorFactory.CreateStep(executorId, fn)

            | WorkflowStep.WithFallback (inner, fallbackId, fallback) ->
                let executorId = $"WithFallback_{stepIndex}_{fallbackId}"
                let fn = Func<obj, Task<obj>>(fun input ->
                    task {
                        let ctx = WorkflowContext.create()
                        try
                            return! executeStep inner input ctx
                        with _ ->
                            return! fallback input ctx
                    })
                ExecutorFactory.CreateStep(executorId, fn)

        /// Compiles a workflow definition to MAF Workflow using WorkflowBuilder.
        /// Returns a Workflow that can be executed with InProcessExecution.RunAsync.
        /// If no name is set, uses "Workflow" as the default name.
        let toMAF<'input, 'output> (workflow: WorkflowDef<'input, 'output>) : MAFWorkflow =
            let name = workflow.Name |> Option.defaultValue "Workflow"
            // Compile typed steps to erased WorkflowSteps
            let steps = PackedTypedStep.compileAll workflow.TypedSteps
            match steps with
            | [] -> failwith "Workflow must have at least one step"
            | steps ->
                // Create executors for all steps with unique indices
                let executors = steps |> List.mapi (fun i step -> toMAFExecutor i step)

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
