module AgentNet.Tests.ToolTests

open NUnit.Framework
open Swensen.Unquote
open AgentNet

// Test functions without XML docs
let greet (name: string) : string =
    $"Hello, {name}!"

let add (x: int) (y: int) : int =
    x + y

let formatPrice (symbol: string) (price: decimal) (currency: string) : string =
    $"{symbol}: {price} {currency}"

/// Sends a friendly greeting to the specified person
let greetWithDocs (name: string) : string =
    $"Hello, {name}!"

/// Calculates the sum of two integers
let addWithDocs (x: int) (y: int) : int =
    x + y

[<Test>]
let ``Tool.create extracts function name from quotation`` () =
    let tool = Tool.create <@ greet @>
    tool.Name =! "greet"

[<Test>]
let ``Tool.create extracts MethodInfo from quotation`` () =
    let tool = Tool.create <@ greet @>
    tool.MethodInfo.Name =! "greet"

[<Test>]
let ``Tool.create works with curried functions`` () =
    let tool = Tool.create <@ add @>
    tool.Name =! "add"
    tool.MethodInfo.GetParameters().Length =! 2

[<Test>]
let ``Tool.create works with three parameter functions`` () =
    let tool = Tool.create <@ formatPrice @>
    tool.Name =! "formatPrice"
    tool.MethodInfo.GetParameters().Length =! 3

[<Test>]
let ``MethodInfo parameter names are preserved`` () =
    let tool = Tool.create <@ formatPrice @>
    let paramNames = tool.MethodInfo.GetParameters() |> Array.map (fun p -> p.Name)
    paramNames =! [| "symbol"; "price"; "currency" |]

[<Test>]
let ``Tool.describe sets description`` () =
    let tool =
        Tool.create <@ greet @>
        |> Tool.describe "Greets a person by name"
    tool.Description =! "Greets a person by name"

[<Test>]
let ``Tool.create sets empty description by default`` () =
    let tool = Tool.create <@ greet @>
    tool.Description =! ""

[<Test>]
let ``Tool.createWithDocs extracts description from XML docs`` () =
    let tool = Tool.createWithDocs <@ greetWithDocs @>
    tool.Description =! "Sends a friendly greeting to the specified person"

[<Test>]
let ``Tool.createWithDocs works with curried functions`` () =
    let tool = Tool.createWithDocs <@ addWithDocs @>
    tool.Description =! "Calculates the sum of two integers"

[<Test>]
let ``Tool.createWithDocs falls back to empty when no XML docs`` () =
    let tool = Tool.createWithDocs <@ greet @>
    tool.Description =! ""
