/// Trade Approval Workflow - Azure Durable Functions Example
///
/// This demonstrates how AgentNet workflows integrate with Azure Durable Functions
/// for long-running, human-in-the-loop processes.
///
/// Run with: func start
///
/// Endpoints:
///   POST /api/trade/start          - Start a new trade approval workflow
///   POST /api/trade/{id}/approve   - Approve or reject a pending trade
///   GET  /api/trade/{id}/status    - Check workflow status
module Samples.DurableFunctions.TradeApprovalWorkflow

open System
open System.Threading.Tasks
open Microsoft.Azure.Functions.Worker
open Microsoft.Azure.Functions.Worker.Http
open Microsoft.DurableTask
open Microsoft.DurableTask.Client
open Microsoft.Extensions.Logging
open AgentNet
open AgentNet.Durable

// ============================================================================
// DOMAIN TYPES
// ============================================================================

type TradeRequest =
    { Symbol: string
      Amount: decimal }

type StockAnalysis =
    { Symbol: string
      Recommendation: string
      Confidence: float }

type ApprovalDecision =
    { Approved: bool
      ApproverName: string
      Comments: string }

type TradeResult =
    { Symbol: string
      Status: string
      Details: string }

// ============================================================================
// WORKFLOW STEPS - Plain .NET functions, AgentNet handles the rest!
// ============================================================================

/// Analyzes a stock and produces a recommendation
let analyzeStock (request: TradeRequest) : Task<StockAnalysis> = task {
    // In a real app, this would call an AI agent for analysis
    return {
        Symbol = request.Symbol
        Recommendation = $"BUY {request.Symbol} - Strong momentum detected"
        Confidence = 0.85
    }
}

/// Sends the analysis for human review (e.g., email, Teams notification)
let sendForApproval (analysis: StockAnalysis) : Task<StockAnalysis> = task {
    // In a real app, this would send a notification
    printfn $"[Notification] Trade approval requested for {analysis.Symbol}"
    printfn $"[Notification] Recommendation: {analysis.Recommendation}"
    printfn $"[Notification] Confidence: {analysis.Confidence:P0}"
    return analysis
}

/// Executes or cancels the trade based on approval decision
let executeTrade (decision: ApprovalDecision) : Task<TradeResult> = task {
    if decision.Approved then
        return {
            Symbol = ""
            Status = "EXECUTED"
            Details = $"Approved by {decision.ApproverName}: {decision.Comments}"
        }
    else
        return {
            Symbol = ""
            Status = "CANCELLED"
            Details = $"Rejected by {decision.ApproverName}: {decision.Comments}"
        }
}

// ============================================================================
// THE WORKFLOW - Clean, declarative, durable!
// ============================================================================

/// A durable workflow that:
/// 1. Analyzes a stock using AI
/// 2. Sends for human approval
/// 3. Waits for approval decision (can take days - workflow survives restarts)
/// 4. Executes or cancels based on decision
let tradeApprovalWorkflow =
    workflow {
        name "TradeApprovalWorkflow"
        step analyzeStock
        step sendForApproval
        awaitEvent "TradeApproval" eventOf<ApprovalDecision>
        step executeTrade
    }

// ============================================================================
// AZURE FUNCTION - Just the orchestrator, that's it!
// ============================================================================

[<Function("TradeApprovalOrchestrator")>]
let orchestrator ([<OrchestrationTrigger>] ctx: TaskOrchestrationContext) =
    let request = ctx.GetInput<TradeRequest>()
    DurableWorkflow.run ctx request tradeApprovalWorkflow

// --- HTTP Triggers ---

[<Function("StartTradeApproval")>]
let startTradeApproval
    ([<HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "trade/start")>]
     req: HttpRequestData,
     [<DurableClient>] client: DurableTaskClient,
     executionContext: FunctionContext) : Task<HttpResponseData> =
    task {
        let log = executionContext.GetLogger("StartTradeApproval")
        let! request = req.ReadFromJsonAsync<TradeRequest>()

        let! instanceId = client.ScheduleNewOrchestrationInstanceAsync("TradeApprovalOrchestrator", request)
        log.LogInformation($"Started trade approval workflow: {instanceId}")

        let response = req.CreateResponse(System.Net.HttpStatusCode.Accepted)
        do! response.WriteAsJsonAsync({|
            InstanceId = instanceId
            Message = $"Trade analysis started for {request.Symbol}"
            StatusUrl = $"/api/trade/{instanceId}/status"
            ApproveUrl = $"/api/trade/{instanceId}/approve"
        |})
        return response
    }

[<Function("ApproveTradeDecision")>]
let approveTrade
    ([<HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "trade/{instanceId}/approve")>]
     req: HttpRequestData,
     instanceId: string,
     [<DurableClient>] client: DurableTaskClient,
     executionContext: FunctionContext) : Task<HttpResponseData> =
    task {
        let log = executionContext.GetLogger("ApproveTradeDecision")
        let! decision = req.ReadFromJsonAsync<ApprovalDecision>()

        do! client.RaiseEventAsync(instanceId, "TradeApproval", decision)
        log.LogInformation($"Approval decision recorded for {instanceId}: Approved={decision.Approved}")

        let response = req.CreateResponse(System.Net.HttpStatusCode.OK)
        do! response.WriteAsJsonAsync({|
            Message = "Approval decision recorded"
            Approved = decision.Approved
        |})
        return response
    }

[<Function("GetTradeStatus")>]
let getTradeStatus
    ([<HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "trade/{instanceId}/status")>]
     req: HttpRequestData,
     instanceId: string,
     [<DurableClient>] client: DurableTaskClient,
     executionContext: FunctionContext) : Task<HttpResponseData> =
    task {
        let! metadata = client.GetInstanceAsync(instanceId)

        let response = req.CreateResponse(System.Net.HttpStatusCode.OK)
        if metadata = null then
            do! response.WriteAsJsonAsync({| Error = "Instance not found" |})
        else
            do! response.WriteAsJsonAsync({|
                InstanceId = metadata.InstanceId
                Status = metadata.RuntimeStatus.ToString()
                CreatedAt = metadata.CreatedAt
                LastUpdatedAt = metadata.LastUpdatedAt
            |})
        return response
    }
