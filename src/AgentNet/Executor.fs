namespace AgentNet

open System
open System.Threading.Tasks

/// Context passed to executors during workflow execution
type WorkflowContext = {
    /// Unique identifier for this workflow run
    RunId: Guid
    /// Shared state dictionary for passing data between executors
    State: Map<string, obj>
    /// Cancellation token for cooperative cancellation (e.g., from Polly timeout/hedging)
    CancellationToken: System.Threading.CancellationToken
    /// Service provider for resolving step dependencies (DI). Supplied by the run/resume host;
    /// defaults to an empty provider. Steps resolve deps at execution time rather than capturing
    /// them in closures, so the workflow definition stays free of captured deps (required for
    /// cross-process durable resume). See ARCHITECTURAL_INVARIANTS.md §5.
    Services: IServiceProvider
}

module WorkflowContext =
    /// An empty service provider used when the host supplies no DI container.
    let private emptyServices =
        { new IServiceProvider with
            member _.GetService(_serviceType: Type) = null }

    /// Creates a new empty workflow context
    let create () = {
        RunId = Guid.NewGuid()
        State = Map.empty
        CancellationToken = System.Threading.CancellationToken.None
        Services = emptyServices
    }

    /// Creates a workflow context with a specific cancellation token
    let withCancellation (ct: System.Threading.CancellationToken) (ctx: WorkflowContext) =
        { ctx with CancellationToken = ct }

    /// Sets the service provider used to resolve step dependencies.
    let withServices (services: IServiceProvider) (ctx: WorkflowContext) =
        { ctx with Services = services }

    /// Resolves a service of type 'T from the context, or None if not registered.
    let tryGetService<'T> (ctx: WorkflowContext) : 'T option =
        match ctx.Services.GetService(typeof<'T>) with
        | null -> None
        | svc -> Some (svc :?> 'T)

    /// Resolves a required service of type 'T from the context, throwing if not registered.
    let getRequiredService<'T> (ctx: WorkflowContext) : 'T =
        match ctx.Services.GetService(typeof<'T>) with
        | null -> failwithf "No service of type '%s' is registered in WorkflowContext.Services." typeof<'T>.FullName
        | svc -> svc :?> 'T

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
            Execute = fun input ctx -> TypedAgent.invokeWithCancellation ctx.CancellationToken input agent
        }
