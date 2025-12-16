using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using StockAdvisorCS;

// Get Azure OpenAI endpoint from environment variable
var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
    ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT environment variable is not set");

var deploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";

// Create the Azure OpenAI client with DefaultAzureCredential
var azureClient = new AzureOpenAIClient(
    new Uri(endpoint),
    new DefaultAzureCredential());

// Get an IChatClient from the Azure OpenAI client
IChatClient chatClient = azureClient.GetChatClient(deploymentName).AsIChatClient();

// Create the StockAdvisor agent with tools
AIAgent stockAdvisor = new ChatClientAgent(
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

// Chat loop
Console.WriteLine("Stock Advisor Agent (Microsoft Agent Framework)");
Console.WriteLine("================================================");
Console.WriteLine("Ask me about stocks! (Type 'exit' to quit)\n");

AgentThread thread = stockAdvisor.GetNewThread();

while (true)
{
    Console.Write("You: ");
    var input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input))
        continue;

    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
        break;

    Console.WriteLine();

    var response = await stockAdvisor.RunAsync(input, thread);
    Console.WriteLine($"StockAdvisor:\n{response.Text}");

    Console.WriteLine();
}

Console.WriteLine("Goodbye!");
