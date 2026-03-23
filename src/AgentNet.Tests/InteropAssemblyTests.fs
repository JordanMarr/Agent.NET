/// Tests that bundled interop DLLs can fully load with all their dependencies.
/// This catches missing transitive dependencies in published NuGet packages.
module AgentNet.Tests.InteropAssemblyTests

open System
open System.IO
open System.Reflection
open NUnit.Framework
open Swensen.Unquote

let private loadAndVerifyAssembly (assemblyName: string) =
    let dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    let path = Path.Combine(dir, $"{assemblyName}.dll")

    if not (File.Exists(path)) then
        Assert.Fail($"Assembly {assemblyName} not found at {path}")

    let assembly = Assembly.LoadFrom(path)

    // Force the runtime to fully resolve all types and their members.
    // This triggers assembly resolution for all dependencies.
    let mutable errors = ResizeArray<string>()
    for t in assembly.GetTypes() do
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
        Assert.Fail($"Failed to resolve types/members in {assemblyName}:{Environment.NewLine}{msg}")

[<Test>]
let ``AgentNet.InProcess.Interop assembly loads and all types resolve`` () =
    loadAndVerifyAssembly "AgentNet.InProcess.Interop"
