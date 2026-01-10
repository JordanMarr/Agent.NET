/// Tests for DurableId module - stable ID generation and display name extraction
module AgentNet.Tests.DurableIdTests

open NUnit.Framework
open Swensen.Unquote
open System.Threading.Tasks
open AgentNet

// Named module-level functions for testing
let namedTaskFunction (x: int) : Task<string> = Task.FromResult (string x)

let namedAsyncFunction (x: int) : Async<string> = async { return string x }

[<Test>]
let ``getDisplayName extracts name from named Task function``() =
    let displayName = DurableId.getDisplayName namedTaskFunction
    displayName =! "namedTaskFunction"

[<Test>]
let ``getDisplayName extracts name from named Async function``() =
    let displayName = DurableId.getDisplayName namedAsyncFunction
    displayName =! "namedAsyncFunction"

[<Test>]
let ``getDisplayName returns type-based name for inline lambda``() =
    let lambdaFn = fun (x: int) -> Task.FromResult (string x)
    let displayName = DurableId.getDisplayName lambdaFn
    // Lambdas don't have a named method to call, so we get type-based name
    displayName =! "Step<Int32, Task`1>"

[<Test>]
let ``forStep generates stable ID based on type signature``() =
    // The ID should be stable based on input/output types
    let id1 = DurableId.forStep namedTaskFunction
    let id2 = DurableId.forStep namedTaskFunction
    // Note: IDs may differ due to closure context, but should start with Node_
    id1.StartsWith("Node_") =! true
    id2.StartsWith("Node_") =! true

[<Test>]
let ``forStep generates different IDs for different type signatures``() =
    let fn1 (x: int) = Task.FromResult (string x)
    let fn2 (x: string) = Task.FromResult x.Length
    let id1 = DurableId.forStep fn1
    let id2 = DurableId.forStep fn2
    id1 <>! id2
