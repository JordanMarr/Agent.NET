module Samples.DurableFunctions.Program

open System
open Microsoft.Extensions.Hosting
open Microsoft.Azure.Functions.Worker
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
        .ConfigureFunctionsWebApplication(Action<IFunctionsWorkerApplicationBuilder>(fun builder -> 
            builder.ConfigureDurableExtension() |> ignore 
        )) 
        .Build() 
        .Run() 
    0