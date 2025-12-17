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


/// A step in a workflow pipeline
type WorkflowStep =
    | Step of name: string * execute: (obj -> WorkflowContext -> Async<obj>)


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

    /// Runs a workflow with the given input
    let run<'input, 'output> (input: 'input) (workflow: WorkflowDef<'input, 'output>) : Async<'output> =
        async {
            let ctx = WorkflowContext.create ()
            let mutable current: obj = input :> obj

            for step in workflow.Steps do
                match step with
                | Step (name, execute) ->
                    let! result = execute current ctx
                    current <- result

            return current :?> 'output
        }

    /// Runs a workflow synchronously
    let runSync<'input, 'output> (input: 'input) (workflow: WorkflowDef<'input, 'output>) : 'output =
        workflow |> run input |> Async.RunSynchronously
