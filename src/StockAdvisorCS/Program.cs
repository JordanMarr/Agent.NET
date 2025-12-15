using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using StockAdvisorCS;

// Get API key from environment variable
var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
    ?? throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is not set");

// Create the Anthropic client and wrap it for SemanticKernel
var anthropicClient = new AnthropicClient(apiKey);
var chatService = new ChatClientBuilder(anthropicClient.Messages)
    .UseFunctionInvocation()
    .Build()
    .AsChatCompletionService();

// Build the kernel with Anthropic as the chat completion service
var builder = Kernel.CreateBuilder();
builder.Services.AddSingleton<IChatCompletionService>(chatService);
var kernel = builder.Build();

// Import our stock tools plugin
kernel.ImportPluginFromType<StockTools>();

// Create the ChatCompletionAgent
var agent = new ChatCompletionAgent
{
    Name = "StockAdvisor",
    Instructions = """
        You are a helpful stock analysis assistant. You help users analyze stocks,
        compare investments, and understand market metrics.

        When a user asks about stocks:
        1. Use the available tools to gather relevant data
        2. Analyze the information
        3. Provide clear, actionable insights

        Be concise but thorough in your analysis.
        """,
    Kernel = kernel,
    Arguments = new KernelArguments(
        new PromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
        }
    )
};

// Chat loop
Console.WriteLine("Stock Advisor Agent");
Console.WriteLine("===================");
Console.WriteLine("Ask me about stocks! (Type 'exit' to quit)\n");

var thread = new ChatHistoryAgentThread();

while (true)
{
    Console.Write("You: ");
    var input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input))
        continue;

    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
        break;

    Console.Write("\nStockAdvisor: ");

    await foreach (var response in agent.InvokeAsync(input, thread))
    {
        Console.Write(response.Message.Content);
    }

    Console.WriteLine("\n");
}

Console.WriteLine("Goodbye!");
