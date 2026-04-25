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

// --- Tool.inject tests ---------------------------------------------------

type IClock =
    abstract Now : unit -> string

type FakeClock(stamp: string) =
    interface IClock with
        member _.Now() = stamp

// Function with a dep + one regular param.
let lookupUser (db: string) (userId: int) : string =
    $"{db}:user{userId}"

// Function with a dep + multiple regular params (covers the Ldarg loop).
/// <summary>Format a price using the given currency formatter</summary>
/// <param name="formatter">The currency formatter service</param>
/// <param name="symbol">The stock ticker symbol</param>
/// <param name="price">The current price</param>
/// <param name="currency">The currency code</param>
let formatWithService (formatter: string) (symbol: string) (price: decimal) (currency: string) : string =
    $"{formatter}|{symbol}: {price} {currency}"

// Function whose only parameter is the dep — after injection it has zero params.
let depOnly (svc: string) : string =
    $"hello {svc}"

// Function with dep + trailing unit — after injection only `unit` remains.
let nowFromClock (clock: IClock) () : string =
    clock.Now()

[<Test>]
let ``Tool_inject removes the leftmost parameter from the Parameters list`` () =
    let tool =
        Tool.create <@ lookupUser @>
        |> Tool.inject "prod-db"
    tool.Parameters.Length =! 1
    tool.Parameters[0].Name =! "userId"
    tool.Parameters[0].Type =! typeof<int>

[<Test>]
let ``Tool_inject preserves remaining parameter names and types in order`` () =
    let tool =
        Tool.create <@ formatWithService @>
        |> Tool.inject "fmt-svc"
    tool.Parameters
    |> List.map (fun p -> p.Name, p.Type)
    =! [ "symbol", typeof<string>; "price", typeof<decimal>; "currency", typeof<string> ]

[<Test>]
let ``Tool_inject preserves param descriptions from XML docs (dep param dropped)`` () =
    let tool =
        Tool.createWithDocs <@ formatWithService @>
        |> Tool.inject "fmt-svc"
    tool.Parameters[0].Description =! "The stock ticker symbol"
    tool.Parameters[1].Description =! "The current price"
    tool.Parameters[2].Description =! "The currency code"

[<Test>]
let ``Tool_inject MethodInfo signature matches remaining Parameters`` () =
    let tool =
        Tool.create <@ formatWithService @>
        |> Tool.inject "fmt-svc"
    let methodParamTypes =
        tool.MethodInfo.GetParameters() |> Array.map (fun p -> p.ParameterType) |> Array.toList
    let toolParamTypes = tool.Parameters |> List.map (fun p -> p.Type)
    methodParamTypes =! toolParamTypes

[<Test>]
let ``Tool_inject MethodInfo parameter names match remaining Parameters`` () =
    let tool =
        Tool.create <@ formatWithService @>
        |> Tool.inject "fmt-svc"
    let methodParamNames =
        tool.MethodInfo.GetParameters() |> Array.map (fun p -> p.Name) |> Array.toList
    let toolParamNames = tool.Parameters |> List.map (fun p -> p.Name)
    methodParamNames =! toolParamNames

[<Test>]
let ``Tool_inject MethodInfo invokes underlying function with captured dep`` () =
    let tool =
        Tool.create <@ lookupUser @>
        |> Tool.inject "prod-db"
    let result = tool.MethodInfo.Invoke(null, [| box 42 |])
    result =! box "prod-db:user42"

[<Test>]
let ``Tool_inject preserves Name and Description on the resulting tool`` () =
    let tool =
        Tool.create <@ lookupUser @>
        |> Tool.describe "Looks up a user"
        |> Tool.inject "prod-db"
    tool.Name =! "lookupUser"
    tool.Description =! "Looks up a user"

[<Test>]
let ``Tool_inject works when the dep is the only parameter`` () =
    let tool =
        Tool.create <@ depOnly @>
        |> Tool.inject "world"
    tool.Parameters =! []
    tool.MethodInfo.GetParameters().Length =! 0
    let result = tool.MethodInfo.Invoke(null, [||])
    result =! box "hello world"

[<Test>]
let ``Tool_inject works when the only remaining parameter is unit`` () =
    let clock = FakeClock("2026-04-25") :> IClock
    let tool =
        Tool.create <@ nowFromClock @>
        |> Tool.inject clock
    // The trailing `()` compiles to a Microsoft.FSharp.Core.Unit param.
    tool.Parameters.Length =! 1
    tool.Parameters[0].Type =! typeof<unit>
    let methodParams = tool.MethodInfo.GetParameters()
    methodParams.Length =! 1
    methodParams[0].ParameterType =! typeof<unit>
    // Unit's runtime representation is null.
    let result = tool.MethodInfo.Invoke(null, [| null |])
    result =! box "2026-04-25"

[<Test>]
let ``Tool_inject MethodInfo has a non-null DeclaringType`` () =
    // Reflection consumers (including some MAF code paths) check DeclaringType — make sure
    // the emitted forwarder lives on a real type, not a free-floating dynamic method.
    let tool =
        Tool.create <@ lookupUser @>
        |> Tool.inject "prod-db"
    tool.MethodInfo.DeclaringType <>! null

[<Test>]
let ``Tool_inject is composable across different deps without cross-contamination`` () =
    let toolA = Tool.create <@ lookupUser @> |> Tool.inject "db-A"
    let toolB = Tool.create <@ lookupUser @> |> Tool.inject "db-B"
    toolA.MethodInfo.Invoke(null, [| box 1 |]) =! box "db-A:user1"
    toolB.MethodInfo.Invoke(null, [| box 2 |]) =! box "db-B:user2"
