#!/usr/bin/env dotnet fsi
#r "nuget: Fun.Build, 1.1.17"

open Fun.Build

let sln = "src/AgentNet.sln"
let outputDir = ".build"

pipeline "publish" {
    description "Clean, build, test, pack and push all NuGet packages"

    stage "clean" {
        run $"dotnet clean {sln} -c Release --verbosity quiet"
        run (fun _ ->
            if System.IO.Directory.Exists(outputDir) then
                System.IO.Directory.Delete(outputDir, true)
        )
    }

    stage "build" {
        run $"dotnet build {sln} -c Release"
    }

    // Tests run in Debug because DurableIdTests.getDisplayName relies on
    // reflection metadata that the Release compiler optimizes away.
    // TODO: fix the test to be Release-safe, then switch back to -c Release --no-build.
    stage "test" {
        run "dotnet test src/AgentNet.Tests/AgentNet.Tests.fsproj"
    }

    stage "pack" {
        run (fun _ ->
            if not (System.IO.Directory.Exists(outputDir)) then
                System.IO.Directory.CreateDirectory(outputDir) |> ignore
        )
        run $"dotnet pack src/AgentNet/AgentNet.fsproj -c Release --output {outputDir}"
        run $"dotnet pack src/AgentNet.Durable/AgentNet.Durable.fsproj -c Release --output {outputDir}"
        run $"dotnet pack src/AgentNet.InProcess/AgentNet.InProcess.fsproj -c Release --output {outputDir}"
        run $"dotnet pack src/AgentNet.InProcess.Polly/AgentNet.InProcess.Polly.fsproj -c Release --output {outputDir}"
    }

    stage "push" {
        whenEnvVar "AGENT_NET_NUGET_KEY"
        run (fun ctx ->
            let key = ctx.GetEnvVar "AGENT_NET_NUGET_KEY"
            for pkg in System.IO.Directory.GetFiles(outputDir, "*.nupkg") do
                ctx.RunSensitiveCommand $"""dotnet nuget push {pkg} -s https://api.nuget.org/v3/index.json -k {key}"""
                |> Async.RunSynchronously
                |> ignore
        )
    }

    runIfOnlySpecified
}

pipeline "test" {
    description "Build and run tests only"

    stage "build" {
        run $"dotnet build {sln} -c Release"
    }

    stage "test" {
        run "dotnet test src/AgentNet.Tests/AgentNet.Tests.fsproj"
    }

    runIfOnlySpecified
}

pipeline "test-live" {
    description "Test against published NuGet packages (all packages)"

    stage "build" {
        run "dotnet build src/AgentNet.Tests/AgentNet.Tests.fsproj -p:TestLivePackages=true"
    }

    stage "test" {
        run "dotnet test src/AgentNet.Tests/AgentNet.Tests.fsproj -p:TestLivePackages=true --no-build"
    }

    runIfOnlySpecified
}

pipeline "test-live-inprocess" {
    description "Test against published InProcess packages only (catches missing transitive deps)"

    stage "build" {
        run "dotnet build src/AgentNet.Tests/AgentNet.Tests.fsproj -p:TestLivePackages=InProcess"
    }

    stage "test" {
        run "dotnet test src/AgentNet.Tests/AgentNet.Tests.fsproj -p:TestLivePackages=InProcess --no-build --filter FullyQualifiedName!~DurableWorkflowTests"
    }

    runIfOnlySpecified
}
