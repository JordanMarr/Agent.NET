module Samples.DurableFunctions.Program

open Microsoft.Extensions.Hosting
open Microsoft.Azure.Functions.Worker
open Microsoft.Azure.Functions.Worker.Extensions.DurableTask
open Microsoft.Extensions.Configuration

[<EntryPoint>]
let main args =
    HostBuilder()
        .ConfigureAppConfiguration(fun context config ->
            config
                .AddJsonFile("local.settings.json", optional = true, reloadOnChange = true)
                .AddEnvironmentVariables()
            |> ignore
        )
        .ConfigureFunctionsWebApplication(fun builder ->
            // Manually configure Durable extension since auto-discovery doesn't work with F#
            builder.ConfigureDurableExtension() |> ignore
        )
        .Build()
        .Run()
    0