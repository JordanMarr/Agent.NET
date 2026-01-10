module AgentNet.Tests.ToolTests

open NUnit.Framework
open Swensen.Unquote
open AgentNet

// Test functions without XML docs
let greet (name: string) =
    $"Hello, {name}!" |> Task.fromResult

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

/// <summary>Formats a stock price for display</summary>
/// <param name="symbol">The stock ticker symbol</param>
/// <param name="price">The current price</param>
/// <param name="currency">The currency code (e.g., USD)</param>
let formatWithParamDocs (symbol: string) (price: decimal) (currency: string) : string =
    $"{symbol}: {price} {currency}"

[<Test>]
let ``Tool_create extracts function name from quotation`` () =
    let tool = Tool.create <@ greet @>
    tool.Name =! "greet"

[<Test>]
let ``Tool_create extracts MethodInfo from quotation`` () =
    let tool = Tool.create <@ greet @>
    tool.MethodInfo.Name =! "greet"

[<Test>]
let ``Tool_create works with curried functions`` () =
    let tool = Tool.create <@ add @>
    tool.Name =! "add"
    tool.MethodInfo.GetParameters().Length =! 2

[<Test>]
let ``Tool_create works with three parameter functions`` () =
    let tool = Tool.create <@ formatPrice @>
    tool.Name =! "formatPrice"
    tool.MethodInfo.GetParameters().Length =! 3

[<Test>]
let ``MethodInfo parameter names are preserved`` () =
    let tool = Tool.create <@ formatPrice @>
    let paramNames = tool.MethodInfo.GetParameters() |> Array.map (fun p -> p.Name)
    paramNames =! [| "symbol"; "price"; "currency" |]

[<Test>]
let ``Tool_describe sets description`` () =
    let tool =
        Tool.create <@ greet @>
        |> Tool.describe "Greets a person by name"
    tool.Description =! "Greets a person by name"

[<Test>]
let ``Tool_create sets empty description by default`` () =
    let tool = Tool.create <@ greet @>
    tool.Description =! ""

[<Test>]
let ``Tool_createWithDocs extracts description from XML docs`` () =
    let tool = Tool.createWithDocs <@ greetWithDocs @>
    tool.Description =! "Sends a friendly greeting to the specified person"

[<Test>]
let ``Tool_createWithDocs works with curried functions`` () =
    let tool = Tool.createWithDocs <@ addWithDocs @>
    tool.Description =! "Calculates the sum of two integers"

[<Test>]
let ``Tool_createWithDocs falls back to empty when no XML docs`` () =
    let tool = Tool.createWithDocs <@ greet @>
    tool.Description =! ""

[<Test>]
let ``Tool_create populates Parameters with names and types`` () =
    let tool = Tool.create <@ formatPrice @>
    tool.Parameters.Length =! 3
    tool.Parameters[0].Name =! "symbol"
    tool.Parameters[0].Type =! typeof<string>
    tool.Parameters[1].Name =! "price"
    tool.Parameters[1].Type =! typeof<decimal>
    tool.Parameters[2].Name =! "currency"
    tool.Parameters[2].Type =! typeof<string>

[<Test>]
let ``Tool_create sets empty param descriptions by default`` () =
    let tool = Tool.create <@ formatPrice @>
    tool.Parameters |> List.iter (fun p -> p.Description =! "")

[<Test>]
let ``Tool_createWithDocs extracts param descriptions from XML docs`` () =
    let tool = Tool.createWithDocs <@ formatWithParamDocs @>
    tool.Parameters[0].Description =! "The stock ticker symbol"
    tool.Parameters[1].Description =! "The current price"
    tool.Parameters[2].Description =! "The currency code (e.g., USD)"

[<Test>]
let ``Tool_createWithDocs falls back to empty param descriptions when not documented`` () =
    let tool = Tool.createWithDocs <@ greetWithDocs @>
    // greetWithDocs has no <param> tags, so description should be empty
    tool.Parameters[0].Description =! ""
