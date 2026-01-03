using StockAdvisorCS;

// ============================================================================
// STOCK ADVISOR - MAIN MENU
// ============================================================================

// Load .env from current dir or any parent directory
DotNetEnv.Env.TraversePath().Load();

Console.WriteLine("Stock Advisor Agent (C# Edition)");
Console.WriteLine("================================");

while (true)
{
    ShowMenu();
    var choice = Console.ReadLine();

    switch (choice)
    {
        case "1":
            await AgentChatSample.RunAsync();
            break;

        case "2":
            await WorkflowSample.RunAsync("AAPL", "MSFT");
            break;

        case "3":
            await WorkflowSample.RunAsync("GOOGL", "AMZN");
            break;

        case "4":
            await SingleAnalysisSample.RunAsync("TSLA");
            break;

        case "5":
            Console.Write("\nEnter first stock symbol: ");
            var sym1 = Console.ReadLine()?.Trim().ToUpper() ?? "";
            Console.Write("Enter second stock symbol: ");
            var sym2 = Console.ReadLine()?.Trim().ToUpper() ?? "";
            if (!string.IsNullOrWhiteSpace(sym1) && !string.IsNullOrWhiteSpace(sym2))
            {
                await WorkflowSample.RunAsync(sym1, sym2);
            }
            else
            {
                Console.WriteLine("Invalid symbols.");
            }
            break;

        case "6":
        case "exit":
            Console.WriteLine("\nGoodbye!");
            return;

        default:
            Console.WriteLine("\nInvalid option. Please try again.");
            break;
    }
}

void ShowMenu()
{
    Console.WriteLine();
    Console.WriteLine("Stock Advisor - Demo Scenarios");
    Console.WriteLine("==============================");
    Console.WriteLine("1. Agent Chat (interactive - requires Claude API)");
    Console.WriteLine("2. Workflow: Compare AAPL vs MSFT");
    Console.WriteLine("3. Workflow: Compare GOOGL vs AMZN");
    Console.WriteLine("4. Single Stock Analysis (TSLA)");
    Console.WriteLine("5. Custom Workflow Comparison");
    Console.WriteLine("6. Exit");
    Console.WriteLine();
    Console.Write("Select option: ");
}
