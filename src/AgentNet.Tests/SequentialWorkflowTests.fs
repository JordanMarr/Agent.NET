/// Tests for simple sequential workflows (step -> step -> step)
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
    let researcher (topic: Topic) =
        {
            Topic = topic
            Sources = ["Source A"; "Source B"]
            RawFindings = $"Findings about {topic.Name}"
        }
        |> Task.fromResult

    let analyzer (research: ResearchData) =
        {
            Research = research
            Insights = [$"Insight from {research.Sources.Length} sources"]
            Confidence = 0.85
        }
        |> Task.fromResult

    let writer (analysis: AnalysisResult) =
        {
            Analysis = analysis
            Title = $"Report: {analysis.Research.Topic.Name}"
            Summary = $"Based on {analysis.Insights.Length} insights with {analysis.Confidence} confidence"
        }
        |> Task.fromResult

    // Build the workflow using the DSL
    let myWorkflow = workflow {
        step researcher
        step analyzer
        step writer
    }

    // Act: Run the workflow
    let input = { Name = "F# agents"; Keywords = ["functional"; "async"] }
    let result = (myWorkflow |> Workflow.InProcess.run input).GetAwaiter().GetResult()

    // Assert: Verify the output shows correct sequencing
    result.Title =! "Report: F# agents"
    result.Analysis.Confidence =! 0.85
    result.Analysis.Research.Sources.Length =! 2
    result.Analysis.Research.Topic.Keywords =! ["functional"; "async"]

/// Integration test: validates agent CE -> Build -> TypedAgent.create -> workflow path
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
        ChatAgent.create "You are a researcher. Research the given topic."
        |> ChatAgent.build stubClient
        |> TypedAgent.create id (fun _ response -> response)

    let analyzer =
        ChatAgent.create "You are an analyzer. Analyze the given research."
        |> ChatAgent.build stubClient
        |> TypedAgent.create id (fun _ response -> response)

    let writer =
        ChatAgent.create "You are a writer. Write a report from the analysis."
        |> ChatAgent.build stubClient
        |> TypedAgent.create id (fun _ response -> response)

    // Build the workflow using the DSL
    let myWorkflow = workflow {
        step researcher
        step analyzer
        step writer
    }

    // Act: Run the workflow with input that matches first pattern
    let result = (myWorkflow |> Workflow.InProcess.run "Investigate: AI agents").GetAwaiter().GetResult()

    // Assert: Verify the stub was called and returned expected response
    result =! "FINAL_REPORT: Conclusion reached."

    // Verify all three agents were called
    stubClient.CallHistory.Length =! 3

[<Test>]
let ``Workflow output type is correctly inferred from last step``() =
    // Arrange: Each step transforms to a different type
    let parseInput (input: string) = { Name = input; Keywords = input.Split(' ') |> Array.toList } |> Task.fromResult
    let countKeywords (topic: Topic) = topic.Keywords.Length |> Task.fromResult
    let isSignificant (count: int) = count > 2 |> Task.fromResult

    let myWorkflow = workflow {
        step parseInput
        step countKeywords
        step isSignificant
    }

    // This compiles without explicit type annotation, proving type inference works
    // myWorkflow : WorkflowDef<string, bool>
    let result: bool = (myWorkflow |> Workflow.InProcess.run "functional reactive async programming").GetAwaiter().GetResult()

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
        step step1
        step step2
        step step3
    }

    // Act
    let initialTopic = { Name = "Test"; Keywords = ["k1"; "k2"] }
    let result = (myWorkflow |> Workflow.InProcess.run initialTopic).GetAwaiter().GetResult()

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
        step tokenize
        step analyze
    }

    // Act
    let result = (myWorkflow |> Workflow.InProcess.run { Text = "The quick brown fox jumps" }).GetAwaiter().GetResult()

    // Assert
    result.Count =! 5
    result.LongestToken =! "quick"  // or "brown" or "jumps" - all 5 chars, maxBy returns first

[<Test>]
let ``Sync functions work with Task_fromResult wrapper``() =
    // Arrange: Sync functions using |> Task.fromResult pattern
    let parse (s: string) = s.Length |> Task.fromResult
    let double (n: int) = n * 2 |> Task.fromResult
    let format (n: int) = $"Result: {n}" |> Task.fromResult

    let syncWorkflow = workflow {
        step parse
        step double
        step format
    }

    // Act
    let result = (syncWorkflow |> Workflow.InProcess.run "hello").GetAwaiter().GetResult()

    // Assert
    result =! "Result: 10"

[<Test>]
let ``Mixed Task_fromResult and async functions in workflow``() =
    // Arrange: Mix of Task.fromResult pattern and async functions
    let parseSync (s: string) = s.Split(' ') |> Array.toList |> Task.fromResult
    let fetchAsync (words: string list) = task { return words.Length }
    let formatSync (count: int) = $"Word count: {count}" |> Task.fromResult

    let mixedWorkflow = workflow {
        step parseSync     // Uses Task.fromResult - no operator
        step fetchAsync     // Async - no operator
        step formatSync     // Uses Task.fromResult - no operator
    }

    // Act
    let result = (mixedWorkflow |> Workflow.InProcess.run "one two three four five").GetAwaiter().GetResult()

    // Assert
    result =! "Word count: 5"

// ============ toMAF and InProcessExecution tests ============

[<Test>]
let ``Workflow_toMAF compiles sequential workflow to MAF format``() =
    // Arrange: Simple sequential workflow
    let step1 (s: string) = s.ToUpper() |> Task.fromResult
    let step2 (s: string) = s + "!" |> Task.fromResult

    let myWorkflow =
        workflow {
            step step1
            step step2
        }
        |> Workflow.withName "TestWorkflow"

    // Act: Compile to MAF
    let mafWorkflow = Workflow.toMAF myWorkflow

    // Assert: MAF workflow was created (basic check - no exceptions)
    test <@ not (isNull (box mafWorkflow)) @>

[<Test>]
let ``Workflow_runInProcess executes workflow via MAF InProcessExecution``() =
    // Arrange: Simple sequential workflow - using named executors for clarity
    let step1 = Executor.fromFn "Step1" (fun (s: string) -> s.ToUpper())
    let step2 = Executor.fromFn "Step2" (fun (s: string) -> s + "!!!")

    let myWorkflow =
        workflow {
            step step1
            step step2
        }
        |> Workflow.withName "InProcessTestWorkflow"

    // Act: Run via InProcessExecution
    let result = (myWorkflow |> Workflow.InProcess.run "hello").GetAwaiter().GetResult()

    // Assert
    result =! "HELLO!!!"

[<Test>]
let ``Workflow_runInProcess handles multiple steps correctly``() =
    // Arrange: Three-step workflow with type transformations - using named executors
    let parse = Executor.fromFn "Parse" (fun (s: string) -> s.Length)
    let double = Executor.fromFn "Double" (fun (n: int) -> n * 2)
    let format = Executor.fromFn "Format" (fun (n: int) -> $"Result: {n}")

    let myWorkflow =
        workflow {
            step parse
            step double
            step format
        }
        |> Workflow.withName "MultiStepWorkflow"

    // Act
    let result = (myWorkflow |> Workflow.InProcess.run "hello").GetAwaiter().GetResult()

    // Assert
    result =! "Result: 10"

[<Test>]
let ``Workflow_toMAF works without explicit name (auto-generates)``() =
    // Arrange: Workflow without an explicit name
    let step1 (s: string) = s.ToUpper() |> Task.fromResult

    let unnamedWorkflow = workflow {
        step step1
    }

    // Act: Compile to MAF - should not throw, auto-generates name
    let mafWorkflow = Workflow.toMAF unnamedWorkflow

    // Assert: MAF workflow was created
    test <@ not (isNull (box mafWorkflow)) @>

[<Test>]
let ``Workflow_runInProcess works without explicit name (auto-generates)``() =
    // Arrange: Workflow without an explicit name
    let step1 (s: string) = s.ToUpper() |> Task.fromResult

    let unnamedWorkflow = workflow {
        step step1
    }

    // Act: Run - should not throw, auto-generates name
    let result = (unnamedWorkflow |> Workflow.InProcess.run "test").GetAwaiter().GetResult()

    // Assert
    result =! "TEST"
