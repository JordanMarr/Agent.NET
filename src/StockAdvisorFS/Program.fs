open System
open AgentNet
open Azure.AI.OpenAI
open Azure.Identity
open Microsoft.Extensions.AI
open StockAdvisorFS.StockTools

let failIfNone msg opt = opt |> Option.defaultWith (fun () -> failwith msg)
let tryGetEnv = Environment.GetEnvironmentVariable >> Option.ofObj

let endpoint = tryGetEnv "AZURE_OPENAI_ENDPOINT" |> failIfNone "AZURE_OPENAI_ENDPOINT environment variable is not set."
let deploymentName = tryGetEnv "AZURE_OPENAI_DEPLOYMENT" |> Option.defaultValue "gpt-4o"

let client = AzureOpenAIClient(Uri(endpoint), DefaultAzureCredential())
let chatClient = client.GetChatClient(deploymentName).AsIChatClient()
let stockAdvisorAgent = 
    agent {
        name "StockAdvisor"
        instructions """
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
            """

        add stockInfoTool
        add historicalTool
        add volatilityTool
        add compareTool
    }

let agent = stockAdvisorAgent.Build(chatClient)

printfn "Stock Advisor Agent (F# Edition)"
printfn "================================="
printfn "Ask me about stocks! (Type 'exit' to quit)\n"

let rec loop () = async {
    printf "You: "
    let input = Console.ReadLine()

    if String.IsNullOrWhiteSpace(input) then
        return! loop ()
    elif input.Equals("exit", StringComparison.OrdinalIgnoreCase) then
        return ()
    else
        printf "\nStockAdvisor: "
        let! response = agent.Chat input
        printfn "%s\n" response
        return! loop ()
}

loop () |> Async.RunSynchronously
printfn "Goodbye!"
