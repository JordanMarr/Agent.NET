namespace AgentNet.Tests

open System.Threading.Tasks
open NUnit.Framework
open AgentNet
open AgentNet.InProcess
open Swensen.Unquote

module DecorateTests =

    let counter = ref 0

    let countDecorator : Decorator =
        fun exec ->
            fun input ctx ->
                incr counter
                exec input ctx

    let tagDecorator tag : Decorator =
        fun exec ->
            fun input ctx ->
                exec (sprintf "%s-%s" (input :?> string) tag :> obj) ctx

    [<Test>]
    let ``decorate wraps ExecuteInProcess`` () =
        task {
            counter.Value <- 0

            let wf =
                workflow {
                    step (fun (x: string) -> task { return x + "!" })
                    decorate countDecorator
                }

            let! result = wf |> Workflow.InProcess.run "hi"
            Assert.That(result, Is.EqualTo("hi!"))
            Assert.That(!counter, Is.EqualTo(1))
        }

    [<Test>]
    let ``decorate applies only to previous step`` () =
        task {
            counter.Value <- 0

            let wf =
                workflow {
                    step (fun (x: int) -> task { return x + 1 })
                    step (fun (x: int) -> task { return x * 2 })
                    decorate countDecorator
                }

            let! result = wf |> Workflow.InProcess.run 3
            Assert.That(result, Is.EqualTo(8))   // (3+1)*2
            Assert.That(!counter, Is.EqualTo(1)) // only second step wrapped
        }

    [<Test>]
    let ``decorator preserves output`` () =
        task {
            let wf =
                workflow {
                    step (fun (x: int) -> task { return x + 10 })
                    decorate countDecorator
                }

            let! result = wf |> Workflow.InProcess.run 5
            Assert.That(result, Is.EqualTo(15))
        }

    [<Test>]
    let ``decorate with composed decorators preserves intuitive order`` () =
        task {
            // Track call order
            let calls = ResizeArray<string>()

            let retryDecorator : Decorator =
                fun exec ->
                    fun input ctx ->
                        calls.Add("retry")
                        exec input ctx

            let logDecorator : Decorator =
                fun exec ->
                    fun input ctx ->
                        calls.Add("log")
                        exec input ctx

            let wf =
                workflow {
                    step (fun x -> task { return x + "!" })
                    decorate (retryDecorator << logDecorator)
                }

            let! result = wf |> Workflow.InProcess.run "hi"

            test <@ result = "hi!" @>
            test <@ calls |> Seq.toList = ["retry"; "log"] @>
        }

    [<Test>]
    let ``multiple decorate calls stack in order`` () =
        task {
            let calls = ResizeArray<string>()

            let firstDecorator : Decorator =
                fun exec ->
                    fun input ctx ->
                        calls.Add("first")
                        exec input ctx

            let secondDecorator : Decorator =
                fun exec ->
                    fun input ctx ->
                        calls.Add("second")
                        exec input ctx

            let wf =
                workflow {
                    step (fun x -> task { return x + "!" })
                    decorate firstDecorator
                    decorate secondDecorator
                }

            let! result = wf |> Workflow.InProcess.run "hi"

            test <@ result = "hi!" @>

            // secondDecorator wraps firstDecorator, so it runs first
            test <@ calls |> Seq.toList = ["second"; "first"] @>
        }

