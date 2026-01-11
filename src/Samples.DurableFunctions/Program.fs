module Samples.DurableFunctions.Program

open System
open Microsoft.Extensions.Hosting
open Microsoft.Azure.Functions.Worker

[<EntryPoint>]
let main args =
    HostBuilder()
        .ConfigureFunctionsWebApplication(Action<IFunctionsWorkerApplicationBuilder>(fun builder ->
            // Explicitly configure the Durable Task extension
            builder.ConfigureDurableExtension() |> ignore
        ))
        .Build()
        .Run()
    0
