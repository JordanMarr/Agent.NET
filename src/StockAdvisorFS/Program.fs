open System
open System.Threading.Tasks
open AgentNet
open Azure.AI.OpenAI
open Azure.Identity
open Microsoft.Extensions.AI
open StockAdvisorFS

let failIfNone msg opt = opt |> Option.defaultWith (fun () -> failwith msg)
let tryGetEnv = Environment.GetEnvironmentVariable >> Option.ofObj

let endpoint = tryGetEnv "AZURE_OPENAI_ENDPOINT" |> failIfNone "AZURE_OPENAI_ENDPOINT environment variable is not set."
let deploymentName = tryGetEnv "AZURE_OPENAI_DEPLOYMENT" |> Option.defaultValue "gpt-4o"

let client = AzureOpenAIClient(Uri(endpoint), DefaultAzureCredential())
let chatClient = client.GetChatClient(deploymentName).AsIChatClient()

// Create tools using XML doc comments for descriptions
let stockInfoTool =  Tool.createWithDocs <@ StockTools.getStockInfo @>
let historicalTool = Tool.createWithDocs <@ StockTools.getHistoricalPrices @>
let volatilityTool = Tool.createWithDocs <@ StockTools.calculateVolatility @>
let compareTool =    Tool.createWithDocs <@ StockTools.compareStocks @>

// Create the Stock Advisor agent
let stockAdvisor = 
    Agent.create """
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
    |> Agent.withName "StockAdvisor"
    |> Agent.withTools [stockInfoTool; historicalTool; volatilityTool; compareTool]
    |> Agent.build chatClient

printfn "Stock Advisor Agent (F# Edition)"
printfn "================================="
printfn "Ask me about stocks! (Type 'exit' to quit)\n"

let rec loop () = task {
    printf "You: "
    let input = Console.ReadLine()

    if String.IsNullOrWhiteSpace(input) then
        return! loop ()
    elif input.Equals("exit", StringComparison.OrdinalIgnoreCase) then
        return ()
    else
        printf "\nStockAdvisor: "
        let! response = stockAdvisor.Chat(input)
        printfn "%s\n" response
        return! loop ()
}

loop () |> Task.WaitAll
printfn "Goodbye!"
