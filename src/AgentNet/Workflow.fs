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

    /// Creates an executor from an AgentNet ChatAgent
    let fromAgent (name: string) (agent: ChatAgent) : Executor<string, string> =
        {
            Name = name
            Execute = fun input _ -> agent.Chat input
        }


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
}


/// Internal module for workflow building
module internal WorkflowInternal =

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

    member _.Yield(_) : WorkflowState<'a, 'a> = { Steps = [] }

    /// Starts the workflow with an executor
    [<CustomOperation("start")>]
    member _.Start(state: WorkflowState<_, _>, executor: Executor<'i, 'o>) : WorkflowState<'i, 'o> =
        { Steps = state.Steps @ [WorkflowInternal.wrapExecutor executor] }

    /// Adds the next step to the workflow
    /// The executor's input type must match the previous step's output type
    [<CustomOperation("next")>]
    member _.Next(state: WorkflowState<'input, 'middle>, executor: Executor<'middle, 'output>) : WorkflowState<'input, 'output> =
        { Steps = state.Steps @ [WorkflowInternal.wrapExecutor executor] }

    /// Routes to different executors based on the previous step's output
    /// Use with pattern matching: route (function | CaseA -> exec1 | CaseB -> exec2)
    [<CustomOperation("route")>]
    member _.Route(state: WorkflowState<'input, 'middle>, router: 'middle -> Executor<'middle, 'output>) : WorkflowState<'input, 'output> =
        { Steps = state.Steps @ [WorkflowInternal.wrapRouter router] }

    /// Runs multiple executors in parallel on the same input (fan-out)
    /// Output becomes a list of all results
    [<CustomOperation("fanOut")>]
    member _.FanOut(state: WorkflowState<'input, 'middle>, executors: Executor<'middle, 'o> list) : WorkflowState<'input, 'o list> =
        { Steps = state.Steps @ [WorkflowInternal.wrapParallel executors] }

    /// Aggregates parallel results into a single output (fan-in)
    /// Converts the obj list from fanOut into a typed list for the executor
    [<CustomOperation("fanIn")>]
    member _.FanIn(state: WorkflowState<'input, 'elem list>, executor: Executor<'elem list, 'output>) : WorkflowState<'input, 'output> =
        { Steps = state.Steps @ [WorkflowInternal.wrapFanIn executor] }

    /// Sets retry count for the previous step
    [<CustomOperation("retry")>]
    member _.Retry(state: WorkflowState<'input, 'output>, count: int) : WorkflowState<'input, 'output> =
        { Steps = state.Steps |> WorkflowInternal.modifyLastWithResilience (fun s -> { s with RetryCount = count }) }

    /// Sets backoff strategy for retries on the previous step
    [<CustomOperation("backoff")>]
    member _.Backoff(state: WorkflowState<'input, 'output>, strategy: BackoffStrategy) : WorkflowState<'input, 'output> =
        { Steps = state.Steps |> WorkflowInternal.modifyLastWithResilience (fun s -> { s with Backoff = strategy }) }

    /// Sets timeout for the previous step
    [<CustomOperation("timeout")>]
    member _.Timeout(state: WorkflowState<'input, 'output>, duration: TimeSpan) : WorkflowState<'input, 'output> =
        { Steps = state.Steps |> WorkflowInternal.modifyLastWithResilience (fun s -> { s with Timeout = Some duration }) }

    /// Sets fallback executor for the previous step (used if all retries fail)
    /// The fallback executor must have matching input/output types
    [<CustomOperation("fallback")>]
    member _.Fallback(state: WorkflowState<'input, 'output>, executor: Executor<'middle, 'output>) : WorkflowState<'input, 'output> =
        let fallbackFn (input: obj) (ctx: WorkflowContext) : Task<obj> = task {
            let typedInput = unbox<'middle> input
            let! result = executor.Execute typedInput ctx
            return box result
        }
        { Steps = state.Steps |> WorkflowInternal.modifyLastWithResilience (fun s -> { s with Fallback = Some fallbackFn }) }

    /// Builds the final workflow definition
    member _.Run(state: WorkflowState<'input, 'output>) : WorkflowDef<'input, 'output> =
        { Steps = state.Steps }


/// The workflow computation expression builder instance
[<AutoOpen>]
module WorkflowCE =
    let workflow = WorkflowBuilder()


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
