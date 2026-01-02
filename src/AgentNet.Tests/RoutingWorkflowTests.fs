/// Tests for conditional routing workflows using pattern matching
/// Based on the routing examples from docs/WorkflowDSL-Design.md
module AgentNet.Tests.RoutingWorkflowTests

open NUnit.Framework
open Swensen.Unquote
open AgentNet

// Domain types for routing tests
type AnalysisResult =
    | HighConfidence of score: float
    | LowConfidence of score: float
    | Inconclusive

type Category =
    | Tech
    | Finance
    | General

type Article = { Title: string; Category: Category }
type CategorizedArticle = { Article: Article; Handler: string }

[<Test>]
let ``Route selects correct executor based on DU case``() =
    // Arrange: Create an analyzer that returns different results
    let analyze = Executor.fromFn "Analyze" (fun (input: string) ->
        if input.Contains("confident") then HighConfidence 0.9
        elif input.Contains("unsure") then LowConfidence 0.4
        else Inconclusive)

    let highHandler = Executor.fromFn "HighHandler" (fun (_: AnalysisResult) ->
        "Processed with high confidence")
    let lowHandler = Executor.fromFn "LowHandler" (fun (_: AnalysisResult) ->
        "Processed with low confidence")
    let inconclusiveHandler = Executor.fromFn "InconclusiveHandler" (fun (_: AnalysisResult) ->
        "Needs manual review")

    let routingWorkflow = workflow {
        start analyze
        route (function
            | HighConfidence _ -> highHandler
            | LowConfidence _ -> lowHandler
            | Inconclusive -> inconclusiveHandler)
    }

    // Act & Assert: Test each branch
    Workflow.runSync "I am confident about this" routingWorkflow =! "Processed with high confidence"
    Workflow.runSync "I am unsure about this" routingWorkflow =! "Processed with low confidence"
    Workflow.runSync "No idea" routingWorkflow =! "Needs manual review"

[<Test>]
let ``Route can extract data from DU cases``() =
    // Arrange
    let analyze = Executor.fromFn "Analyze" (fun (value: int) ->
        if value >= 80 then HighConfidence (float value / 100.0)
        elif value >= 50 then LowConfidence (float value / 100.0)
        else Inconclusive)

    let formatHigh = Executor.fromFn "FormatHigh" (function
        | HighConfidence score -> $"High: {score:F2}"
        | _ -> "unexpected")
    let formatLow = Executor.fromFn "FormatLow" (function
        | LowConfidence score -> $"Low: {score:F2}"
        | _ -> "unexpected")
    let formatInconclusive = Executor.fromFn "FormatInconclusive" (fun _ ->
        "Inconclusive: N/A")

    let routingWorkflow = workflow {
        start analyze
        route (function
            | HighConfidence _ as result -> formatHigh
            | LowConfidence _ as result -> formatLow
            | Inconclusive -> formatInconclusive)
    }

    // Act & Assert
    Workflow.runSync 95 routingWorkflow =! "High: 0.95"
    Workflow.runSync 65 routingWorkflow =! "Low: 0.65"
    Workflow.runSync 30 routingWorkflow =! "Inconclusive: N/A"

[<Test>]
let ``Route with category-based content handling``() =
    // Arrange
    let categorize = Executor.fromFn "Categorize" (fun (title: string) ->
        if title.Contains("AI") || title.Contains("Code") then
            { Title = title; Category = Tech }
        elif title.Contains("Stock") || title.Contains("Market") then
            { Title = title; Category = Finance }
        else
            { Title = title; Category = General })

    let techWriter = Executor.fromFn "TechWriter" (fun (article: Article) -> { Article = article; Handler = "TechDesk" })
    let financeWriter = Executor.fromFn "FinanceWriter" (fun (article: Article) -> { Article = article; Handler = "FinanceDesk" })
    let generalWriter = Executor.fromFn "GeneralWriter" (fun (article: Article) -> { Article = article; Handler = "GeneralDesk" })

    let routingWorkflow = workflow {
        start categorize
        route (fun article ->
            match article.Category with
            | Tech -> techWriter
            | Finance -> financeWriter
            | General -> generalWriter)
    }

    // Act & Assert
    let techResult = Workflow.runSync "New AI breakthrough" routingWorkflow
    techResult.Handler =! "TechDesk"

    let financeResult = Workflow.runSync "Stock Market update" routingWorkflow
    financeResult.Handler =! "FinanceDesk"

    let generalResult = Workflow.runSync "Weather forecast" routingWorkflow
    generalResult.Handler =! "GeneralDesk"

[<Test>]
let ``Route followed by additional steps``() =
    // Arrange
    let classify = Executor.fromFn "Classify" (fun (n: int) ->
        if n > 0 then HighConfidence (float n)
        else LowConfidence (float n))

    let processPositive = Executor.fromFn "ProcessPositive" (fun (_: AnalysisResult) -> "positive")
    let processNegative = Executor.fromFn "ProcessNegative" (fun (_: AnalysisResult) -> "negative")

    let format = Executor.fromFn "Format" (fun (label: string) ->
        $"Result: {label.ToUpper()}")

    let routingWorkflow = workflow {
        start classify
        route (function
            | HighConfidence _ -> processPositive
            | LowConfidence _ | Inconclusive -> processNegative)
        next format
    }

    // Act & Assert
    Workflow.runSync 5 routingWorkflow =! "Result: POSITIVE"
    Workflow.runSync -3 routingWorkflow =! "Result: NEGATIVE"
