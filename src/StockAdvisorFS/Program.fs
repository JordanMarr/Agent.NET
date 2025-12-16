open System
open AgentNet
open Azure.AI.OpenAI
open Azure.Identity
open Microsoft.Extensions.AI
open StockAdvisorFS.StockTools

// ============================================================================
// AGENT DEFINITION - Clean, declarative, beautiful!
// Compare this to the C# version in StockAdvisorCS...
// ============================================================================

let stockAdvisor = agent {
    name "StockAdvisor"
    instructions """
        You are a helpful stock analysis assistant. You help users analyze stocks,
        compare investments, and understand market metrics.

        When a user asks about stocks:
        1. Use the available tools to gather relevant data
        2. Analyze the information
        3. Provide clear, actionable insights

        Be concise but thorough in your analysis.
        """

    // Register tools - each is just one line!
    add stockInfoTool
    add historicalTool
    add volatilityTool
    add compareTool
}

// ============================================================================
// MAIN - Wire up Azure OpenAI and run the chat loop
// ============================================================================

[<EntryPoint>]
let main args =
    // Get Azure OpenAI endpoint
    let endpoint =
        Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
        |> Option.ofObj
        |> Option.defaultWith (fun () ->
            failwith "AZURE_OPENAI_ENDPOINT environment variable is not set")

    let deploymentName =
        Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")
        |> Option.ofObj
        |> Option.defaultValue "gpt-4o"

    // Create Azure OpenAI client with DefaultAzureCredential
    let client = AzureOpenAIClient(Uri(endpoint), DefaultAzureCredential())
    let chatClient = client.GetChatClient(deploymentName).AsIChatClient()

    // Build the agent
    let agent = stockAdvisor.Build(chatClient)

    // Chat loop
    printfn "Stock Advisor Agent (F# Edition)"
    printfn "================================="
    printfn "Ask me about stocks! (Type 'exit' to quit)\n"

    let rec loop () =
        printf "You: "
        let input = Console.ReadLine()

        if String.IsNullOrWhiteSpace(input) then
            loop ()
        elif input.Equals("exit", StringComparison.OrdinalIgnoreCase) then
            ()
        else
            printf "\nStockAdvisor: "
            let response = agent.Chat input |> Async.RunSynchronously
            printfn "%s\n" response
            loop ()

    loop ()
    printfn "Goodbye!"
    0
