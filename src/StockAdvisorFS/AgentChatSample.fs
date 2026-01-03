module StockAdvisorFS.AgentChatSample

open System
open System.Threading.Tasks
open AgentNet
open Anthropic
open Microsoft.Extensions.AI

// ============================================================================
// AGENT CHAT SAMPLE
// Demonstrates creating an AI agent with tools and interactive chat.
// Uses Claude via the Anthropic API.
// ============================================================================

let private failIfNone msg opt = opt |> Option.defaultWith (fun () -> failwith msg)
let private tryGetEnv = Environment.GetEnvironmentVariable >> Option.ofObj

let private createChatClient () =
    let apiKey = tryGetEnv "ANTHROPIC_API_KEY" |> failIfNone "ANTHROPIC_API_KEY environment variable is not set."
    let model = tryGetEnv "ANTHROPIC_MODEL" |> Option.defaultValue "claude-sonnet-4-20250514"

    let client = AnthropicClient(APIKey = apiKey)
    client.AsIChatClient(model)

let private createStockAdvisor (chatClient: IChatClient) =
    // Create tools using XML doc comments for descriptions
    let stockInfoTool =  Tool.createWithDocs <@ StockTools.getStockInfo @>
    let historicalTool = Tool.createWithDocs <@ StockTools.getHistoricalPrices @>
    let volatilityTool = Tool.createWithDocs <@ StockTools.calculateVolatility @>
    let compareTool =    Tool.createWithDocs <@ StockTools.compareStocks @>

    ChatAgent.create """
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
        Be concise but thorough in your analysis."""
    |> ChatAgent.withName "StockAdvisor"
    |> ChatAgent.withTools [stockInfoTool; historicalTool; volatilityTool; compareTool]
    |> ChatAgent.build chatClient

let private chatLoop (agent: ChatAgent) = task {
    printfn "\nAgent Chat Mode - Type 'back' to return to menu\n"

    let rec loop () = task {
        printf "You: "
        let input = Console.ReadLine()

        if String.IsNullOrWhiteSpace(input) then
            return! loop ()
        elif input.Equals("back", StringComparison.OrdinalIgnoreCase) then
            return ()
        else
            printf "\nStockAdvisor: "
            let! response = agent.Chat(input)
            printfn "%s\n" response
            return! loop ()
    }

    do! loop ()
}

/// Runs the agent chat sample. Returns the agent for reuse.
let run (existingAgent: ChatAgent option) : Task<ChatAgent option> = task {
    match existingAgent with
    | Some agent ->
        do! chatLoop agent
        return Some agent
    | None ->
        printfn "\nInitializing agent (requires Anthropic API key)..."
        try
            let chatClient = createChatClient ()
            let agent = createStockAdvisor chatClient
            do! chatLoop agent
            return Some agent
        with ex ->
            printfn $"\nError: {ex.Message}"
            printfn "Agent chat requires ANTHROPIC_API_KEY environment variable."
            return None
}
