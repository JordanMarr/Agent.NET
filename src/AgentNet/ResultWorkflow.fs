namespace AgentNet

open System
open System.Threading.Tasks

// =============================================================================
// Internal Result Helpers (no external dependencies)
// =============================================================================

module internal ResultHelpers =
    let map f = function Ok x -> Ok (f x) | Error e -> Error e
    let bind f = function Ok x -> f x | Error e -> Error e
    let mapError f = function Ok x -> Ok x | Error e -> Error (f e)

module internal TaskResultHelpers =
    let map f tr = task {
        let! r = tr
        return ResultHelpers.map f r
    }

    let bind f tr = task {
        let! r = tr
        match r with
        | Ok x -> return! f x
        | Error e -> return Error e
    }


// =============================================================================
// ResultExecutor Type
// =============================================================================

/// An executor that transforms input to Result<output, error> within a workflow
type ResultExecutor<'input, 'output, 'error> = {
    Name: string
    Execute: 'input -> WorkflowContext -> Task<Result<'output, 'error>>
}


// =============================================================================
// ResultExecutor Module
// =============================================================================

/// Module for creating result-aware executors
[<RequireQualifiedAccess>]
module ResultExecutor =

    /// Creates a result executor from a simple function (map semantics - wrapped in Ok)
    let map (name: string) (fn: 'input -> 'output) : ResultExecutor<'input, 'output, 'error> =
        {
            Name = name
            Execute = fun input _ -> task { return Ok (fn input) }
        }

    /// Creates a result executor from a Result-returning function (bind semantics)
    let bind (name: string) (fn: 'input -> Result<'output, 'error>) : ResultExecutor<'input, 'output, 'error> =
        {
            Name = name
            Execute = fun input _ -> task { return fn input }
        }

    /// Creates a result executor from a Task function (map semantics - wrapped in Ok)
    let mapTask (name: string) (fn: 'input -> Task<'output>) : ResultExecutor<'input, 'output, 'error> =
        {
            Name = name
            Execute = fun input _ -> task {
                let! result = fn input
                return Ok result
            }
        }

    /// Creates a result executor from a Task Result-returning function (bind semantics)
    let bindTask (name: string) (fn: 'input -> Task<Result<'output, 'error>>) : ResultExecutor<'input, 'output, 'error> =
        {
            Name = name
            Execute = fun input _ -> fn input
        }

    /// Creates a result executor from an F# Async function (map semantics - wrapped in Ok)
    let mapAsync (name: string) (fn: 'input -> Async<'output>) : ResultExecutor<'input, 'output, 'error> =
        {
            Name = name
            Execute = fun input _ -> task {
                let! result = fn input |> Async.StartAsTask
                return Ok result
            }
        }

    /// Creates a result executor from an F# Async Result-returning function (bind semantics)
    let bindAsync (name: string) (fn: 'input -> Async<Result<'output, 'error>>) : ResultExecutor<'input, 'output, 'error> =
        {
            Name = name
            Execute = fun input _ -> fn input |> Async.StartAsTask
        }

    /// Creates a result executor with full context access
    let create (name: string) (fn: 'input -> WorkflowContext -> Task<Result<'output, 'error>>) : ResultExecutor<'input, 'output, 'error> =
        {
            Name = name
            Execute = fn
        }

    /// Creates a result executor from an AgentNet ChatAgent (map semantics - agents don't return Result)
    let fromChatAgent (name: string) (agent: ChatAgent) : ResultExecutor<string, string, 'error> =
        {
            Name = name
            Execute = fun input _ -> task {
                let! result = agent.Chat input
                return Ok result
            }
        }

    /// Creates a result executor from a regular workflow (wraps output in Ok)
    let fromWorkflow (name: string) (workflow: WorkflowDef<'input, 'output>) : ResultExecutor<'input, 'output, 'error> =
        {
            Name = name
            Execute = fun input _ -> task {
                let! result = Workflow.InProcess.run input workflow
                return Ok result
            }
        }


// =============================================================================
// ResultWorkflow Step Types
// =============================================================================

/// A step in a result workflow pipeline
type ResultWorkflowStep<'error> =
    | Step of name: string * execute: (obj -> WorkflowContext -> Task<Result<obj, 'error>>)
    | Route of router: (obj -> WorkflowContext -> Task<Result<obj, 'error>>)
    | Parallel of executors: (obj -> WorkflowContext -> Task<Result<obj, 'error>>) list


/// A result workflow definition that can be executed
type ResultWorkflowDef<'input, 'output, 'error> = {
    /// The steps in the workflow, in order
    Steps: ResultWorkflowStep<'error> list
}

/// Internal state carrier that threads type information through the builder
type ResultWorkflowState<'input, 'output, 'error> = {
    Steps: ResultWorkflowStep<'error> list
    StepCount: int
}


// =============================================================================
// ResultStep Type (unifies supported step input types for SRTP)
// =============================================================================

/// A step type for result workflows that unifies Task<Result>/Async<Result> functions,
/// TypedAgents, and ResultExecutors.
/// This enables clean result workflow syntax and mixed-type fanOut operations.
/// Note: For nested workflows, use ResultWorkflow.InProcess.toExecutor to convert to ResultExecutor.
type ResultStep<'i, 'o, 'e> =
    | TaskResultStep of ('i -> Task<Result<'o, 'e>>)
    | AsyncResultStep of ('i -> Async<Result<'o, 'e>>)
    | AgentResultStep of TypedAgent<'i, 'o>
    | ExecutorResultStep of ResultExecutor<'i, 'o, 'e>


/// SRTP witness type for converting various types to ResultStep.
/// Uses the type class pattern to enable inline resolution at call sites.
type ResultStepConv = ResultStepConv with
    // Bind semantics (Result-returning functions)
    static member inline ToStep(_: ResultStepConv, fn: 'i -> Task<Result<'o, 'e>>) : ResultStep<'i, 'o, 'e> =
        TaskResultStep fn
    static member inline ToStep(_: ResultStepConv, fn: 'i -> Async<Result<'o, 'e>>) : ResultStep<'i, 'o, 'e> =
        AsyncResultStep fn

    // Map semantics (TypedAgent - wrapped in Ok automatically)
    static member inline ToStep(_: ResultStepConv, agent: TypedAgent<'i, 'o>) : ResultStep<'i, 'o, 'e> =
        AgentResultStep agent

    // Passthrough for ResultExecutor and ResultStep
    static member inline ToStep(_: ResultStepConv, exec: ResultExecutor<'i, 'o, 'e>) : ResultStep<'i, 'o, 'e> =
        ExecutorResultStep exec
    static member inline ToStep(_: ResultStepConv, step: ResultStep<'i, 'o, 'e>) : ResultStep<'i, 'o, 'e> =
        step


// =============================================================================
// ResultWorkflowInternal Module
// =============================================================================

/// Module for workflow building internals (public for inline SRTP support)
[<AutoOpen>]
module ResultWorkflowInternal =

    /// Wraps a typed result executor as an untyped step
    let wrapExecutor<'i, 'o, 'e> (exec: ResultExecutor<'i, 'o, 'e>) : ResultWorkflowStep<'e> =
        Step (exec.Name, fun input ctx -> task {
            let typedInput = input :?> 'i
            let! result = exec.Execute typedInput ctx
            return ResultHelpers.map box result
        })

    /// Wraps a typed router function as an untyped route step
    let wrapRouter<'a, 'b, 'e> (router: 'a -> ResultExecutor<'a, 'b, 'e>) : ResultWorkflowStep<'e> =
        Route (fun input ctx -> task {
            let typedInput = input :?> 'a
            let selectedExecutor = router typedInput
            let! result = selectedExecutor.Execute typedInput ctx
            return ResultHelpers.map box result
        })

    /// Wraps a list of typed executors as untyped parallel functions
    /// All executors run in parallel; if any returns Error, the first Error is returned
    let wrapParallel<'i, 'o, 'e> (executors: ResultExecutor<'i, 'o, 'e> list) : ResultWorkflowStep<'e> =
        let wrappedFns =
            executors
            |> List.map (fun exec ->
                fun (input: obj) (ctx: WorkflowContext) -> task {
                    let typedInput = input :?> 'i
                    let! result = exec.Execute typedInput ctx
                    return ResultHelpers.map box result
                })
        Parallel wrappedFns

    /// Wraps an aggregator executor, handling the obj list -> typed list conversion
    let wrapFanIn<'elem, 'o, 'e> (exec: ResultExecutor<'elem list, 'o, 'e>) : ResultWorkflowStep<'e> =
        Step (exec.Name, fun input ctx -> task {
            // Input is obj list from parallel, convert each element to the expected type
            let objList = input :?> obj list
            let typedList = objList |> List.map (fun o -> o :?> 'elem)
            let! result = exec.Execute typedList ctx
            return ResultHelpers.map box result
        })

    // ========== SRTP-based helper functions for ResultStep ==========

    /// Converts a ResultStep<'i, 'o, 'e> to a ResultExecutor<'i, 'o, 'e>
    let resultStepToExecutor<'i, 'o, 'e> (name: string) (step: ResultStep<'i, 'o, 'e>) : ResultExecutor<'i, 'o, 'e> =
        match step with
        | TaskResultStep fn ->
            { Name = name; Execute = fun input _ -> fn input }
        | AsyncResultStep fn ->
            { Name = name; Execute = fun input _ -> fn input |> Async.StartAsTask }
        | AgentResultStep agent ->
            { Name = name; Execute = fun input _ -> task {
                let! output = TypedAgent.invoke input agent
                return Ok output
            }}
        | ExecutorResultStep exec ->
            { exec with Name = name }

    /// Wraps a ResultStep as an untyped ResultWorkflowStep
    let wrapResultStep<'i, 'o, 'e> (name: string) (step: ResultStep<'i, 'o, 'e>) : ResultWorkflowStep<'e> =
        let exec = resultStepToExecutor name step
        Step (exec.Name, fun input ctx -> task {
            let typedInput = input :?> 'i
            let! result = exec.Execute typedInput ctx
            return ResultHelpers.map box result
        })

    /// Wraps a list of ResultSteps as parallel execution
    let wrapResultStepParallel<'i, 'o, 'e> (steps: ResultStep<'i, 'o, 'e> list) : ResultWorkflowStep<'e> =
        let wrappedFns =
            steps
            |> List.mapi (fun i step ->
                let exec = resultStepToExecutor $"Parallel {i+1}" step
                fun (input: obj) (ctx: WorkflowContext) -> task {
                    let typedInput = input :?> 'i
                    let! result = exec.Execute typedInput ctx
                    return ResultHelpers.map box result
                })
        Parallel wrappedFns

    /// Wraps a list of ResultSteps as parallel execution with a name prefix
    let wrapResultStepParallelNamed<'i, 'o, 'e> (namePrefix: string) (steps: ResultStep<'i, 'o, 'e> list) : ResultWorkflowStep<'e> =
        let wrappedFns =
            steps
            |> List.mapi (fun i step ->
                let exec = resultStepToExecutor $"{namePrefix} {i+1}" step
                fun (input: obj) (ctx: WorkflowContext) -> task {
                    let typedInput = input :?> 'i
                    let! result = exec.Execute typedInput ctx
                    return ResultHelpers.map box result
                })
        Parallel wrappedFns

    /// Wraps a ResultStep as a fan-in aggregator, handling obj list -> typed list conversion
    let wrapResultStepFanIn<'elem, 'o, 'e> (name: string) (step: ResultStep<'elem list, 'o, 'e>) : ResultWorkflowStep<'e> =
        let exec = resultStepToExecutor name step
        Step (exec.Name, fun input ctx -> task {
            let objList = input :?> obj list
            let typedList = objList |> List.map (fun o -> o :?> 'elem)
            let! result = exec.Execute typedList ctx
            return ResultHelpers.map box result
        })


// =============================================================================
// ResultWorkflowBuilder CE
// =============================================================================

/// Builder for the result workflow computation expression
type ResultWorkflowBuilder() =

    member _.Yield(_) : ResultWorkflowState<'a, 'a, 'e> = { Steps = []; StepCount = 0 }

    // ============ STEP OPERATIONS ============
    // Uses inline SRTP to accept Task<Result>, Async<Result>, TypedAgent, ResultExecutor,
    // ResultWorkflow, or ResultStep directly

    /// Adds first step - establishes workflow input/output/error types (uses SRTP)
    [<CustomOperation("step")>]
    member inline _.StepFirst(state: ResultWorkflowState<_, _, 'e>, x: ^T) : ResultWorkflowState<'i, 'o, 'e> =
        let step : ResultStep<'i, 'o, 'e> =
            ((^T or ResultStepConv) : (static member ToStep: ResultStepConv * ^T -> ResultStep<'i, 'o, 'e>) (ResultStepConv, x))
        let name = $"Step {state.StepCount + 1}"
        { Steps = state.Steps @ [ResultWorkflowInternal.wrapResultStep name step]; StepCount = state.StepCount + 1 }

    /// Adds first step with name - establishes workflow input/output/error types
    [<CustomOperation("step")>]
    member inline _.StepFirst(state: ResultWorkflowState<_, _, 'e>, name: string, x: ^T) : ResultWorkflowState<'i, 'o, 'e> =
        let step : ResultStep<'i, 'o, 'e> =
            ((^T or ResultStepConv) : (static member ToStep: ResultStepConv * ^T -> ResultStep<'i, 'o, 'e>) (ResultStepConv, x))
        { Steps = state.Steps @ [ResultWorkflowInternal.wrapResultStep name step]; StepCount = state.StepCount + 1 }

    /// Adds subsequent step - threads input type through (uses SRTP)
    [<CustomOperation("step")>]
    member inline _.Step(state: ResultWorkflowState<'input, 'middle, 'e>, x: ^T) : ResultWorkflowState<'input, 'output, 'e> =
        let step : ResultStep<'middle, 'output, 'e> =
            ((^T or ResultStepConv) : (static member ToStep: ResultStepConv * ^T -> ResultStep<'middle, 'output, 'e>) (ResultStepConv, x))
        let name = $"Step {state.StepCount + 1}"
        { Steps = state.Steps @ [ResultWorkflowInternal.wrapResultStep name step]; StepCount = state.StepCount + 1 }

    /// Adds subsequent step with name - threads input type through
    [<CustomOperation("step")>]
    member inline _.Step(state: ResultWorkflowState<'input, 'middle, 'e>, name: string, x: ^T) : ResultWorkflowState<'input, 'output, 'e> =
        let step : ResultStep<'middle, 'output, 'e> =
            ((^T or ResultStepConv) : (static member ToStep: ResultStepConv * ^T -> ResultStep<'middle, 'output, 'e>) (ResultStepConv, x))
        { Steps = state.Steps @ [ResultWorkflowInternal.wrapResultStep name step]; StepCount = state.StepCount + 1 }

    // ============ ROUTING ============

    /// Routes to different executors based on the previous step's output
    [<CustomOperation("route")>]
    member _.Route(state: ResultWorkflowState<'input, 'middle, 'e>, router: 'middle -> ResultExecutor<'middle, 'output, 'e>) : ResultWorkflowState<'input, 'output, 'e> =
        { Steps = state.Steps @ [ResultWorkflowInternal.wrapRouter router]; StepCount = state.StepCount + 1 }

    // ============ FANOUT OPERATIONS ============
    // SRTP overloads for 2-5 arguments - no wrapper needed!
    // For 6+ branches, use: fanOut [+fn1; +fn2; ...]

    /// Runs 2 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: ResultWorkflowState<'input, 'middle, 'e>, x1: ^A, x2: ^B) : ResultWorkflowState<'input, 'o list, 'e> =
        let s1 : ResultStep<'middle, 'o, 'e> = ((^A or ResultStepConv) : (static member ToStep: ResultStepConv * ^A -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x1))
        let s2 : ResultStep<'middle, 'o, 'e> = ((^B or ResultStepConv) : (static member ToStep: ResultStepConv * ^B -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x2))
        { Steps = state.Steps @ [ResultWorkflowInternal.wrapResultStepParallel [s1; s2]]; StepCount = state.StepCount + 1 }

    /// Runs 3 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: ResultWorkflowState<'input, 'middle, 'e>, x1: ^A, x2: ^B, x3: ^C) : ResultWorkflowState<'input, 'o list, 'e> =
        let s1 : ResultStep<'middle, 'o, 'e> = ((^A or ResultStepConv) : (static member ToStep: ResultStepConv * ^A -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x1))
        let s2 : ResultStep<'middle, 'o, 'e> = ((^B or ResultStepConv) : (static member ToStep: ResultStepConv * ^B -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x2))
        let s3 : ResultStep<'middle, 'o, 'e> = ((^C or ResultStepConv) : (static member ToStep: ResultStepConv * ^C -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x3))
        { Steps = state.Steps @ [ResultWorkflowInternal.wrapResultStepParallel [s1; s2; s3]]; StepCount = state.StepCount + 1 }

    /// Runs 4 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: ResultWorkflowState<'input, 'middle, 'e>, x1: ^A, x2: ^B, x3: ^C, x4: ^D) : ResultWorkflowState<'input, 'o list, 'e> =
        let s1 : ResultStep<'middle, 'o, 'e> = ((^A or ResultStepConv) : (static member ToStep: ResultStepConv * ^A -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x1))
        let s2 : ResultStep<'middle, 'o, 'e> = ((^B or ResultStepConv) : (static member ToStep: ResultStepConv * ^B -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x2))
        let s3 : ResultStep<'middle, 'o, 'e> = ((^C or ResultStepConv) : (static member ToStep: ResultStepConv * ^C -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x3))
        let s4 : ResultStep<'middle, 'o, 'e> = ((^D or ResultStepConv) : (static member ToStep: ResultStepConv * ^D -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x4))
        { Steps = state.Steps @ [ResultWorkflowInternal.wrapResultStepParallel [s1; s2; s3; s4]]; StepCount = state.StepCount + 1 }

    /// Runs 5 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: ResultWorkflowState<'input, 'middle, 'e>, x1: ^A, x2: ^B, x3: ^C, x4: ^D, x5: ^E) : ResultWorkflowState<'input, 'o list, 'e> =
        let s1 : ResultStep<'middle, 'o, 'e> = ((^A or ResultStepConv) : (static member ToStep: ResultStepConv * ^A -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x1))
        let s2 : ResultStep<'middle, 'o, 'e> = ((^B or ResultStepConv) : (static member ToStep: ResultStepConv * ^B -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x2))
        let s3 : ResultStep<'middle, 'o, 'e> = ((^C or ResultStepConv) : (static member ToStep: ResultStepConv * ^C -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x3))
        let s4 : ResultStep<'middle, 'o, 'e> = ((^D or ResultStepConv) : (static member ToStep: ResultStepConv * ^D -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x4))
        let s5 : ResultStep<'middle, 'o, 'e> = ((^E or ResultStepConv) : (static member ToStep: ResultStepConv * ^E -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x5))
        { Steps = state.Steps @ [ResultWorkflowInternal.wrapResultStepParallel [s1; s2; s3; s4; s5]]; StepCount = state.StepCount + 1 }

    /// Runs multiple steps in parallel (fan-out) - for 6+ branches, use '+' operator
    [<CustomOperation("fanOut")>]
    member _.FanOut(state: ResultWorkflowState<'input, 'middle, 'e>, steps: ResultStep<'middle, 'o, 'e> list) : ResultWorkflowState<'input, 'o list, 'e> =
        { Steps = state.Steps @ [ResultWorkflowInternal.wrapResultStepParallel steps]; StepCount = state.StepCount + 1 }

    // Named fanOut overloads - name is used as prefix for parallel step names

    /// Runs 2 named steps in parallel (fan-out)
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: ResultWorkflowState<'input, 'middle, 'e>, name: string, x1: ^A, x2: ^B) : ResultWorkflowState<'input, 'o list, 'e> =
        let s1 : ResultStep<'middle, 'o, 'e> = ((^A or ResultStepConv) : (static member ToStep: ResultStepConv * ^A -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x1))
        let s2 : ResultStep<'middle, 'o, 'e> = ((^B or ResultStepConv) : (static member ToStep: ResultStepConv * ^B -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x2))
        { Steps = state.Steps @ [ResultWorkflowInternal.wrapResultStepParallelNamed name [s1; s2]]; StepCount = state.StepCount + 1 }

    /// Runs 3 named steps in parallel (fan-out)
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: ResultWorkflowState<'input, 'middle, 'e>, name: string, x1: ^A, x2: ^B, x3: ^C) : ResultWorkflowState<'input, 'o list, 'e> =
        let s1 : ResultStep<'middle, 'o, 'e> = ((^A or ResultStepConv) : (static member ToStep: ResultStepConv * ^A -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x1))
        let s2 : ResultStep<'middle, 'o, 'e> = ((^B or ResultStepConv) : (static member ToStep: ResultStepConv * ^B -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x2))
        let s3 : ResultStep<'middle, 'o, 'e> = ((^C or ResultStepConv) : (static member ToStep: ResultStepConv * ^C -> ResultStep<'middle, 'o, 'e>) (ResultStepConv, x3))
        { Steps = state.Steps @ [ResultWorkflowInternal.wrapResultStepParallelNamed name [s1; s2; s3]]; StepCount = state.StepCount + 1 }

    // ============ FANIN OPERATIONS ============
    // Uses inline SRTP to accept Task<Result> fn, Async<Result> fn, TypedAgent, ResultExecutor, or ResultStep

    /// Aggregates parallel results with any supported step type (uses SRTP)
    [<CustomOperation("fanIn")>]
    member inline _.FanIn(state: ResultWorkflowState<'input, 'elem list, 'e>, x: ^T) : ResultWorkflowState<'input, 'output, 'e> =
        let step : ResultStep<'elem list, 'output, 'e> =
            ((^T or ResultStepConv) : (static member ToStep: ResultStepConv * ^T -> ResultStep<'elem list, 'output, 'e>) (ResultStepConv, x))
        let name = $"Step {state.StepCount + 1}"
        { Steps = state.Steps @ [ResultWorkflowInternal.wrapResultStepFanIn name step]; StepCount = state.StepCount + 1 }

    /// Aggregates parallel results with a named step
    [<CustomOperation("fanIn")>]
    member inline _.FanIn(state: ResultWorkflowState<'input, 'elem list, 'e>, name: string, x: ^T) : ResultWorkflowState<'input, 'output, 'e> =
        let step : ResultStep<'elem list, 'output, 'e> =
            ((^T or ResultStepConv) : (static member ToStep: ResultStepConv * ^T -> ResultStep<'elem list, 'output, 'e>) (ResultStepConv, x))
        { Steps = state.Steps @ [ResultWorkflowInternal.wrapResultStepFanIn name step]; StepCount = state.StepCount + 1 }

    /// Builds the final result workflow definition
    member _.Run(state: ResultWorkflowState<'input, 'output, 'e>) : ResultWorkflowDef<'input, 'output, 'e> =
        { Steps = state.Steps }


/// The result workflow computation expression builder instance
[<AutoOpen>]
module ResultWorkflowCE =
    let resultWorkflow = ResultWorkflowBuilder()

    /// Wraps a Task-returning function with map semantics (result wrapped in Ok).
    /// Use for functions that don't return Result but should be included in result workflows.
    let inline okTask (fn: 'i -> Task<'o>) : 'i -> Task<Result<'o, 'e>> =
        fun input -> task {
            let! result = fn input
            return Ok result
        }

    /// Wraps an Async-returning function with map semantics (result wrapped in Ok).
    let inline okAsync (fn: 'i -> Async<'o>) : 'i -> Async<Result<'o, 'e>> =
        fun input -> async {
            let! result = fn input
            return Ok result
        }

    /// SRTP witness type for the 'ok' wrapper function.
    type OkConv = OkConv with
        static member inline Wrap(_: OkConv, fn: 'i -> Task<'o>) : 'i -> Task<Result<'o, 'e>> = okTask fn
        static member inline Wrap(_: OkConv, fn: 'i -> Async<'o>) : 'i -> Async<Result<'o, 'e>> = okAsync fn

    /// Wraps a function with map semantics (result wrapped in Ok).
    /// Supports both Task<'o> and Async<'o> returning functions.
    /// Usage: step (ok myTaskFn) or step (ok myAsyncFn)
    let inline ok (fn: ^T) : ^R =
        ((^T or OkConv) : (static member Wrap: OkConv * ^T -> ^R) (OkConv, fn))

    /// Prefix operator to convert any supported type to ResultStep<'i, 'o, 'e>.
    /// Supports: Task<Result> fn, Async<Result> fn, TypedAgent, ResultExecutor, or ResultStep passthrough.
    /// Used for fanOut lists with 6+ branches: fanOut [+fn1; +fn2; +fn3; +fn4; +fn5; +fn6]
    let inline (~+) (x: ^T) : ResultStep<'i, 'o, 'e> =
        ((^T or ResultStepConv) : (static member ToStep: ResultStepConv * ^T -> ResultStep<'i, 'o, 'e>) (ResultStepConv, x))


// =============================================================================
// ResultWorkflow Execution Module
// =============================================================================

/// Functions for executing result workflows
[<RequireQualifiedAccess>]
module ResultWorkflow =

    /// In-process result workflow execution.
    /// Use this for testing, simple scenarios, or when you don't need durable suspension.
    module InProcess =

        /// Executes a single step
        let private executeStep<'e> (step: ResultWorkflowStep<'e>) (input: obj) (ctx: WorkflowContext) : Task<Result<obj, 'e>> =
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

                    // Check for any errors, return first error if found
                    let firstError =
                        results
                        |> Array.tryPick (function Error e -> Some e | Ok _ -> None)

                    match firstError with
                    | Some e -> return Error e
                    | None ->
                        let okValues =
                            results
                            |> Array.choose (function Ok v -> Some v | Error _ -> None)
                            |> Array.toList
                        return Ok (box okValues)
            }

        /// Runs a result workflow with the given input and context
        let runWithContext<'input, 'output, 'error> (input: 'input) (ctx: WorkflowContext) (workflow: ResultWorkflowDef<'input, 'output, 'error>) : Task<Result<'output, 'error>> =
            let rec executeSteps (steps: ResultWorkflowStep<'error> list) (current: obj) : Task<Result<obj, 'error>> =
                task {
                    match steps with
                    | [] ->
                        return Ok current
                    | step :: remaining ->
                        let! result = executeStep step current ctx
                        match result with
                        | Ok value ->
                            return! executeSteps remaining value
                        | Error e ->
                            return Error e  // Short-circuit on error!
                }

            task {
                let! result = executeSteps workflow.Steps (input :> obj)
                return ResultHelpers.map unbox<'output> result
            }

        /// Runs a result workflow with the given input (creates a new context)
        let run<'input, 'output, 'error> (input: 'input) (workflow: ResultWorkflowDef<'input, 'output, 'error>) : Task<Result<'output, 'error>> =
            let ctx = WorkflowContext.create ()
            runWithContext input ctx workflow

        /// Converts a result workflow to a result executor (enables workflow composition)
        let toExecutor<'input, 'output, 'error> (name: string) (workflow: ResultWorkflowDef<'input, 'output, 'error>) : ResultExecutor<'input, 'output, 'error> =
            {
                Name = name
                Execute = fun input ctx -> runWithContext input ctx workflow
            }
