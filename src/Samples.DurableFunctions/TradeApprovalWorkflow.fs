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

[<AutoOpen>]
module ApprovalWorkflow =

    // ============================================================================
    // WORKFLOW DOMAIN TYPES (Inputs/Outputs)
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
    let analyzeStock (request: TradeRequest) = 
        // In a real app, this would call an AI agent for analysis
        {
            Symbol = request.Symbol
            Recommendation = $"BUY {request.Symbol} - Strong momentum detected"
            Confidence = 0.85
        }
        |> Task.fromResult

    /// Sends the analysis for human review (e.g., email, Teams notification)
    let sendForApproval (analysis: StockAnalysis) : Task<StockAnalysis> = 
        // In a real app, this would send a notification
        printfn $"[Notification] Trade approval requested for {analysis.Symbol}"
        printfn $"[Notification] Recommendation: {analysis.Recommendation}"
        printfn $"[Notification] Confidence: {analysis.Confidence:P0}"
        analysis |> Task.fromResult

    /// Executes or cancels the trade based on approval decision
    let executeTrade (decision: ApprovalDecision) = 
        if decision.Approved then
            {
                Symbol = ""
                Status = "EXECUTED"
                Details = $"Approved by {decision.ApproverName}: {decision.Comments}"
            } |> Task.fromResult
        else
            {
                Symbol = ""
                Status = "CANCELLED"
                Details = $"Rejected by {decision.ApproverName}: {decision.Comments}"
            } |> Task.fromResult
    
    // ============================================================================
    // WORKFLOW DEFINITION - Composing steps into a durable workflow
    // ============================================================================

    /// A clean, declarative, durable workflow that:
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

/// ============================================================================
/// The Orchestrator Function and Workflow Definition
/// ============================================================================

/// Here, we stitch together a `workflow` from our defined steps that executes within the Durable Functions orchestrator.
[<Function("TradeApprovalOrchestrator")>]
let orchestrator ([<OrchestrationTrigger>] ctx: TaskOrchestrationContext) =
    let request = ctx.GetInput<TradeRequest>()

    // Run the defined workflow within the Durable Functions context!
    tradeApprovalWorkflow
    |> Workflow.Durable.run ctx request

/// ============================================================================
/// HTTP Trigger Endpoints to Start Workflow, Approve, and Check Status
/// ============================================================================

/// URL: "/api/trade/start"
/// An HTTP Trigger to schedule a new orchestration instanceId and kick-off the trade approval workflow
[<Function("StartTradeApproval")>]
let startTradeApproval
    ([<HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "trade/start")>]
     req: HttpRequestData,
     [<DurableClient>] client: DurableTaskClient,
     executionContext: FunctionContext) : Task<HttpResponseData> =
    task {
        let log = executionContext.GetLogger("StartTradeApproval")
        let! request = req.ReadFromJsonAsync<TradeRequest>()

        // Start a new orchestration instance
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

/// URL: "/api/trade/{instanceId}/approve"
/// An HTTP Trigger callable by a human approver to submit their decision.
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


/// URL: "/api/trade/{instanceId}/status"
/// An HTTP Trigger to check the status of the trade approval workflow.
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
