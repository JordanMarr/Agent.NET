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
