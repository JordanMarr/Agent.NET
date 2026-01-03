module StockAdvisorFS.WorkflowSample

open System
open System.Threading.Tasks
open AgentNet
open Anthropic
open Microsoft.Extensions.AI

// ============================================================================
// WORKFLOW SAMPLE
// Demonstrates the AgentNet workflow DSL with parallel execution,
// sequential pipeline, and AI agent integration for comparing two stocks.
// ============================================================================

// ----------------------------------------------------------------------------
// Chat Client Setup
// ----------------------------------------------------------------------------

let private failIfNone msg opt = opt |> Option.defaultWith (fun () -> failwith msg)
let private tryGetEnv = Environment.GetEnvironmentVariable >> Option.ofObj

let private createChatClient () =
    let apiKey = tryGetEnv "ANTHROPIC_API_KEY" |> failIfNone "ANTHROPIC_API_KEY environment variable is not set."
    let model = tryGetEnv "ANTHROPIC_MODEL" |> Option.defaultValue "claude-sonnet-4-20250514"
    let client = AnthropicClient(APIKey = apiKey)
    client.AsIChatClient(model)

let private createAnalysisAgent (chatClient: IChatClient) =
    Agent.create """
        You are a stock comparison analyst. Given information about two stocks,
        provide a concise comparison highlighting:
        - Which stock appears stronger and why
        - Key differences in their metrics
        - Risk considerations
        - A brief recommendation
        Keep your response focused and under 200 words.
        """
    |> Agent.withName "StockAnalyst"
    |> Agent.build chatClient

// ----------------------------------------------------------------------------
// Domain Types
// ----------------------------------------------------------------------------

type StockSymbols = { Symbol1: string; Symbol2: string }
type StockData = { Symbol: string; Info: string; Volatility: string }
type StockPair = { Stock1: StockData; Stock2: StockData }
type AnalysisResult = { Pair: StockPair; Analysis: string }

// ----------------------------------------------------------------------------
// Workflow Executors
// ----------------------------------------------------------------------------

/// Fetches stock info and volatility for a symbol
let private fetchStockData (symbol: string) : Task<StockData> = task {
    let! info = StockTools.getStockInfo symbol
    let! volatility = StockTools.calculateVolatility symbol
    return { Symbol = symbol; Info = info; Volatility = volatility }
}

/// Fetches data for both stocks in parallel
let private fetchBothStocks (symbols: StockSymbols) : Task<StockPair> = task {
    let! results = Task.WhenAll([|
        fetchStockData symbols.Symbol1
        fetchStockData symbols.Symbol2
    |])
    return { Stock1 = results.[0]; Stock2 = results.[1] }
}

/// Creates an executor that uses the AI agent to compare stocks
let private createCompareExecutor (agent: ChatAgent) =
    let formatPrompt (pair: StockPair) =
        $"""Compare these two stocks:

{pair.Stock1.Symbol}:
{pair.Stock1.Info}
Volatility: {pair.Stock1.Volatility}

{pair.Stock2.Symbol}:
{pair.Stock2.Info}
Volatility: {pair.Stock2.Volatility}"""

    let parseResponse pair response =
        { Pair = pair; Analysis = response }

    Executor.fromAgentWith "CompareStocks" formatPrompt parseResponse agent

/// Generates a formatted report
let private generateReport (result: AnalysisResult) : string =
    $"""
================================================================================
                        STOCK COMPARISON REPORT
================================================================================

--- {result.Pair.Stock1.Symbol} ---
{result.Pair.Stock1.Info}

{result.Pair.Stock1.Volatility}

--- {result.Pair.Stock2.Symbol} ---
{result.Pair.Stock2.Info}

{result.Pair.Stock2.Volatility}

--- AI ANALYSIS ---
{result.Analysis}
================================================================================
"""

// ----------------------------------------------------------------------------
// Workflow Definition
// ----------------------------------------------------------------------------

/// Multi-stock comparison workflow using the AgentNet workflow DSL
let private stockComparisonWorkflow (agent: ChatAgent) = workflow {
    start (Executor.fromTask "FetchBothStocks" fetchBothStocks)
    next (createCompareExecutor agent)
    next (Executor.fromFn "GenerateReport" generateReport)
}

// ----------------------------------------------------------------------------
// Public Entry Point
// ----------------------------------------------------------------------------

/// Runs the workflow sample comparing two stocks
let run (symbol1: string) (symbol2: string) : Task<unit> = task {
    printfn $"\nRunning comparison workflow for {symbol1} vs {symbol2}..."
    printfn "Initializing AI agent..."

    let chatClient = createChatClient ()
    let agent = createAnalysisAgent chatClient
    let wf = stockComparisonWorkflow agent

    printfn "Step 1: Fetching stock data in parallel..."
    printfn "Step 2: AI agent analyzing stocks..."

    let input = { Symbol1 = symbol1; Symbol2 = symbol2 }
    let! report = Workflow.run input wf

    printfn "%s" report
}
