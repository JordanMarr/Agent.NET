namespace AgentNet

open System

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
    Execute: 'input -> WorkflowContext -> Async<'output>
}

/// Module for creating executors
[<RequireQualifiedAccess>]
module Executor =

    /// Creates an executor from a simple function
    let fromFn (name: string) (fn: 'input -> 'output) : Executor<'input, 'output> =
        {
            Name = name
            Execute = fun input _ -> async { return fn input }
        }

    /// Creates an executor from an async function
    let fromAsync (name: string) (fn: 'input -> Async<'output>) : Executor<'input, 'output> =
        {
            Name = name
            Execute = fun input _ -> fn input
        }

    /// Creates an executor from a function that takes context
    let create (name: string) (fn: 'input -> WorkflowContext -> Async<'output>) : Executor<'input, 'output> =
        {
            Name = name
            Execute = fn
        }

    /// Creates an executor from an AgentNet Agent
    let fromAgent (name: string) (agent: Agent) : Executor<string, string> =
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
    Fallback: (obj -> WorkflowContext -> Async<obj>) option
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
    | Step of name: string * execute: (obj -> WorkflowContext -> Async<obj>)
    | Route of router: (obj -> WorkflowContext -> Async<obj>)
    | Parallel of executors: (obj -> WorkflowContext -> Async<obj>) list
    | Resilient of settings: ResilienceSettings * inner: WorkflowStep


/// A workflow definition that can be executed
type WorkflowDef<'input, 'output> = {
    /// The steps in the workflow, in order
    Steps: WorkflowStep list
}


/// Internal module for workflow building
module internal WorkflowInternal =

    /// Wraps a typed executor as an untyped step
    let wrapExecutor<'i, 'o> (exec: Executor<'i, 'o>) : WorkflowStep =
        Step (exec.Name, fun input ctx -> async {
            let typedInput = input :?> 'i
            let! result = exec.Execute typedInput ctx
            return result :> obj
        })

    /// Wraps a typed router function as an untyped route step
    /// The router takes input and returns an executor to run on that same input
    let wrapRouter<'a, 'b> (router: 'a -> Executor<'a, 'b>) : WorkflowStep =
        Route (fun input ctx -> async {
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
                fun (input: obj) (ctx: WorkflowContext) -> async {
                    let typedInput = input :?> 'i
                    let! result = exec.Execute typedInput ctx
                    return result :> obj
                })
        Parallel wrappedFns

    /// Wraps an aggregator executor, handling the obj list → typed list conversion
    let wrapFanIn<'elem, 'o> (exec: Executor<'elem list, 'o>) : WorkflowStep =
        Step (exec.Name, fun input ctx -> async {
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

    member _.Yield(_) : WorkflowStep list = []

    /// Starts the workflow with an executor
    [<CustomOperation("start")>]
    member _.Start<'i, 'o>(steps: WorkflowStep list, executor: Executor<'i, 'o>) : WorkflowStep list =
        steps @ [WorkflowInternal.wrapExecutor executor]

    /// Adds the next step to the workflow
    [<CustomOperation("next")>]
    member _.Next<'i, 'o>(steps: WorkflowStep list, executor: Executor<'i, 'o>) : WorkflowStep list =
        steps @ [WorkflowInternal.wrapExecutor executor]

    /// Routes to different executors based on the previous step's output
    /// Use with pattern matching: route (function | CaseA -> exec1 | CaseB -> exec2)
    [<CustomOperation("route")>]
    member _.Route<'a, 'b>(steps: WorkflowStep list, router: 'a -> Executor<'a, 'b>) : WorkflowStep list =
        steps @ [WorkflowInternal.wrapRouter router]

    /// Runs multiple executors in parallel on the same input (fan-out)
    /// Output becomes a list of all results
    [<CustomOperation("fanOut")>]
    member _.FanOut<'i, 'o>(steps: WorkflowStep list, executors: Executor<'i, 'o> list) : WorkflowStep list =
        steps @ [WorkflowInternal.wrapParallel executors]

    /// Aggregates parallel results into a single output (fan-in)
    /// Converts the obj list from fanOut into a typed list for the executor
    [<CustomOperation("fanIn")>]
    member _.FanIn<'elem, 'o>(steps: WorkflowStep list, executor: Executor<'elem list, 'o>) : WorkflowStep list =
        steps @ [WorkflowInternal.wrapFanIn executor]

    /// Sets retry count for the previous step
    [<CustomOperation("retry")>]
    member _.Retry(steps: WorkflowStep list, count: int) : WorkflowStep list =
        steps |> WorkflowInternal.modifyLastWithResilience (fun s -> { s with RetryCount = count })

    /// Sets backoff strategy for retries on the previous step
    [<CustomOperation("backoff")>]
    member _.Backoff(steps: WorkflowStep list, strategy: BackoffStrategy) : WorkflowStep list =
        steps |> WorkflowInternal.modifyLastWithResilience (fun s -> { s with Backoff = strategy })

    /// Sets timeout for the previous step
    [<CustomOperation("timeout")>]
    member _.Timeout(steps: WorkflowStep list, duration: TimeSpan) : WorkflowStep list =
        steps |> WorkflowInternal.modifyLastWithResilience (fun s -> { s with Timeout = Some duration })

    /// Sets fallback executor for the previous step (used if all retries fail)
    [<CustomOperation("fallback")>]
    member _.Fallback<'i, 'o>(steps: WorkflowStep list, executor: Executor<'i, 'o>) : WorkflowStep list =
        let fallbackFn (input: obj) (ctx: WorkflowContext) : Async<obj> = async {
            let typedInput = unbox<'i> input
            let! result = executor.Execute typedInput ctx
            return box result
        }
        steps |> WorkflowInternal.modifyLastWithResilience (fun s -> { s with Fallback = Some fallbackFn })

    /// Builds the final workflow definition
    member _.Run(steps: WorkflowStep list) : WorkflowDef<'input, 'output> =
        { Steps = steps }


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

    /// Executes an async operation with timeout
    let private withTimeout (timeout: TimeSpan) (operation: Async<'a>) : Async<'a> =
        async {
            let! child = Async.StartChild(operation, int timeout.TotalMilliseconds)
            return! child
        }

    /// Executes an inner step (non-resilient)
    let rec private executeInnerStep (step: WorkflowStep) (input: obj) (ctx: WorkflowContext) : Async<obj> =
        async {
            match step with
            | Step (_, execute) ->
                return! execute input ctx
            | Route router ->
                return! router input ctx
            | Parallel executors ->
                let! results =
                    executors
                    |> List.map (fun exec -> exec input ctx)
                    |> Async.Parallel
                return (results |> Array.toList) :> obj
            | Resilient (settings, inner) ->
                return! executeWithResilience settings inner input ctx
        }

    /// Executes a step with resilience (retry, timeout, fallback)
    and private executeWithResilience (settings: ResilienceSettings) (inner: WorkflowStep) (input: obj) (ctx: WorkflowContext) : Async<obj> =
        let rec attemptWithRetry (remainingRetries: int) (attempt: int) : Async<obj> =
            async {
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
                            do! Async.Sleep (int delay.TotalMilliseconds)

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

    /// Runs a workflow with the given input
    let run<'input, 'output> (input: 'input) (workflow: WorkflowDef<'input, 'output>) : Async<'output> =
        async {
            let ctx = WorkflowContext.create ()
            let mutable current: obj = input :> obj

            for step in workflow.Steps do
                let! result = executeInnerStep step current ctx
                current <- result

            return current :?> 'output
        }

    /// Runs a workflow synchronously
    let runSync<'input, 'output> (input: 'input) (workflow: WorkflowDef<'input, 'output>) : 'output =
        workflow |> run input |> Async.RunSynchronously
