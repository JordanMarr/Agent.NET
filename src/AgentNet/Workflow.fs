namespace AgentNet

open System
open System.Threading.Tasks

/// Backoff strategy for retries
type BackoffStrategy =
    | Immediate
    | Linear of delay: TimeSpan
    | Exponential of initial: TimeSpan * multiplier: float

/// Resilience settings for a step
type ResilienceSettings = {
    RetryCount: int
    Backoff: BackoffStrategy
    Timeout: TimeSpan option
    Fallback: (obj -> WorkflowContext -> Task<obj>) option
}

module ResilienceSettings =
    let defaults = {
        RetryCount = 0
        Backoff = Immediate
        Timeout = None
        Fallback = None
    }

/// A step in a workflow pipeline (untyped, internal)
type WorkflowStep =
    | Step of durableId: string * name: string * execute: (obj -> WorkflowContext -> Task<obj>)
    | Route of durableId: string * router: (obj -> WorkflowContext -> Task<obj>)
    | Parallel of branches: (string * (obj -> WorkflowContext -> Task<obj>)) list  // (durableId, executor) pairs
    | Resilient of settings: ResilienceSettings * inner: WorkflowStep


/// A workflow step type that unifies Task functions, Async functions, TypedAgents, Executors, and nested Workflows.
/// This enables clean workflow syntax and mixed-type fanOut operations.
type Step<'i, 'o> =
    | TaskStep of ('i -> Task<'o>)
    | AsyncStep of ('i -> Async<'o>)
    | AgentStep of TypedAgent<'i, 'o>
    | ExecutorStep of Executor<'i, 'o>
    | NestedWorkflow of WorkflowDef<'i, 'o>

/// A workflow definition that can be executed
and WorkflowDef<'input, 'output> = {
    /// Optional name for the workflow (required for MAF compilation)
    Name: string option
    /// The steps in the workflow, in order
    Steps: WorkflowStep list
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
    Steps: WorkflowStep list
}


/// Module for workflow building internals (public for inline SRTP support)
[<AutoOpen>]
module WorkflowInternal =

    /// Forward reference to executeInnerStep (set by Workflow module after it's defined)
    let mutable internal executeWorkflowStepRef: (WorkflowStep -> obj -> WorkflowContext -> Task<obj>) option = None

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
    /// Note: NestedWorkflow is handled separately in wrapStep
    let stepToExecutor<'i, 'o> (name: string) (step: Step<'i, 'o>) : Executor<'i, 'o> =
        match step with
        | TaskStep fn -> Executor.fromTask name fn
        | AsyncStep fn -> Executor.fromAsync name fn
        | AgentStep agent -> Executor.fromTypedAgent name agent
        | ExecutorStep exec -> { exec with Name = name }
        | NestedWorkflow _ -> failwith "NestedWorkflow should be handled in wrapStep, not stepToExecutor"

    /// Wraps a Step as an untyped WorkflowStep
    let wrapStep<'i, 'o> (durableId: string) (name: string) (step: Step<'i, 'o>) : WorkflowStep =
        match step with
        | NestedWorkflow wf ->
            // Execute all nested workflow steps in sequence
            Step (durableId, name, fun input ctx -> task {
                let exec = executeWorkflowStepRef.Value
                let mutable current = input
                for nestedStep in wf.Steps do
                    let! result = exec nestedStep current ctx
                    current <- result
                return current
            })
        | _ ->
            let exec = stepToExecutor name step
            Step (durableId, exec.Name, fun input ctx -> task {
                let typedInput = input :?> 'i
                let! result = exec.Execute typedInput ctx
                return result :> obj
            })

    /// Wraps a list of Steps as parallel execution (supports mixed types!)
    /// Uses auto-generated durable IDs for each parallel branch
    let wrapStepParallel<'i, 'o> (steps: Step<'i, 'o> list) : WorkflowStep =
        let branches =
            steps
            |> List.mapi (fun i step ->
                let durableId = getDurableId step
                let displayName = getDisplayName step
                let exec = stepToExecutor displayName step
                warnIfLambda step durableId
                let executor = fun (input: obj) (ctx: WorkflowContext) -> task {
                    let typedInput = input :?> 'i
                    let! result = exec.Execute typedInput ctx
                    return result :> obj
                }
                (durableId, executor))
        Parallel branches

    /// Wraps a Step as a fan-in aggregator, handling obj list → typed list conversion
    let wrapStepFanIn<'elem, 'o> (durableId: string) (name: string) (step: Step<'elem list, 'o>) : WorkflowStep =
        let exec = stepToExecutor name step
        Step (durableId, exec.Name, fun input ctx -> task {
            let objList = input :?> obj list
            let typedList = objList |> List.map (fun o -> o :?> 'elem)
            let! result = exec.Execute typedList ctx
            return result :> obj
        })

    /// Converts a Step to a fallback function for resilience settings
    let stepToFallback<'i, 'o> (step: Step<'i, 'o>) : (obj -> WorkflowContext -> Task<obj>) =
        let exec = stepToExecutor "Fallback" step
        fun (input: obj) (ctx: WorkflowContext) -> task {
            let typedInput = input :?> 'i
            let! result = exec.Execute typedInput ctx
            return result :> obj
        }

    /// Wraps a typed executor as an untyped step
    let wrapExecutor<'i, 'o> (durableId: string) (exec: Executor<'i, 'o>) : WorkflowStep =
        Step (durableId, exec.Name, fun input ctx -> task {
            let typedInput = input :?> 'i
            let! result = exec.Execute typedInput ctx
            return result :> obj
        })

    /// Wraps a typed router function as an untyped route step
    /// The router takes input and returns an executor to run on that same input
    let wrapRouter<'a, 'b> (durableId: string) (router: 'a -> Executor<'a, 'b>) : WorkflowStep =
        Route (durableId, fun input ctx -> task {
            let typedInput = input :?> 'a
            let selectedExecutor = router typedInput
            let! result = selectedExecutor.Execute typedInput ctx
            return result :> obj
        })

    /// Wraps a list of typed executors as untyped parallel functions
    let wrapParallel<'i, 'o> (executors: Executor<'i, 'o> list) : WorkflowStep =
        let branches =
            executors
            |> List.map (fun exec ->
                let durableId = DurableId.forExecutor exec
                let executor = fun (input: obj) (ctx: WorkflowContext) -> task {
                    let typedInput = input :?> 'i
                    let! result = exec.Execute typedInput ctx
                    return result :> obj
                }
                (durableId, executor))
        Parallel branches

    /// Wraps an aggregator executor, handling the obj list → typed list conversion
    let wrapFanIn<'elem, 'o> (exec: Executor<'elem list, 'o>) : WorkflowStep =
        let durableId = DurableId.forExecutor exec
        Step (durableId, exec.Name, fun input ctx -> task {
            // Input is obj list from parallel, convert each element to the expected type
            let objList = input :?> obj list
            let typedList = objList |> List.map (fun o -> o :?> 'elem)
            let! result = exec.Execute typedList ctx
            return result :> obj
        })

    /// Modifies the last step with a resilience update function
    let modifyLastWithResilience (updateSettings: ResilienceSettings -> ResilienceSettings) (steps: WorkflowStep list) : WorkflowStep list =
        match List.rev steps with
        | [] -> failwith "No previous step to modify"
        | last :: rest ->
            let newLast =
                match last with
                | Resilient (settings, inner) ->
                    // Already wrapped - update the settings
                    Resilient (updateSettings settings, inner)
                | other ->
                    // Wrap with new resilience settings
                    Resilient (updateSettings ResilienceSettings.defaults, other)
            List.rev (newLast :: rest)


/// Builder for the workflow computation expression
type WorkflowBuilder() =

    member _.Yield(_) : WorkflowState<'a, 'a> = { Steps = [] }

    // ============ STEP OPERATIONS ============
    // Uses inline SRTP to accept Task fn, Async fn, TypedAgent, Executor, Workflow, or Step directly
    // Durable IDs are auto-generated; display names come from Executor.Name if available

    /// Adds first step - establishes workflow input/output types (uses SRTP for type resolution)
    [<CustomOperation("step")>]
    member inline _.StepFirst(state: WorkflowState<_, _>, x: ^T) : WorkflowState<'i, 'o> =
        let step : Step<'i, 'o> = ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'i, 'o>) (StepConv, x))
        let durableId = WorkflowInternal.getDurableId step
        let displayName = WorkflowInternal.getDisplayName step
        WorkflowInternal.warnIfLambda step durableId
        { Steps = state.Steps @ [WorkflowInternal.wrapStep durableId displayName step] }

    /// Adds subsequent step - threads input type through (uses SRTP for type resolution)
    [<CustomOperation("step")>]
    member inline _.Step(state: WorkflowState<'input, 'middle>, x: ^T) : WorkflowState<'input, 'output> =
        let step : Step<'middle, 'output> = ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'middle, 'output>) (StepConv, x))
        let durableId = WorkflowInternal.getDurableId step
        let displayName = WorkflowInternal.getDisplayName step
        WorkflowInternal.warnIfLambda step durableId
        { Steps = state.Steps @ [WorkflowInternal.wrapStep durableId displayName step] }

    // ============ ROUTING ============

    /// Routes to different executors based on the previous step's output.
    /// Uses SRTP to accept Task fn, Async fn, TypedAgent, Executor, or Step directly.
    /// Durable IDs are auto-generated for each branch.
    [<CustomOperation("route")>]
    member inline _.Route(state: WorkflowState<'input, 'middle>, router: 'middle -> ^T) : WorkflowState<'input, 'output> =
        // Generate a durable ID for the route decision point based on input/output types
        let routeDurableId = DurableId.forRoute router
        let wrappedRouter = fun (input: 'middle) ->
            let result = router input
            let step : Step<'middle, 'output> =
                ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'middle, 'output>) (StepConv, result))
            let displayName = WorkflowInternal.getDisplayName step
            WorkflowInternal.warnIfLambda step (WorkflowInternal.getDurableId step)
            WorkflowInternal.stepToExecutor displayName step
        { Steps = state.Steps @ [WorkflowInternal.wrapRouter routeDurableId wrappedRouter] }

    // ============ FANOUT OPERATIONS ============
    // SRTP overloads for 2-5 arguments - no wrapper needed!
    // For 6+ branches, use: fanOut [+fn1; +fn2; ...]
    // Durable IDs are auto-generated for each parallel branch

    /// Runs 2 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, x1: ^A, x2: ^B) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        { Steps = state.Steps @ [WorkflowInternal.wrapStepParallel [s1; s2]] }

    /// Runs 3 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, x1: ^A, x2: ^B, x3: ^C) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let s3 : Step<'middle, 'o> = ((^C or StepConv) : (static member ToStep: StepConv * ^C -> Step<'middle, 'o>) (StepConv, x3))
        { Steps = state.Steps @ [WorkflowInternal.wrapStepParallel [s1; s2; s3]] }

    /// Runs 4 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, x1: ^A, x2: ^B, x3: ^C, x4: ^D) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let s3 : Step<'middle, 'o> = ((^C or StepConv) : (static member ToStep: StepConv * ^C -> Step<'middle, 'o>) (StepConv, x3))
        let s4 : Step<'middle, 'o> = ((^D or StepConv) : (static member ToStep: StepConv * ^D -> Step<'middle, 'o>) (StepConv, x4))
        { Steps = state.Steps @ [WorkflowInternal.wrapStepParallel [s1; s2; s3; s4]] }

    /// Runs 5 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, x1: ^A, x2: ^B, x3: ^C, x4: ^D, x5: ^E) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let s3 : Step<'middle, 'o> = ((^C or StepConv) : (static member ToStep: StepConv * ^C -> Step<'middle, 'o>) (StepConv, x3))
        let s4 : Step<'middle, 'o> = ((^D or StepConv) : (static member ToStep: StepConv * ^D -> Step<'middle, 'o>) (StepConv, x4))
        let s5 : Step<'middle, 'o> = ((^E or StepConv) : (static member ToStep: StepConv * ^E -> Step<'middle, 'o>) (StepConv, x5))
        { Steps = state.Steps @ [WorkflowInternal.wrapStepParallel [s1; s2; s3; s4; s5]] }

    /// Runs multiple steps in parallel (fan-out) - for 6+ branches, use '+' operator
    [<CustomOperation("fanOut")>]
    member _.FanOut(state: WorkflowState<'input, 'middle>, steps: Step<'middle, 'o> list) : WorkflowState<'input, 'o list> =
        { Steps = state.Steps @ [WorkflowInternal.wrapStepParallel steps] }

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
        { Steps = state.Steps @ [WorkflowInternal.wrapStepFanIn durableId displayName step] }

    // ============ RESILIENCE OPERATIONS ============

    /// Sets retry count for the previous step
    [<CustomOperation("retry")>]
    member _.Retry(state: WorkflowState<'input, 'output>, count: int) : WorkflowState<'input, 'output> =
        { state with Steps = state.Steps |> WorkflowInternal.modifyLastWithResilience (fun s -> { s with RetryCount = count }) }

    /// Sets backoff strategy for retries on the previous step
    [<CustomOperation("backoff")>]
    member _.Backoff(state: WorkflowState<'input, 'output>, strategy: BackoffStrategy) : WorkflowState<'input, 'output> =
        { state with Steps = state.Steps |> WorkflowInternal.modifyLastWithResilience (fun s -> { s with Backoff = strategy }) }

    /// Sets timeout for the previous step
    [<CustomOperation("timeout")>]
    member _.Timeout(state: WorkflowState<'input, 'output>, duration: TimeSpan) : WorkflowState<'input, 'output> =
        { state with Steps = state.Steps |> WorkflowInternal.modifyLastWithResilience (fun s -> { s with Timeout = Some duration }) }

    // ============ FALLBACK OPERATIONS ============
    // Uses inline SRTP to accept Task fn, Async fn, TypedAgent, Executor, or Step directly

    /// Sets fallback for the previous step (uses SRTP for type resolution)
    [<CustomOperation("fallback")>]
    member inline _.Fallback(state: WorkflowState<'input, 'output>, x: ^T) : WorkflowState<'input, 'output> =
        let step : Step<'middle, 'output> = ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'middle, 'output>) (StepConv, x))
        let fallbackFn = WorkflowInternal.stepToFallback step
        { state with Steps = state.Steps |> WorkflowInternal.modifyLastWithResilience (fun s -> { s with Fallback = Some fallbackFn }) }

    /// Builds the final workflow definition
    member _.Run(state: WorkflowState<'input, 'output>) : WorkflowDef<'input, 'output> =
        { Name = None; Steps = state.Steps }


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

    /// Calculates delay for a given retry attempt based on backoff strategy
    let private calculateDelay (strategy: BackoffStrategy) (attempt: int) : TimeSpan =
        match strategy with
        | Immediate -> TimeSpan.Zero
        | Linear delay -> delay
        | Exponential (initial, multiplier) ->
            let factor = Math.Pow(multiplier, float (attempt - 1))
            TimeSpan.FromMilliseconds(initial.TotalMilliseconds * factor)

    /// Executes a task operation with timeout
    let private withTimeout (timeout: TimeSpan) (operation: Task<'a>) : Task<'a> =
        task {
            use cts = new System.Threading.CancellationTokenSource()
            let timeoutTask = Task.Delay(timeout, cts.Token)
            let! completed = Task.WhenAny(operation, timeoutTask)

            if Object.ReferenceEquals(completed, timeoutTask) then
                return raise (TimeoutException("Operation timed out"))
            else
                cts.Cancel()
                return! operation
        }

    /// Executes an inner step (non-resilient)
    let rec private executeInnerStep (step: WorkflowStep) (input: obj) (ctx: WorkflowContext) : Task<obj> =
        task {
            match step with
            | Step (_, _, execute) ->
                return! execute input ctx
            | Route (_, router) ->
                return! router input ctx
            | Parallel branches ->
                let! results =
                    branches
                    |> List.map (fun (_, exec) -> exec input ctx)
                    |> Task.WhenAll
                return (results |> Array.toList) :> obj
            | Resilient (settings, inner) ->
                return! executeWithResilience settings inner input ctx
        }

    /// Executes a step with resilience (retry, timeout, fallback)
    and private executeWithResilience (settings: ResilienceSettings) (inner: WorkflowStep) (input: obj) (ctx: WorkflowContext) : Task<obj> =
        let rec attemptWithRetry (remainingRetries: int) (attempt: int) : Task<obj> =
            task {
                try
                    // Apply timeout if specified
                    let operation = executeInnerStep inner input ctx
                    let timedOperation =
                        match settings.Timeout with
                        | Some t -> withTimeout t operation
                        | None -> operation

                    return! timedOperation
                with ex ->
                    if remainingRetries > 0 then
                        // Calculate and apply backoff delay
                        let delay = calculateDelay settings.Backoff attempt
                        if delay > TimeSpan.Zero then
                            do! Task.Delay (int delay.TotalMilliseconds)

                        // Retry
                        return! attemptWithRetry (remainingRetries - 1) (attempt + 1)
                    else
                        // All retries exhausted - try fallback or rethrow
                        match settings.Fallback with
                        | Some fallbackFn ->
                            return! fallbackFn input ctx
                        | None ->
                            return raise ex
            }

        attemptWithRetry settings.RetryCount 1

    // Initialize the forward reference so nested workflows can execute
    do WorkflowInternal.executeWorkflowStepRef <- Some executeInnerStep

    /// Runs a workflow with the given input and context
    let runWithContext<'input, 'output> (input: 'input) (ctx: WorkflowContext) (workflow: WorkflowDef<'input, 'output>) : Task<'output> =
        task {
            let mutable current: obj = input :> obj

            for step in workflow.Steps do
                let! result = executeInnerStep step current ctx
                current <- result

            return current :?> 'output
        }

    /// Runs a workflow with the given input (creates a new context)
    let run<'input, 'output> (input: 'input) (workflow: WorkflowDef<'input, 'output>) : Task<'output> =
        let ctx = WorkflowContext.create ()
        runWithContext input ctx workflow

    /// Runs a workflow synchronously
    let runSync<'input, 'output> (input: 'input) (workflow: WorkflowDef<'input, 'output>) : 'output =
        (workflow |> run input).GetAwaiter().GetResult()

    /// Converts a workflow to an executor (enables workflow composition)
    let toExecutor<'input, 'output> (name: string) (workflow: WorkflowDef<'input, 'output>) : Executor<'input, 'output> =
        {
            Name = name
            Execute = fun input ctx -> runWithContext input ctx workflow
        }

    /// Sets the name of a workflow (required for MAF compilation)
    let withName<'input, 'output> (name: string) (workflow: WorkflowDef<'input, 'output>) : WorkflowDef<'input, 'output> =
        { workflow with Name = Some name }

    // ============ MAF COMPILATION ============
    // Note: toMAF is available in the AgentNet.Durable package
