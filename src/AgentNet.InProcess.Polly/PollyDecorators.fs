namespace AgentNet.InProcess.Polly

open System.Threading.Tasks
open Polly
open AgentNet
open AgentNet.InProcess

module PollyDecorators =

    /// Wraps a step's ExecuteInProcess function with a Polly ResiliencePipeline.
    let pipeline (pipeline: ResiliencePipeline) : Decorator =
        fun exec ->
            fun input ctx ->
                let callback = fun (ct: System.Threading.CancellationToken) ->
                    let ctxWithToken = { ctx with CancellationToken = ct }
                    exec input ctxWithToken |> ValueTask<obj>
                pipeline.ExecuteAsync<obj>(callback, ctx.CancellationToken).AsTask()
