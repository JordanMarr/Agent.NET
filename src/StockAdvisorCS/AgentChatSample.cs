using Anthropic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace StockAdvisorCS;

// ============================================================================
// AGENT CHAT SAMPLE
// Demonstrates creating an AI agent with tools and interactive chat.
// Uses Claude via the Anthropic API.
// ============================================================================

public static class AgentChatSample
{
    private static AIAgent? _cachedAgent;

    private static IChatClient CreateChatClient()
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is not set.");
        var model = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-sonnet-4-20250514";

        var client = new AnthropicClient { ApiKey = apiKey };
        return client.AsIChatClient(model);
    }

    private static AIAgent CreateStockAdvisor(IChatClient chatClient)
    {
        return new ChatClientAgent(
            chatClient,
            name: "StockAdvisor",
            instructions: """
                You are a helpful stock analysis assistant. You help users analyze stocks,
                compare investments, and understand market metrics.

                When a user asks about stocks:
                1. Use the available tools to gather relevant data
                2. Analyze the information
                3. Provide clear, actionable insights

                After providing your analysis, also consider broader market context:
                - Market sector trends
                - Economic factors that might affect the stock
                - Risk considerations based on current market conditions
                - A final investment recommendation summary

                Be concise but thorough in your analysis.
                """,
            tools:
            [
                AIFunctionFactory.Create(StockTools.GetStockInfo),
                AIFunctionFactory.Create(StockTools.GetHistoricalPrices),
                AIFunctionFactory.Create(StockTools.CalculateVolatility),
                AIFunctionFactory.Create(StockTools.CompareStocks)
            ]);
    }

    private static async Task ChatLoopAsync(AIAgent agent)
    {
        Console.WriteLine("\nAgent Chat Mode - Type 'back' to return to menu\n");

        var thread = agent.GetNewThread();

        while (true)
        {
            Console.Write("You: ");
            var input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input))
                continue;

            if (input.Equals("back", StringComparison.OrdinalIgnoreCase))
                break;

            Console.WriteLine();
            var response = await agent.RunAsync(input, thread);
            Console.WriteLine($"StockAdvisor:\n{response.Text}\n");
        }
    }

    /// <summary>
    /// Runs the agent chat sample.
    /// </summary>
    public static async Task RunAsync()
    {
        if (_cachedAgent == null)
        {
            Console.WriteLine("\nInitializing agent (requires Anthropic API key)...");
            try
            {
                var chatClient = CreateChatClient();
                _cachedAgent = CreateStockAdvisor(chatClient);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine("Agent chat requires ANTHROPIC_API_KEY environment variable.");
                return;
            }
        }

        await ChatLoopAsync(_cachedAgent);
    }
}
