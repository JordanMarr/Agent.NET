open System
open AgentNet
open DotNetEnv

// ============================================================================
// STOCK ADVISOR - MAIN MENU
// ============================================================================

let showMenu () =
    printfn ""
    printfn "Stock Advisor - Demo Scenarios"
    printfn "=============================="
    printfn "1. Agent Chat (interactive - requires Claude API)"
    printfn "2. Workflow: Compare AAPL vs MSFT"
    printfn "3. Workflow: Compare GOOGL vs AMZN"
    printfn "4. Single Stock Analysis (TSLA)"
    printfn "5. Custom Workflow Comparison"
    printfn "6. Exit"
    printfn ""
    printf "Select option: "

let rec mainLoop (agentOpt: ChatAgent option) = task {
    showMenu ()

    match Console.ReadLine() with
    | "1" ->
        let! newAgentOpt = StockAdvisorFS.AgentChatSample.run agentOpt
        return! mainLoop newAgentOpt

    | "2" ->
        do! StockAdvisorFS.WorkflowSample.run "AAPL" "MSFT"
        return! mainLoop agentOpt

    | "3" ->
        do! StockAdvisorFS.WorkflowSample.run "GOOGL" "AMZN"
        return! mainLoop agentOpt

    | "4" ->
        do! StockAdvisorFS.SingleAnalysisSample.run "TSLA"
        return! mainLoop agentOpt

    | "5" ->
        printf "\nEnter first stock symbol: "
        let sym1 = Console.ReadLine().Trim().ToUpper()
        printf "Enter second stock symbol: "
        let sym2 = Console.ReadLine().Trim().ToUpper()
        if not (String.IsNullOrWhiteSpace sym1) && not (String.IsNullOrWhiteSpace sym2) then
            do! StockAdvisorFS.WorkflowSample.run sym1 sym2
        else
            printfn "Invalid symbols."
        return! mainLoop agentOpt

    | "6" | "exit" ->
        printfn "\nGoodbye!"
        return ()

    | _ ->
        printfn "\nInvalid option. Please try again."
        return! mainLoop agentOpt
}

// ============================================================================
// ENTRY POINT
// ============================================================================

// Load .env from current dir or any parent directory
Env.TraversePath().Load() |> ignore

printfn "Stock Advisor Agent (F# Edition)"
printfn "================================="

mainLoop None |> fun t -> t.Wait()
