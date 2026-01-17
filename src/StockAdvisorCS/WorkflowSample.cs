using Anthropic;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace StockAdvisorCS;

// ============================================================================
// WORKFLOW SAMPLE
// Demonstrates the MAF WorkflowBuilder with parallel execution,
// sequential pipeline, and AI agent integration for comparing two stocks.
// ============================================================================

// ----------------------------------------------------------------------------
// Domain Types
// ----------------------------------------------------------------------------

public record StockSymbols(string Symbol1, string Symbol2);
public record StockData(string Symbol, string Info, string Volatility);
public record StockPair(StockData Stock1, StockData Stock2);
public record AnalysisResult(StockPair Pair, string Analysis);

// ----------------------------------------------------------------------------
// Workflow Executors
// ----------------------------------------------------------------------------

internal sealed class FetchBothStocksExecutor() : Executor<StockSymbols, StockPair>("FetchBothStocks")
{
    public override async ValueTask<StockPair> HandleAsync(
        StockSymbols symbols,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("  [FetchBothStocks] Fetching stock data in parallel...");

        var task1 = FetchStockDataAsync(symbols.Symbol1);
        var task2 = FetchStockDataAsync(symbols.Symbol2);
        await Task.WhenAll(task1, task2);

        return new StockPair(task1.Result, task2.Result);
    }

    private static async Task<StockData> FetchStockDataAsync(string symbol)
    {
        var info = await StockTools.GetStockInfo(symbol);
        var volatility = await StockTools.CalculateVolatility(symbol);
        return new StockData(symbol, info, volatility);
    }
}

internal sealed class CompareStocksExecutor(AIAgent agent) : Executor<StockPair, AnalysisResult>("CompareStocks")
{
    public override async ValueTask<AnalysisResult> HandleAsync(
        StockPair pair,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("  [CompareStocks] AI agent analyzing stocks...");

        var prompt = $"""
            Compare these two stocks:

            {pair.Stock1.Symbol}:
            {pair.Stock1.Info}
            Volatility: {pair.Stock1.Volatility}

            {pair.Stock2.Symbol}:
            {pair.Stock2.Info}
            Volatility: {pair.Stock2.Volatility}
            """;

        var thread = agent.GetNewThread();
        var response = await agent.RunAsync(prompt, thread);
        return new AnalysisResult(pair, response.Text);
    }
}

internal sealed class GenerateReportExecutor() : Executor<AnalysisResult, string>("GenerateReport")
{
    public override ValueTask<string> HandleAsync(
        AnalysisResult result,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("  [GenerateReport] Generating report...");
        var report = $"""

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
""";
        return ValueTask.FromResult(report);
    }
}

// ----------------------------------------------------------------------------
// Workflow Sample
// ----------------------------------------------------------------------------

public static class WorkflowSample
{
    private static IChatClient CreateChatClient()
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is not set.");
        var model = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-sonnet-4-20250514";

        var client = new AnthropicClient { ApiKey = apiKey };
        return client.AsIChatClient(model);
    }

    private static AIAgent CreateAnalysisAgent(IChatClient chatClient)
    {
        return new ChatClientAgent(
            chatClient,
            name: "StockAnalyst",
            instructions: """
                You are a stock comparison analyst. Given information about two stocks,
                provide a concise comparison highlighting:
                - Which stock appears stronger and why
                - Key differences in their metrics
                - Risk considerations
                - A brief recommendation
                Keep your response focused and under 200 words.
                """);
    }

    private static Workflow BuildWorkflow(AIAgent agent)
    {
        // Create executors
        var fetchBothStocks = new FetchBothStocksExecutor();
        var compareStockPair = new CompareStocksExecutor(agent);
        var reportExecutor = new GenerateReportExecutor();

        // Build the workflow using MAF WorkflowBuilder
        WorkflowBuilder builder = new(fetchBothStocks);
        builder.AddEdge(fetchBothStocks, compareStockPair);
        builder.AddEdge(compareStockPair, reportExecutor);
        builder.WithOutputFrom(reportExecutor);
        return builder.Build();
    }

    /// <summary>
    /// Runs the workflow sample comparing two stocks.
    /// </summary>
    public static async Task RunAsync(string symbol1, string symbol2)
    {
        Console.WriteLine($"\nRunning MAF workflow for {symbol1} vs {symbol2}...");
        Console.WriteLine("Initializing AI agent...");

        var chatClient = CreateChatClient();
        var agent = CreateAnalysisAgent(chatClient);
        var workflow = BuildWorkflow(agent);

        var input = new StockSymbols(symbol1, symbol2);

        await using var run = await InProcessExecution.RunAsync(workflow, input);

        // Process workflow events
        foreach (var evt in run.NewEvents)
        {
            if (evt is ExecutorCompletedEvent completed && completed.ExecutorId == "GenerateReport")
            {
                Console.WriteLine(completed.Data);
            }
        }
    }
}
