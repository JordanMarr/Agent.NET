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

/// A step in a workflow pipeline (untyped, internal)
type WorkflowStep =
    | Step of durableId: string * name: string * execute: (obj -> WorkflowContext -> Task<obj>)
    | Route of durableId: string * router: (obj -> WorkflowContext -> Task<obj>)
    | Parallel of branches: (string * (obj -> WorkflowContext -> Task<obj>)) list  // (durableId, executor) pairs
    // Durable-only operations (require DurableTask runtime)
    // Stores metadata only - durable primitives are invoked in the execution layer
    | AwaitEvent of durableId: string * eventName: string
    | Delay of durableId: string * duration: TimeSpan
    // Resilience wrappers (wrap the preceding step)
    | WithRetry of inner: WorkflowStep * maxRetries: int
    | WithTimeout of inner: WorkflowStep * timeout: TimeSpan
    | WithFallback of inner: WorkflowStep * fallbackId: string * fallback: (obj -> WorkflowContext -> Task<obj>)


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
    Name: string option
    Steps: WorkflowStep list
}


/// Module for workflow building internals (public for inline SRTP support)
[<AutoOpen>]
module WorkflowInternal =

    /// Executes a single workflow step (used by nested workflows and in-process execution)
    let rec executeStep (step: WorkflowStep) (input: obj) (ctx: WorkflowContext) : Task<obj> =
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
            // Durable-only operations - fail in in-process execution
            | AwaitEvent (durableId, _) ->
                return failwith $"AwaitEvent '{durableId}' requires durable runtime. Use Workflow.Durable.run instead of Workflow.InProcess.run."
            | Delay (_, duration) ->
                return failwith $"Delay ({duration}) requires durable runtime. Use Workflow.Durable.run instead of Workflow.InProcess.run."
            // Resilience wrappers - work in in-process execution
            | WithRetry (inner, maxRetries) ->
                let rec retry attempt =
                    task {
                        try
                            return! executeStep inner input ctx
                        with ex when attempt < maxRetries ->
                            return! retry (attempt + 1)
                    }
                return! retry 0
            | WithTimeout (inner, timeout) ->
                let execution = executeStep inner input ctx
                let timeoutTask = Task.Delay(timeout)
                let! winner = Task.WhenAny(execution, timeoutTask)
                if obj.ReferenceEquals(winner, timeoutTask) then
                    return raise (TimeoutException($"Step timed out after {timeout}"))
                else
                    return! execution
            | WithFallback (inner, _, fallback) ->
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
                let mutable current = input
                for nestedStep in wf.Steps do
                    let! result = executeStep nestedStep current ctx
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
    /// Handles both F# list and C# List<object> (from MAF ParallelExecutor)
    let wrapStepFanIn<'elem, 'o> (durableId: string) (name: string) (step: Step<'elem list, 'o>) : WorkflowStep =
        let exec = stepToExecutor name step
        Step (durableId, exec.Name, fun input ctx -> task {
            // Input from MAF Parallel is List<object>, convert to F# list
            let objList =
                match input with
                | :? System.Collections.IList as csList ->
                    csList |> Seq.cast<obj> |> Seq.toList
                | _ ->
                    input :?> obj list  // Fallback for direct F# list
            let typedList = objList |> List.map (fun o -> o :?> 'elem)
            let! result = exec.Execute typedList ctx
            return result :> obj
        })

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
    /// Handles both F# list and C# List<object> (from MAF ParallelExecutor)
    let wrapFanIn<'elem, 'o> (exec: Executor<'elem list, 'o>) : WorkflowStep =
        let durableId = DurableId.forExecutor exec
        Step (durableId, exec.Name, fun input ctx -> task {
            // Input from MAF Parallel is List<object>, convert to F# list
            let objList =
                match input with
                | :? System.Collections.IList as csList ->
                    csList |> Seq.cast<obj> |> Seq.toList
                | _ ->
                    input :?> obj list  // Fallback for direct F# list
            let typedList = objList |> List.map (fun o -> o :?> 'elem)
            let! result = exec.Execute typedList ctx
            return result :> obj
        })


/// Builder for the workflow computation expression
type WorkflowBuilder() =

    member _.Yield(_) : WorkflowState<'a, 'a> = { Name = None; Steps = [] }

    /// Sets the name of the workflow (used for MAF compilation and durable function registration)
    [<CustomOperation("name")>]
    member _.Name(state: WorkflowState<'input, 'output>, name: string) : WorkflowState<'input, 'output> =
        { state with Name = Some name }

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
        { Name = state.Name; Steps = state.Steps @ [WorkflowInternal.wrapStep durableId displayName step] }

    /// Adds subsequent step - threads input type through (uses SRTP for type resolution)
    [<CustomOperation("step")>]
    member inline _.Step(state: WorkflowState<'input, 'middle>, x: ^T) : WorkflowState<'input, 'output> =
        let step : Step<'middle, 'output> = ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'middle, 'output>) (StepConv, x))
        let durableId = WorkflowInternal.getDurableId step
        let displayName = WorkflowInternal.getDisplayName step
        WorkflowInternal.warnIfLambda step durableId
        { Name = state.Name; Steps = state.Steps @ [WorkflowInternal.wrapStep durableId displayName step] }

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
        { Name = state.Name; Steps = state.Steps @ [WorkflowInternal.wrapRouter routeDurableId wrappedRouter] }

    // ============ FANOUT OPERATIONS ============
    // SRTP overloads for 2-5 arguments - no wrapper needed!
    // For 6+ branches, use: fanOut [+fn1; +fn2; ...]
    // Durable IDs are auto-generated for each parallel branch

    /// Runs 2 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, x1: ^A, x2: ^B) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        { Name = state.Name; Steps = state.Steps @ [WorkflowInternal.wrapStepParallel [s1; s2]] }

    /// Runs 3 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, x1: ^A, x2: ^B, x3: ^C) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let s3 : Step<'middle, 'o> = ((^C or StepConv) : (static member ToStep: StepConv * ^C -> Step<'middle, 'o>) (StepConv, x3))
        { Name = state.Name; Steps = state.Steps @ [WorkflowInternal.wrapStepParallel [s1; s2; s3]] }

    /// Runs 4 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, x1: ^A, x2: ^B, x3: ^C, x4: ^D) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let s3 : Step<'middle, 'o> = ((^C or StepConv) : (static member ToStep: StepConv * ^C -> Step<'middle, 'o>) (StepConv, x3))
        let s4 : Step<'middle, 'o> = ((^D or StepConv) : (static member ToStep: StepConv * ^D -> Step<'middle, 'o>) (StepConv, x4))
        { Name = state.Name; Steps = state.Steps @ [WorkflowInternal.wrapStepParallel [s1; s2; s3; s4]] }

    /// Runs 5 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle>, x1: ^A, x2: ^B, x3: ^C, x4: ^D, x5: ^E) : WorkflowState<'input, 'o list> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let s3 : Step<'middle, 'o> = ((^C or StepConv) : (static member ToStep: StepConv * ^C -> Step<'middle, 'o>) (StepConv, x3))
        let s4 : Step<'middle, 'o> = ((^D or StepConv) : (static member ToStep: StepConv * ^D -> Step<'middle, 'o>) (StepConv, x4))
        let s5 : Step<'middle, 'o> = ((^E or StepConv) : (static member ToStep: StepConv * ^E -> Step<'middle, 'o>) (StepConv, x5))
        { Name = state.Name; Steps = state.Steps @ [WorkflowInternal.wrapStepParallel [s1; s2; s3; s4; s5]] }

    /// Runs multiple steps in parallel (fan-out) - for 6+ branches, use '+' operator
    [<CustomOperation("fanOut")>]
    member _.FanOut(state: WorkflowState<'input, 'middle>, steps: Step<'middle, 'o> list) : WorkflowState<'input, 'o list> =
        { Name = state.Name; Steps = state.Steps @ [WorkflowInternal.wrapStepParallel steps] }

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
        { Name = state.Name; Steps = state.Steps @ [WorkflowInternal.wrapStepFanIn durableId displayName step] }

    // ============ RESILIENCE OPERATIONS ============
    // These wrap the preceding step with retry, timeout, or fallback behavior

    /// Wraps the previous step with retry logic. On failure, retries up to maxRetries times.
    [<CustomOperation("retry")>]
    member _.Retry(state: WorkflowState<'input, 'output>, maxRetries: int) : WorkflowState<'input, 'output> =
        match state.Steps with
        | [] -> failwith "retry requires a preceding step"
        | steps ->
            let allButLast = steps |> List.take (steps.Length - 1)
            let last = steps |> List.last
            { Name = state.Name; Steps = allButLast @ [WithRetry(last, maxRetries)] }

    /// Wraps the previous step with a timeout. Fails with TimeoutException if duration exceeded.
    [<CustomOperation("timeout")>]
    member _.Timeout(state: WorkflowState<'input, 'output>, duration: TimeSpan) : WorkflowState<'input, 'output> =
        match state.Steps with
        | [] -> failwith "timeout requires a preceding step"
        | steps ->
            let allButLast = steps |> List.take (steps.Length - 1)
            let last = steps |> List.last
            { Name = state.Name; Steps = allButLast @ [WithTimeout(last, duration)] }

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
        match state.Steps with
        | [] -> failwith "fallback requires a preceding step"
        | steps ->
            let allButLast = steps |> List.take (steps.Length - 1)
            let last = steps |> List.last
            { Name = state.Name; Steps = allButLast @ [WithFallback(last, durableId, fallbackExec)] }

    /// Builds the final workflow definition
    member _.Run(state: WorkflowState<'input, 'output>) : WorkflowDef<'input, 'output> =
        { Name = state.Name; Steps = state.Steps }


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
            | Step (durableId, _, execute) ->
                // Wrap the execute function, creating an AgentNet context for execution
                // Combine durableId with stepIndex to ensure uniqueness within workflow
                let executorId = $"{durableId}_{stepIndex}"
                let fn = Func<obj, Task<obj>>(fun input ->
                    let ctx = WorkflowContext.create()
                    execute input ctx)
                ExecutorFactory.CreateStep(executorId, fn)

            | Route (durableId, router) ->
                // Router selects and executes the appropriate branch
                // Combine durableId with stepIndex to ensure uniqueness
                let executorId = $"{durableId}_{stepIndex}"
                let fn = Func<obj, Task<obj>>(fun input ->
                    let ctx = WorkflowContext.create()
                    router input ctx)
                ExecutorFactory.CreateStep(executorId, fn)

            | Parallel branches ->
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
            | AwaitEvent (durableId, _) ->
                failwith $"AwaitEvent '{durableId}' cannot be compiled for in-process execution. Use Workflow.Durable.run instead."
            | Delay (_, duration) ->
                failwith $"Delay ({duration}) cannot be compiled for in-process execution. Use Workflow.Durable.run instead."

            // Resilience wrappers - compile by wrapping the inner step execution
            | WithRetry (inner, maxRetries) ->
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

            | WithTimeout (inner, timeout) ->
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

            | WithFallback (inner, fallbackId, fallback) ->
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
            match workflow.Steps with
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
