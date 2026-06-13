/// Tests for dependency injection into workflow steps via WorkflowContext.Services.
/// Steps resolve dependencies at execution time (ctx.Services) rather than capturing them,
/// which is what keeps a WorkflowDef free of captured deps for cross-process durable resume.
module AgentNet.Tests.WorkflowDiTests

open System
open System.Threading.Tasks
open NUnit.Framework
open Swensen.Unquote
open AgentNet
open AgentNet.InProcess

// A dependency to be injected into steps.
type IGreeter =
    abstract member Greet: string -> string

type Greeter() =
    interface IGreeter with
        member _.Greet name = $"Hello, {name}!"

/// A minimal IServiceProvider that resolves a single registered service (no M.E.DI dependency).
let private providerFor (svc: 'svc) =
    { new IServiceProvider with
        member _.GetService(serviceType: Type) =
            if serviceType = typeof<'svc> then box svc else null }

// A context-aware step that resolves its dependency from ctx.Services at execution time.
let private greetStep =
    Executor.create "greet" (fun (name: string) (ctx: WorkflowContext) ->
        let greeter = ctx |> WorkflowContext.getRequiredService<IGreeter>
        Task.fromResult (greeter.Greet name))

[<Test>]
let ``runWithServices injects the provider into step context``() =
    let wf = workflow { step greetStep }
    let services = providerFor (Greeter() :> IGreeter)

    let result = (wf |> Workflow.InProcess.runWithServices services "World").GetAwaiter().GetResult()

    result =! "Hello, World!"

[<Test>]
let ``getRequiredService throws when no provider supplied (plain run)``() =
    let wf = workflow { step greetStep }

    // Default run uses an empty provider, so the required service is missing.
    raises<exn> <@ (wf |> Workflow.InProcess.run "World").GetAwaiter().GetResult() @>

[<Test>]
let ``tryGetService returns None when service is not registered``() =
    let probe =
        Executor.create "probe" (fun (_: string) (ctx: WorkflowContext) ->
            let found = ctx |> WorkflowContext.tryGetService<IGreeter>
            Task.fromResult (Option.isSome found))

    let wf = workflow { step probe }

    let result = (wf |> Workflow.InProcess.run "x").GetAwaiter().GetResult()

    result =! false

[<Test>]
let ``toExecutor propagates services into a nested (composed) workflow``() =
    // Inner workflow needs the injected greeter; it is composed into the outer workflow as a step.
    let inner = workflow { step greetStep } |> Workflow.withName "inner"
    let innerExecutor = Workflow.InProcess.toExecutor "inner" inner

    let outer = workflow { step innerExecutor }
    let services = providerFor (Greeter() :> IGreeter)

    let result = (outer |> Workflow.InProcess.runWithServices services "Nested").GetAwaiter().GetResult()

    result =! "Hello, Nested!"
