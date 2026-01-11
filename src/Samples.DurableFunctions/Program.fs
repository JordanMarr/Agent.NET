module Samples.DurableFunctions.Program

open Microsoft.Extensions.Hosting
open Microsoft.Azure.Functions.Worker

[<EntryPoint>]
let main args =
    HostBuilder()
        .ConfigureFunctionsWorkerDefaults()
        .Build()
        .Run()
    0
