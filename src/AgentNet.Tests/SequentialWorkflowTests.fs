/// Tests for simple sequential workflows (start -> next -> next)
/// Based on the first example from docs/WorkflowDSL-Design.md
module AgentNet.Tests.SequentialWorkflowTests

open NUnit.Framework
open Swensen.Unquote
open AgentNet
open AgentNet.Tests.Stubs

// Domain types for sequential workflow tests
type Topic = { Name: string; Keywords: string list }
type ResearchData = { Topic: Topic; Sources: string list; RawFindings: string }
type AnalysisResult = { Research: ResearchData; Insights: string list; Confidence: float }
type FinalReport = { Analysis: AnalysisResult; Title: string; Summary: string }

[<Test>]
let ``Simple sequential workflow executes in order``() =
    // Arrange: Create executors with custom domain types
    let researcher = Executor.fromFn "Researcher" (fun (topic: Topic) ->
        {
            Topic = topic
            Sources = ["Source A"; "Source B"]
            RawFindings = $"Findings about {topic.Name}"
        })

    let analyzer = Executor.fromFn "Analyzer" (fun (research: ResearchData) ->
        {
            Research = research
            Insights = [$"Insight from {research.Sources.Length} sources"]
            Confidence = 0.85
        })

    let writer = Executor.fromFn "Writer" (fun (analysis: AnalysisResult) ->
        {
            Analysis = analysis
            Title = $"Report: {analysis.Research.Topic.Name}"
            Summary = $"Based on {analysis.Insights.Length} insights with {analysis.Confidence} confidence"
        })

    // Build the workflow using the DSL
    let myWorkflow = workflow {
        start researcher
        next analyzer
        next writer
    }

    // Act: Run the workflow
    let input = { Name = "F# agents"; Keywords = ["functional"; "async"] }
    let result = Workflow.runSync input myWorkflow

    // Assert: Verify the output shows correct sequencing
    result.Title =! "Report: F# agents"
    result.Analysis.Confidence =! 0.85
    result.Analysis.Research.Sources.Length =! 2
    result.Analysis.Research.Topic.Keywords =! ["functional"; "async"]

/// Integration test: validates agent CE -> Build -> Executor.fromAgent -> workflow path
[<Test>]
let ``Agent executors integrate with workflow DSL``() =
    // Arrange: Create a stub chat client with distinct, non-overlapping patterns
    let stubClient = new StubChatClient()
    // Each response contains a unique marker that won't match other patterns
    stubClient.SetResponse("Investigate:", "RESEARCH_RESULT: Found interesting data.")
    stubClient.SetResponse("RESEARCH_RESULT:", "ANALYSIS_RESULT: Data is significant.")
    stubClient.SetResponse("ANALYSIS_RESULT:", "FINAL_REPORT: Conclusion reached.")

    // Create agent executors using the stub client
    let researcher = 
        Agent.create "You are a researcher. Research the given topic."
        |> Agent.build stubClient
        |> Executor.fromAgent "Researcher"

    let analyzer = 
        Agent.create "You are an analyzer. Analyze the given research."
        |> Agent.build stubClient
        |> Executor.fromAgent "Analyzer"

    let writer = 
        Agent.create "You are a writer. Write a report from the analysis."
        |> Agent.build stubClient
        |> Executor.fromAgent "Writer"

    // Build the workflow using the DSL
    let myWorkflow = workflow {
        start researcher
        next analyzer
        next writer
    }

    // Act: Run the workflow with input that matches first pattern
    let result = Workflow.runSync "Investigate: AI agents" myWorkflow

    // Assert: Verify the stub was called and returned expected response
    result =! "FINAL_REPORT: Conclusion reached."

    // Verify all three agents were called
    stubClient.CallHistory.Length =! 3

[<Test>]
let ``Workflow output type is correctly inferred from last step``() =
    // Arrange: Each step transforms to a different type
    let parseInput = Executor.fromFn "ParseInput" (fun (input: string) ->
        { Name = input; Keywords = input.Split(' ') |> Array.toList })

    let countKeywords = Executor.fromFn "CountKeywords" (fun (topic: Topic) ->
        topic.Keywords.Length)

    let isSignificant = Executor.fromFn "IsSignificant" (fun (count: int) ->
        count > 2)

    let myWorkflow = workflow {
        start parseInput
        next countKeywords
        next isSignificant
    }

    // This compiles without explicit type annotation, proving type inference works
    // myWorkflow : WorkflowDef<string, bool>
    let result: bool = Workflow.runSync "functional reactive async programming" myWorkflow

    result =! true  // 4 keywords > 2

[<Test>]
let ``Each step receives output from previous step``() =
    // Arrange: Create executors that record their inputs
    let mutable step1Input: Topic option = None
    let mutable step2Input: ResearchData option = None
    let mutable step3Input: AnalysisResult option = None

    let step1 = Executor.fromFn "Step1" (fun (input: Topic) ->
        step1Input <- Some input
        { Topic = input; Sources = ["S1"]; RawFindings = "raw" })

    let step2 = Executor.fromFn "Step2" (fun (input: ResearchData) ->
        step2Input <- Some input
        { Research = input; Insights = ["I1"]; Confidence = 0.9 })

    let step3 = Executor.fromFn "Step3" (fun (input: AnalysisResult) ->
        step3Input <- Some input
        { Analysis = input; Title = "T"; Summary = "S" })

    let myWorkflow = workflow {
        start step1
        next step2
        next step3
    }

    // Act
    let initialTopic = { Name = "Test"; Keywords = ["k1"; "k2"] }
    let result = Workflow.runSync initialTopic myWorkflow

    // Assert: Verify data flow through custom types
    step1Input.Value.Name =! "Test"
    step2Input.Value.Topic.Name =! "Test"
    step2Input.Value.Sources =! ["S1"]
    step3Input.Value.Research.Topic.Name =! "Test"
    step3Input.Value.Confidence =! 0.9
    result.Title =! "T"
    result.Analysis.Insights =! ["I1"]


// Additional domain types for more complex transformations
type TextInput = { Text: string }
type TokenizedText = { Tokens: string list; OriginalLength: int }
type TokenStats = { Count: int; AverageLength: float; LongestToken: string }

[<Test>]
let ``Workflow with type transformations through pipeline``() =
    // Arrange: Pipeline that transforms through multiple types
    let tokenize = Executor.fromFn "Tokenize" (fun (input: TextInput) ->
        let tokens = input.Text.Split(' ') |> Array.toList
        { Tokens = tokens; OriginalLength = input.Text.Length })

    let analyze = Executor.fromFn "Analyze" (fun (tokenized: TokenizedText) ->
        let avgLen = tokenized.Tokens |> List.averageBy (fun t -> float t.Length)
        let longest = tokenized.Tokens |> List.maxBy (fun t -> t.Length)
        { Count = tokenized.Tokens.Length; AverageLength = avgLen; LongestToken = longest })

    let myWorkflow = workflow {
        start tokenize
        next analyze
    }

    // Act
    let result = Workflow.runSync { Text = "The quick brown fox jumps" } myWorkflow

    // Assert
    result.Count =! 5
    result.LongestToken =! "quick"  // or "brown" or "jumps" - all 5 chars, maxBy returns first
