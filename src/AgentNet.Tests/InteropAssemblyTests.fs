/// Tests that bundled AgentNet.Interop.dll can fully load with all its dependencies.
/// This catches missing transitive dependencies in published NuGet packages.
module AgentNet.Tests.InteropAssemblyTests

open System
open System.Reflection
open NUnit.Framework
open Swensen.Unquote

[<Test>]
let ``AgentNet.Interop assembly loads and all types resolve`` () =
    // Find the AgentNet.Interop assembly (bundled as a DLL in the package)
    let interopAssembly =
        AppDomain.CurrentDomain.GetAssemblies()
        |> Array.tryFind (fun a -> a.GetName().Name = "AgentNet.Interop")

    test <@ interopAssembly.IsSome @>

    // Force the runtime to fully resolve all types and their members.
    // GetTypes() alone won't trigger assembly resolution for types only used
    // as method parameters. We must enumerate methods/constructors to force
    // the JIT to resolve parameter types like TaskOrchestrationContext.
    let mutable errors = ResizeArray<string>()
    for t in interopAssembly.Value.GetTypes() do
        try
            t.GetMethods(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly)
            |> Array.iter (fun m ->
                try
                    m.GetParameters() |> ignore
                with ex ->
                    errors.Add($"{t.FullName}.{m.Name}: {ex.Message}")
            )
            t.GetConstructors(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance)
            |> Array.iter (fun c ->
                try
                    c.GetParameters() |> ignore
                with ex ->
                    errors.Add($"{t.FullName}..ctor: {ex.Message}")
            )
        with ex ->
            errors.Add($"{t.FullName}: {ex.Message}")

    if errors.Count > 0 then
        let msg = String.Join(Environment.NewLine, errors)
        Assert.Fail($"Failed to resolve types/members in AgentNet.Interop:{Environment.NewLine}{msg}")
