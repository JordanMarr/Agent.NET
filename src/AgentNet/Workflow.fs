namespace AgentNet

open System
open System.Threading.Tasks

/// Context passed to executors during workflow execution
type WorkflowContext = {
    /// Unique identifier for this workflow run
    RunId: Guid
    /// Shared state dictionary for passing data between executors
    State: Map<string, obj>
}

module WorkflowContext =
    /// Creates a new empty workflow context
    let create () = {
        RunId = Guid.NewGuid()
        State = Map.empty
    }

    /// Gets a typed value from the context state
    let tryGet<'T> (key: string) (ctx: WorkflowContext) : 'T option =
        ctx.State
        |> Map.tryFind key
        |> Option.bind (fun v ->
            match v with
            | :? 'T as typed -> Some typed
            | _ -> None)

    /// Sets a value in the context state
    let set (key: string) (value: obj) (ctx: WorkflowContext) : WorkflowContext =
        { ctx with State = ctx.State |> Map.add key value }


/// An executor that transforms input to output within a workflow
type Executor<'input, 'output> = {
    Name: string
    Execute: 'input -> WorkflowContext -> Task<'output>
}

/// Module for creating executors
[<RequireQualifiedAccess>]
module Executor =

    /// Creates an executor from a simple function
    let fromFn (name: string) (fn: 'input -> 'output) : Executor<'input, 'output> =
        {
            Name = name
            Execute = fun input _ -> task { return fn input }
        }

    /// Creates an executor from a Task function (C#-friendly)
    let fromTask (name: string) (fn: 'input -> Task<'output>) : Executor<'input, 'output> =
        {
            Name = name
            Execute = fun input _ -> fn input
        }

    /// Creates an executor from an F# Async function
    let fromAsync (name: string) (fn: 'input -> Async<'output>) : Executor<'input, 'output> =
        {
            Name = name
            Execute = fun input _ -> fn input |> Async.StartAsTask
        }

    /// Creates an executor from a function that takes context
    let create (name: string) (fn: 'input -> WorkflowContext -> Task<'output>) : Executor<'input, 'output> =
        {
            Name = name
            Execute = fn
        }

    /// Creates a typed executor from a TypedAgent
    let fromTypedAgent (name: string) (agent: TypedAgent<'input, 'output>) : Executor<'input, 'output> =
        {
            Name = name
            Execute = fun input _ -> TypedAgent.invoke input agent
        }


/// A workflow step type that unifies Task functions, Async functions, TypedAgents, and Executors.
/// This enables clean workflow syntax and mixed-type fanOut operations.
type Step<'i, 'o> =
    | TaskStep of ('i -> Task<'o>)
    | AsyncStep of ('i -> Async<'o>)
    | AgentStep of TypedAgent<'i, 'o>
    | ExecutorStep of Executor<'i, 'o>


/// SRTP witness type for converting various types to Step.
/// Uses the type class pattern to enable inline resolution at call sites.
type StepConv = StepConv with
    static member inline ToStep(_: StepConv, fn: 'i -> Task<'o>) : Step<'i, 'o> = TaskStep fn
    static member inline ToStep(_: StepConv, fn: 'i -> Async<'o>) : Step<'i, 'o> = AsyncStep fn
    static member inline ToStep(_: StepConv, agent: TypedAgent<'i, 'o>) : Step<'i, 'o> = AgentStep agent
    static member inline ToStep(_: StepConv, exec: Executor<'i, 'o>) : Step<'i, 'o> = ExecutorStep exec
    static member inline ToStep(_: StepConv, step: Step<'i, 'o>) : Step<'i, 'o> = step  // Passthrough


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

/// A step in a workflow pipeline
type WorkflowStep =
    | Step of name: string * execute: (obj -> WorkflowContext -> Task<obj>)
    | Route of router: (obj -> WorkflowContext -> Task<obj>)
    | Parallel of executors: (obj -> WorkflowContext -> Task<obj>) list
    | Resilient of settings: ResilienceSettings * inner: WorkflowStep


/// A workflow definition that can be executed
type WorkflowDef<'input, 'output> = {
    /// The steps in the workflow, in order
    Steps: WorkflowStep list
}

/// Internal state carrier that threads type information through the builder
type WorkflowState<'input, 'output> = {
    Steps: WorkflowStep list
    StepCount: int
}


/// Module for workflow building internals (public for inline SRTP support)
[<AutoOpen>]
module WorkflowInternal =

    /// Converts a Step<'i, 'o> to an Executor<'i, 'o>
    let stepToExecutor<'i, 'o> (name: string) (step: Step<'i, 'o>) : Executor<'i, 'o> =
        match step with
        | TaskStep fn -> Executor.fromTask name fn
        | AsyncStep fn -> Executor.fromAsync name fn
        | AgentStep agent -> Executor.fromTypedAgent name agent
        | ExecutorStep exec -> { exec with Name = name }

    /// Wraps a Step as an untyped WorkflowStep
    let wrapStep<'i, 'o> (name: string) (step: Step<'i, 'o>) : WorkflowStep =
        let exec = stepToExecutor name step
        Step (exec.Name, fun input ctx -> task {
            let typedInput = input :?> 'i
            let! result = exec.Execute typedInput ctx
            return result :> obj
        })

    /// Wraps a list of Steps as parallel execution (supports mixed types!)
    let wrapStepParallel<'i, 'o> (steps: Step<'i, 'o> list) : WorkflowStep =
        let wrappedFns =
            steps
            |> List.mapi (fun i step ->
                let exec = stepToExecutor $"Parallel {i+1}" step
                fun (input: obj) (ctx: WorkflowContext) -> task {
                    let typedInput = input :?> 'i
                    let! result = exec.Execute typedInput ctx
                    return result :> obj
                })
        Parallel wrappedFns

    /// Wraps a list of Steps as parallel execution with a name prefix
    let wrapStepParallelNamed<'i, 'o> (namePrefix: string) (steps: Step<'i, 'o> list) : WorkflowStep =
        let wrappedFns =
            steps
            |> List.mapi (fun i step ->
                let exec = stepToExecutor $"{namePrefix} {i+1}" step
                fun (input: obj) (ctx: WorkflowContext) -> task {
                    let typedInput = input :?> 'i
                    let! result = exec.Execute typedInput ctx
                    return result :> obj
                })
        Parallel wrappedFns

    /// Wraps a Step as a fan-in aggregator, handling obj list → typed list conversion
    let wrapStepFanIn<'elem, 'o> (name: string) (step: Step<'elem list, 'o>) : WorkflowStep =
        let exec = stepToExecutor name step
        Step (exec.Name, fun input ctx -> task {
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
    let wrapExecutor<'i, 'o> (exec: Executor<'i, 'o>) : WorkflowStep =
        Step (exec.Name, fun input ctx -> task {
            let typedInput = input :?> 'i
            let! result = exec.Execute typedInput ctx
            return result :> obj
        })

    /// Wraps a typed router function as an untyped route step
    /// The router takes input and returns an executor to run on that same input
    let wrapRouter<'a, 'b> (router: 'a -> Executor<'a, 'b>) : WorkflowStep =
        Route (fun input ctx -> task {
            let typedInput = input :?> 'a
            let selectedExecutor = router typedInput
            let! result = selectedExecutor.Execute typedInput ctx
            return result :> obj
        })

    /// Wraps a list of typed executors as untyped parallel functions
    let wrapParallel<'i, 'o> (executors: Executor<'i, 'o> list) : WorkflowStep =
        let wrappedFns =
            executors
            |> List.map (fun exec ->
                fun (input: obj) (ctx: WorkflowContext) -> task {
                    let typedInput = input :?> 'i
                    let! result = exec.Execute typedInput ctx
                    return result :> obj
                })
        Parallel wrappedFns

    /// Wraps an aggregator executor, handling the obj list → typed list conversion
    let wrapFanIn<'elem, 'o> (exec: Executor<'elem list, 'o>) : WorkflowStep =
        Step (exec.Name, fun input ctx -> task {
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

    member _.Yield(_) : WorkflowState<'a, 'a> = { Steps = []; StepCount = 0 }

    // ============ START OPERATIONS ============
    // Uses inline SRTP to accept Task fn, Async fn, TypedAgent, Executor, or Step directly

    /// Starts workflow with any supported step type (uses SRTP for type resolution)
    [<CustomOperation("start")>]
    member inline _.Start(state: WorkflowState<_, _>, x: ^T) : WorkflowState<'i, 'o> =
        let step : Step<'i, 'o> = ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'i, 'o>) (StepConv, x))
        let name = $"Step {state.StepCount + 1}"
        { Steps = state.Steps @ [WorkflowInternal.wrapStep name step]; StepCount = state.StepCount + 1 }

    /// Starts workflow with a named step
    [<CustomOperation("start")>]
    member inline _.Start(state: WorkflowState<_, _>, name: string, x: ^T) : WorkflowState<'i, 'o> =
        let step : Step<'i, 'o> = ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'i, 'o>) (StepConv, x))
        { Steps = state.Steps @ [WorkflowInternal.wrapStep name step]; StepCount = state.StepCount + 1 }

    // ============ NEXT OPERATIONS ============
    // Uses inline SRTP to accept Task fn, Async fn, TypedAgent, Executor, or Step directly

    /// Adds any supported step type as next step (uses SRTP for type resolution)
    [<CustomOperation("next")>]
    member inline _.Next(state: WorkflowState<'input, 'middle>, x: ^T) : WorkflowState<'input, 'output> =
        let step : Step<'middle, 'output> = ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'middle, 'output>) (StepConv, x))
        let name = $"Step {state.StepCount + 1}"
        { Steps = state.Steps @ [WorkflowInternal.wrapStep name step]; StepCount = state.StepCount + 1 }

    /// Adds a named step as next step
    [<CustomOperation("next")>]
    member inline _.Next(state: WorkflowState<'input, 'middle>, name: string, x: ^T) : WorkflowState<'input, 'output> =
        let step : Step<'middle, 'output> = ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'middle, 'output>) (StepConv, x))
        { Steps = state.Steps @ [WorkflowInternal.wrapStep name step]; StepCount = state.StepCount + 1 }

    // ============ ROUTING ============

    /// Routes to different executors based on the previous step's output
    /// Use with pattern matching: route (function | CaseA -> exec1 | CaseB -> exec2)
    [<CustomOperation("route")>]
    member _.Route(state: WorkflowState<'input, 'middle>, router: 'middle -> Executor<'middle, 'output>) : WorkflowState<'input, 'output> =
        { Steps = state.Steps @ [WorkflowInternal.wrapRouter router]; StepCount = state.StepCount + 1 }

    // ============ FANOUT OPERATIONS ============
    // SRTP overloads for 2-5 arguments - no wrapper needed!
    // For 6+ branches, use: fanOut [s fn1; s fn2; ...]

    /// Runs 2 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, x1: ^A, x2: ^B) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        { Steps = state.Steps @ [WorkflowInternal.wrapStepParallel [s1; s2]]; StepCount = state.StepCount + 1 }

    /// Runs 3 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, x1: ^A, x2: ^B, x3: ^C) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let s3 : Step<'middle, 'o> = ((^C or StepConv) : (static member ToStep: StepConv * ^C -> Step<'middle, 'o>) (StepConv, x3))
        { Steps = state.Steps @ [WorkflowInternal.wrapStepParallel [s1; s2; s3]]; StepCount = state.StepCount + 1 }

    /// Runs 4 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, x1: ^A, x2: ^B, x3: ^C, x4: ^D) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let s3 : Step<'middle, 'o> = ((^C or StepConv) : (static member ToStep: StepConv * ^C -> Step<'middle, 'o>) (StepConv, x3))
        let s4 : Step<'middle, 'o> = ((^D or StepConv) : (static member ToStep: StepConv * ^D -> Step<'middle, 'o>) (StepConv, x4))
        { Steps = state.Steps @ [WorkflowInternal.wrapStepParallel [s1; s2; s3; s4]]; StepCount = state.StepCount + 1 }

    /// Runs 5 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, x1: ^A, x2: ^B, x3: ^C, x4: ^D, x5: ^E) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let s3 : Step<'middle, 'o> = ((^C or StepConv) : (static member ToStep: StepConv * ^C -> Step<'middle, 'o>) (StepConv, x3))
        let s4 : Step<'middle, 'o> = ((^D or StepConv) : (static member ToStep: StepConv * ^D -> Step<'middle, 'o>) (StepConv, x4))
        let s5 : Step<'middle, 'o> = ((^E or StepConv) : (static member ToStep: StepConv * ^E -> Step<'middle, 'o>) (StepConv, x5))
        { Steps = state.Steps @ [WorkflowInternal.wrapStepParallel [s1; s2; s3; s4; s5]]; StepCount = state.StepCount + 1 }

    /// Runs multiple steps in parallel (fan-out) - for 6+ branches, use 's' helper
    [<CustomOperation("fanOut")>]
    member _.FanOut(state: WorkflowState<'input, 'middle>, steps: Step<'middle, 'o> list) : WorkflowState<'input, 'o list> =
        { Steps = state.Steps @ [WorkflowInternal.wrapStepParallel steps]; StepCount = state.StepCount + 1 }

    // Named fanOut overloads - name is used as prefix for parallel step names

    /// Runs 2 named steps in parallel (fan-out)
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, name: string, x1: ^A, x2: ^B) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        { Steps = state.Steps @ [WorkflowInternal.wrapStepParallelNamed name [s1; s2]]; StepCount = state.StepCount + 1 }

    /// Runs 3 named steps in parallel (fan-out)
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, name: string, x1: ^A, x2: ^B, x3: ^C) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let s3 : Step<'middle, 'o> = ((^C or StepConv) : (static member ToStep: StepConv * ^C -> Step<'middle, 'o>) (StepConv, x3))
        { Steps = state.Steps @ [WorkflowInternal.wrapStepParallelNamed name [s1; s2; s3]]; StepCount = state.StepCount + 1 }

    /// Runs 4 named steps in parallel (fan-out)
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, name: string, x1: ^A, x2: ^B, x3: ^C, x4: ^D) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let s3 : Step<'middle, 'o> = ((^C or StepConv) : (static member ToStep: StepConv * ^C -> Step<'middle, 'o>) (StepConv, x3))
        let s4 : Step<'middle, 'o> = ((^D or StepConv) : (static member ToStep: StepConv * ^D -> Step<'middle, 'o>) (StepConv, x4))
        { Steps = state.Steps @ [WorkflowInternal.wrapStepParallelNamed name [s1; s2; s3; s4]]; StepCount = state.StepCount + 1 }

    /// Runs 5 named steps in parallel (fan-out)
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, name: string, x1: ^A, x2: ^B, x3: ^C, x4: ^D, x5: ^E) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let s3 : Step<'middle, 'o> = ((^C or StepConv) : (static member ToStep: StepConv * ^C -> Step<'middle, 'o>) (StepConv, x3))
        let s4 : Step<'middle, 'o> = ((^D or StepConv) : (static member ToStep: StepConv * ^D -> Step<'middle, 'o>) (StepConv, x4))
        let s5 : Step<'middle, 'o> = ((^E or StepConv) : (static member ToStep: StepConv * ^E -> Step<'middle, 'o>) (StepConv, x5))
        { Steps = state.Steps @ [WorkflowInternal.wrapStepParallelNamed name [s1; s2; s3; s4; s5]]; StepCount = state.StepCount + 1 }

    // ============ FANIN OPERATIONS ============
    // Uses inline SRTP to accept Task fn, Async fn, TypedAgent, Executor, or Step directly

    /// Aggregates parallel results with any supported step type (uses SRTP for type resolution)
    [<CustomOperation("fanIn")>]
    member inline _.FanIn(state: WorkflowState<'input, 'elem list>, x: ^T) : WorkflowState<'input, 'output> =
        let step : Step<'elem list, 'output> = ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'elem list, 'output>) (StepConv, x))
        let name = $"Step {state.StepCount + 1}"
        { Steps = state.Steps @ [WorkflowInternal.wrapStepFanIn name step]; StepCount = state.StepCount + 1 }

    /// Aggregates parallel results with a named step
    [<CustomOperation("fanIn")>]
    member inline _.FanIn(state: WorkflowState<'input, 'elem list>, name: string, x: ^T) : WorkflowState<'input, 'output> =
        let step : Step<'elem list, 'output> = ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'elem list, 'output>) (StepConv, x))
        { Steps = state.Steps @ [WorkflowInternal.wrapStepFanIn name step]; StepCount = state.StepCount + 1 }

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
        { Steps = state.Steps }


/// The workflow computation expression builder instance
[<AutoOpen>]
module WorkflowCE =
    let workflow = WorkflowBuilder()

    /// Alias for Task.FromResult - lifts sync values to Task for workflow compatibility
    let toTask = Task.FromResult

    /// Converts any supported type to Step<'i, 'o>.
    /// Supports: Task fn, Async fn, TypedAgent, Executor, or Step passthrough.
    /// Primarily used for fanOut lists with 6+ branches: fanOut [step fn1; step fn2; ...]
    let inline step (x: ^T) : Step<'i, 'o> =
        ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'i, 'o>) (StepConv, x))

    /// Prefix operator shorthand for 'step'. Converts any supported type to Step<'i, 'o>.
    /// Example: fanOut [+fn1; +fn2; +fn3; +fn4; +fn5; +fn6]
    let inline (~+) (x: ^T) : Step<'i, 'o> = step x


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
            | Step (_, execute) ->
                return! execute input ctx
            | Route router ->
                return! router input ctx
            | Parallel executors ->
                let! results =
                    executors
                    |> List.map (fun exec -> exec input ctx)
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
