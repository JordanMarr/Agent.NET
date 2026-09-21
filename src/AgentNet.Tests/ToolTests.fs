module AgentNet.Tests.ToolTests

open NUnit.Framework
open Swensen.Unquote
open System.Text.Json
open Microsoft.Extensions.AI
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

// --- XML doc lookup with non-trivial parameter types (issue #8) -----------

// Declared inside a module, so its reflection name is nested (ToolTests+RoundDeps).
type RoundDeps = { ShowId: System.Guid }

type Outer<'a> = { Value: 'a }

/// <summary>Starts a new round of trivia in this party.</summary>
/// <param name="deps">Injected: never shown to the model.</param>
/// <param name="topic">What the round should be about.</param>
let startRound (deps: RoundDeps) (topic: string) : string =
    $"{deps.ShowId}:{topic}"

/// <summary>Searches with optional filters</summary>
/// <param name="query">The search text</param>
/// <param name="tags">Tags to filter by</param>
/// <param name="limit">Maximum number of results</param>
let searchWithGenerics (query: string option) (tags: string list) (limit: Map<string, int[]>) : string =
    $"{query}{tags}{limit}"

/// <summary>Takes a nested generic of a nested type</summary>
/// <param name="wrapped">The wrapped deps</param>
let nestedGeneric (wrapped: Outer<RoundDeps option>[]) : string =
    $"{wrapped}"

[<Test>]
let ``Tool_createWithDocs finds docs when a parameter type is nested in a module`` () =
    let tool = Tool.createWithDocs <@ startRound @>
    tool.Description =! "Starts a new round of trivia in this party."
    tool.Parameters[0].Description =! "Injected: never shown to the model."
    tool.Parameters[1].Description =! "What the round should be about."

[<Test>]
let ``Tool_createWithDocs finds docs when parameter types are generic`` () =
    let tool = Tool.createWithDocs <@ searchWithGenerics @>
    tool.Description =! "Searches with optional filters"
    tool.Parameters[0].Description =! "The search text"
    tool.Parameters[1].Description =! "Tags to filter by"
    tool.Parameters[2].Description =! "Maximum number of results"

[<Test>]
let ``Tool_createWithDocs finds docs for nested generic array parameter types`` () =
    let tool = Tool.createWithDocs <@ nestedGeneric @>
    tool.Description =! "Takes a nested generic of a nested type"
    tool.Parameters[0].Description =! "The wrapped deps"

[<Test>]
let ``Tool_createWithDocs then inject keeps docs when the dep is a nested record`` () =
    let tool =
        Tool.createWithDocs <@ startRound @>
        |> Tool.inject { ShowId = System.Guid.Empty }
    tool.Description =! "Starts a new round of trivia in this party."
    tool.Parameters.Length =! 1
    tool.Parameters[0].Description =! "What the round should be about."

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

// Tool.inject records the dep on the ToolDef; the schema/invocation behaviour is realised when the
// tool is converted to an AIFunction, so these go through the real factory.

let private schemaOf (tool: ToolDef) =
    (MAF.toolToAIFunction tool).JsonSchema

let private schemaPropertyNames (tool: ToolDef) =
    match (schemaOf tool).TryGetProperty "properties" with
    | true, props -> [ for p in props.EnumerateObject() -> p.Name ]
    | _ -> []

let private invoke (tool: ToolDef) (args: (string * obj) list) =
    let fn = MAF.toolToAIFunction tool
    let result = fn.InvokeAsync(AIFunctionArguments(dict args)).AsTask().Result
    match result with
    | :? JsonElement as j -> j.GetString()
    | other -> string other

[<Test>]
let ``Tool_inject leaves the underlying MethodInfo untouched and records a binding`` () =
    let original = Tool.create <@ formatWithService @>
    let tool = original |> Tool.inject "fmt-svc"
    tool.MethodInfo =! original.MethodInfo
    tool.Bindings.Positions =! [ 0 ]
    original.Bindings.IsEmpty =! true

[<Test>]
let ``Tool_inject never exposes injected values when a ToolDef is printed`` () =
    let tool =
        Tool.create <@ formatWithService @>
        |> Tool.inject "s3cr3t-api-key"
    let printed = [ sprintf "%A" tool; tool.ToString(); sprintf "%A" tool.Bindings; string tool.Bindings ]
    printed |> List.iter (fun text -> text.Contains "s3cr3t" =! false)
    sprintf "%A" tool.Bindings =! "<injected: 0>"

[<Test>]
let ``Tool_inject hides the injected parameter from the model schema`` () =
    let tool =
        Tool.create <@ formatWithService @>
        |> Tool.inject "fmt-svc"
    schemaPropertyNames tool =! (tool.Parameters |> List.map (fun p -> p.Name))
    schemaPropertyNames tool =! [ "symbol"; "price"; "currency" ]

[<Test>]
let ``Tool_inject invokes underlying function with captured dep`` () =
    let tool =
        Tool.create <@ lookupUser @>
        |> Tool.inject "prod-db"
    invoke tool [ "userId", box 42 ] =! "prod-db:user42"

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
    schemaPropertyNames tool =! []
    invoke tool [] =! "hello world"

[<Test>]
let ``Tool_inject works when the only remaining parameter is unit`` () =
    let clock = FakeClock("2026-04-25") :> IClock
    let tool =
        Tool.create <@ nowFromClock @>
        |> Tool.inject clock
    // The trailing `()` compiles to a Microsoft.FSharp.Core.Unit param.
    tool.Parameters.Length =! 1
    tool.Parameters[0].Type =! typeof<unit>
    // Unit's runtime representation is null.
    invoke tool [ tool.Parameters[0].Name, null ] =! "2026-04-25"

[<Test>]
let ``Tool_inject can be applied repeatedly, binding left to right`` () =
    let tool =
        Tool.create <@ formatWithService @>
        |> Tool.inject "fmt-svc"
        |> Tool.inject "MSFT"
    tool.Parameters |> List.map (fun p -> p.Name) =! [ "price"; "currency" ]
    schemaPropertyNames tool =! [ "price"; "currency" ]
    invoke tool [ "price", box 1.5m; "currency", box "USD" ] =! "fmt-svc|MSFT: 1.5 USD"

[<Test>]
let ``Tool_inject works with a record dep declared in a module`` () =
    let showId = System.Guid.NewGuid()
    let tool =
        Tool.createWithDocs <@ startRound @>
        |> Tool.inject { ShowId = showId }
    schemaPropertyNames tool =! [ "topic" ]
    invoke tool [ "topic", box "70s rock" ] =! $"{showId}:70s rock"

[<Test>]
let ``Tool_inject rejects a dep of the wrong type up front`` () =
    raises<exn> <@ Tool.create <@ lookupUser @> |> Tool.inject 42 @>

[<Test>]
let ``Tool_inject fails when every parameter is already injected`` () =
    raises<exn> <@ Tool.create <@ depOnly @> |> Tool.inject "a" |> Tool.inject "b" @>

[<Test>]
let ``Tool_inject does not retain deps after the ToolDef is dropped (issue #9)`` () =
    // Previously each call leaked a dynamic type plus a static dep-store entry holding the dep.
    let injectAndDrop () =
        let dep = { ShowId = System.Guid.NewGuid() }
        let tool = Tool.create <@ startRound @> |> Tool.inject dep
        MAF.toolToAIFunction tool |> ignore
        System.WeakReference dep
    let weak = injectAndDrop ()
    System.GC.Collect()
    System.GC.WaitForPendingFinalizers()
    System.GC.Collect()
    weak.IsAlive =! false

[<Test>]
let ``Tool_inject is composable across different deps without cross-contamination`` () =
    let toolA = Tool.create <@ lookupUser @> |> Tool.inject "db-A"
    let toolB = Tool.create <@ lookupUser @> |> Tool.inject "db-B"
    invoke toolA [ "userId", box 1 ] =! "db-A:user1"
    invoke toolB [ "userId", box 2 ] =! "db-B:user2"

// --- Param descriptions reach the model (issue #10) -----------------------

let private schemaParamDescription (tool: ToolDef) (name: string) =
    let prop = (schemaOf tool).GetProperty("properties").GetProperty(name)
    match prop.TryGetProperty "description" with
    | true, d -> Some (d.GetString())
    | _ -> None

[<Test>]
let ``XML param descriptions are passed through to the AIFunction schema`` () =
    let tool = Tool.createWithDocs <@ formatWithParamDocs @>
    schemaParamDescription tool "symbol" =! Some "The stock ticker symbol"
    schemaParamDescription tool "price" =! Some "The current price"
    schemaParamDescription tool "currency" =! Some "The currency code (e.g., USD)"

[<Test>]
let ``Params without a description get no schema description`` () =
    let tool = Tool.create <@ formatPrice @>
    schemaParamDescription tool "symbol" =! None

[<Test>]
let ``XML param descriptions survive Tool_inject`` () =
    let tool =
        Tool.createWithDocs <@ startRound @>
        |> Tool.inject { ShowId = System.Guid.Empty }
    schemaParamDescription tool "topic" =! Some "What the round should be about."
