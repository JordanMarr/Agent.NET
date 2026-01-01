/// Tests for simple sequential workflows (start -> next -> next)
/// Based on the first example from docs/WorkflowDSL-Design.md
module AgentNet.Tests.SequentialWorkflowTests

open NUnit.Framework
open FsUnit
open AgentNet
open AgentNet.Tests.Stubs

/// Test A: Pure function test using Executor.fromFn
/// This validates the workflow DSL syntax without needing an LLM stub.
[<Test>]
let ``Simple sequential workflow executes in order``() =
    // Arrange: Create executors from simple functions
    let researcher = Executor.fromFn "Researcher" (fun (topic: string) ->
        $"Research on {topic}")

    let analyzer = Executor.fromFn "Analyzer" (fun (research: string) ->
        $"Analysis: {research}")

    let writer = Executor.fromFn "Writer" (fun (analysis: string) ->
        $"Report: {analysis}")

    // Build the workflow using the DSL
    let myWorkflow = workflow {
        start researcher
        next analyzer
        next writer
    }

    // Act: Run the workflow
    let result = Workflow.runSync "F# agents" myWorkflow

    // Assert: Verify the output shows correct sequencing
    result |> should equal "Report: Analysis: Research on F# agents"

/// Integration test: validates agent CE -> Build -> Executor.fromAgent -> workflow path
[<Test>]
let ``Agent executors integrate with workflow DSL``() =
    // Arrange: Create a stub chat client with distinct, non-overlapping patterns
    let stubClient = new StubChatClient()
    // Each response contains a unique marker that won't match other patterns
    stubClient.SetResponse("Investigate:", "RESEARCH_RESULT: Found interesting data.")
    stubClient.SetResponse("RESEARCH_RESULT:", "ANALYSIS_RESULT: Data is significant.")
    stubClient.SetResponse("ANALYSIS_RESULT:", "FINAL_REPORT: Conclusion reached.")

    // Create agents using the stub client with new syntax
    let researcherAgent = agent stubClient {
        instructions "You are a researcher. Research the given topic."
    }

    let analyzerAgent = agent stubClient {
        instructions "You are an analyzer. Analyze the given research."
    }

    let writerAgent = agent stubClient {
        instructions "You are a writer. Write a report from the analysis."
    }

    // Create executors from agents
    let researcher = Executor.fromAgent "Researcher" researcherAgent
    let analyzer = Executor.fromAgent "Analyzer" analyzerAgent
    let writer = Executor.fromAgent "Writer" writerAgent

    // Build the workflow using the DSL
    let myWorkflow = workflow {
        start researcher
        next analyzer
        next writer
    }

    // Act: Run the workflow with input that matches first pattern
    let result = Workflow.runSync "Investigate: AI agents" myWorkflow

    // Assert: Verify the stub was called and returned expected response
    result |> should equal "FINAL_REPORT: Conclusion reached."

    // Verify all three agents were called
    stubClient.CallHistory.Length |> should equal 3

/// Verify that the workflow type is correctly inferred as WorkflowDef<string, string>
[<Test>]
let ``Workflow output type is correctly inferred from last step``() =
    let step1 = Executor.fromFn "Step1" (fun (input: string) -> input.Length)
    let step2 = Executor.fromFn "Step2" (fun (len: int) -> len > 5)

    let myWorkflow = workflow {
        start step1
        next step2
    }

    // This compiles without explicit type annotation, proving type inference works
    // myWorkflow : WorkflowDef<string, bool>
    let result: bool = Workflow.runSync "hello world" myWorkflow

    result |> should equal true

/// Additional test: Verify that output from each step flows correctly to the next
[<Test>]
let ``Each step receives output from previous step``() =
    // Arrange: Create executors that record their inputs
    let mutable step1Input = ""
    let mutable step2Input = ""
    let mutable step3Input = ""

    let step1 = Executor.fromFn "Step1" (fun (input: string) ->
        step1Input <- input
        "output1")

    let step2 = Executor.fromFn "Step2" (fun (input: string) ->
        step2Input <- input
        "output2")

    let step3 = Executor.fromFn "Step3" (fun (input: string) ->
        step3Input <- input
        "output3")

    let myWorkflow = workflow {
        start step1
        next step2
        next step3
    }

    // Act
    let result = Workflow.runSync "initial" myWorkflow

    // Assert: Verify data flow
    step1Input |> should equal "initial"
    step2Input |> should equal "output1"
    step3Input |> should equal "output2"
    result |> should equal "output3"
