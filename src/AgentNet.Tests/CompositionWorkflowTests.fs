/// Tests for workflow composition using Workflow.InProcess.toExecutor
/// Based on the composition examples from docs/WorkflowDSL-Design.md
module AgentNet.Tests.CompositionWorkflowTests

open NUnit.Framework
open Swensen.Unquote
open AgentNet

// Domain types for composition tests
type Research = { Topic: string; Findings: string list }

type Analysis = { Research: Research; Conclusion: string; Confidence: float }

type Report = { Analysis: Analysis; Summary: string; Recommendations: string list }

type RawData = { Source: string; Values: int list }

type CleanedData = { Data: RawData; Cleaned: int list }

type Statistics = { Data: CleanedData; Mean: float; Max: int; Min: int }

[<Test>]
let ``Nested workflow executes as single step``() =
    // Arrange: Create an inner workflow for research
    let gatherFacts = Executor.fromFn "GatherFacts" (fun (topic: string) ->
        { Topic = topic; Findings = [$"Fact 1 about {topic}"; $"Fact 2 about {topic}"] })

    let expandFindings = Executor.fromFn "ExpandFindings" (fun (research: Research) ->
        { research with Findings = research.Findings @ ["Additional insight"] })

    let innerWorkflow = workflow {
        step gatherFacts
        step expandFindings
    }

    // Convert to executor
    let researchExecutor = Workflow.InProcess.toExecutor "ResearchWorkflow" innerWorkflow

    // Create outer workflow that uses the inner workflow as a step
    let analyze = Executor.fromFn "Analyze" (fun (research: Research) ->
        { Research = research; Conclusion = "Positive outlook"; Confidence = 0.85 })

    let outerWorkflow = workflow {
        step researchExecutor
        step analyze
    }

    // Act
    let result = (outerWorkflow |> Workflow.InProcess.run "AI agents").GetAwaiter().GetResult()

    // Assert
    result.Research.Topic =! "AI agents"
    result.Research.Findings.Length =! 3
    result.Conclusion =! "Positive outlook"
    result.Confidence =! 0.85

[<Test>]
let ``Data flows correctly through nested workflow``() =
    // Arrange: Track data transformation through nested workflow
    let mutable step1Input = ""
    let mutable step2Input = ""
    let mutable innerInput = ""
    let mutable innerOutput = ""

    let step1 = Executor.fromFn "Step1" (fun (input: string) ->
        step1Input <- input
        input + "-step1")

    let innerTransform = Executor.fromFn "InnerTransform" (fun (input: string) ->
        innerInput <- input
        let output = input.ToUpper()
        innerOutput <- output
        output)

    let innerWorkflow = workflow {
        step innerTransform
    }

    let innerAsStep = Workflow.InProcess.toExecutor "InnerWorkflow" innerWorkflow

    let step2 = Executor.fromFn "Step2" (fun (input: string) ->
        step2Input <- input
        input + "-step2")

    let composedWorkflow = workflow {
        step step1
        step innerAsStep
        step step2
    }

    // Act
    let result = (composedWorkflow |> Workflow.InProcess.run "hello").GetAwaiter().GetResult()

    // Assert: Verify data flow
    step1Input =! "hello"
    innerInput =! "hello-step1"
    innerOutput =! "HELLO-STEP1"
    step2Input =! "HELLO-STEP1"
    result =! "HELLO-STEP1-step2"

[<Test>]
let ``Multiple levels of nesting work correctly``() =
    // Arrange: Create a deeply nested workflow structure
    // Level 3 (innermost)
    let level3Step = Executor.fromFn "Level3" (fun (x: int) -> x * 2)
    let level3Workflow = workflow { step level3Step }
    let level3Executor = Workflow.InProcess.toExecutor "Level3Workflow" level3Workflow

    // Level 2 (wraps level 3)
    let level2Pre = Executor.fromFn "Level2Pre" (fun (x: int) -> x + 1)
    let level2Post = Executor.fromFn "Level2Post" (fun (x: int) -> x + 10)
    let level2Workflow = workflow {
        step level2Pre
        step level3Executor
        step level2Post
    }
    let level2Executor = Workflow.InProcess.toExecutor "Level2Workflow" level2Workflow

    // Level 1 (wraps level 2)
    let level1Pre = Executor.fromFn "Level1Pre" (fun (x: int) -> x * 3)
    let level1Post = Executor.fromFn "Level1Post" (fun (x: int) -> x - 5)
    let level1Workflow = workflow {
        step level1Pre
        step level2Executor
        step level1Post
    }

    // Act: Input 5
    // Level1Pre: 5 * 3 = 15
    // Level2Pre: 15 + 1 = 16
    // Level3: 16 * 2 = 32
    // Level2Post: 32 + 10 = 42
    // Level1Post: 42 - 5 = 37
    let result = (level1Workflow |> Workflow.InProcess.run 5).GetAwaiter().GetResult()

    // Assert
    result =! 37

[<Test>]
let ``Nested workflow with custom domain types``() =
    // Arrange: Data processing pipeline
    let loadData = Executor.fromFn "LoadData" (fun (source: string) ->
        { Source = source; Values = [1; 2; 3; 4; 5; -1; 100] })

    // Inner workflow: Data cleaning
    let removeNegatives = Executor.fromFn "RemoveNegatives" (fun (raw: RawData) ->
        { Data = raw; Cleaned = raw.Values |> List.filter (fun v -> v >= 0) })

    let removeOutliers = Executor.fromFn "RemoveOutliers" (fun (cleaned: CleanedData) ->
        { cleaned with Cleaned = cleaned.Cleaned |> List.filter (fun v -> v < 50) })

    let cleaningWorkflow = workflow {
        step removeNegatives
        step removeOutliers
    }
    let cleaningExecutor = Workflow.InProcess.toExecutor "CleaningWorkflow" cleaningWorkflow

    // Outer workflow includes cleaning as a step
    let computeStats = Executor.fromFn "ComputeStats" (fun (cleaned: CleanedData) ->
        let values = cleaned.Cleaned
        {
            Data = cleaned
            Mean = values |> List.averageBy float
            Max = values |> List.max
            Min = values |> List.min
        })

    let fullPipeline = workflow {
        step loadData
        step cleaningExecutor
        step computeStats
    }

    // Act
    let result = (fullPipeline |> Workflow.InProcess.run "sensor-data").GetAwaiter().GetResult()

    // Assert
    result.Data.Cleaned =! [1; 2; 3; 4; 5]  // -1 and 100 removed
    result.Mean =! 3.0
    result.Max =! 5
    result.Min =! 1

[<Test>]
let ``Nested workflow can be reused in multiple outer workflows``() =
    // Arrange: Create a reusable inner workflow
    let formatNumber = Executor.fromFn "FormatNumber" (fun (n: int) -> $"[{n}]")

    let formatWorkflow = workflow { step formatNumber }
    let formatExecutor = Workflow.InProcess.toExecutor "FormatWorkflow" formatWorkflow

    // First outer workflow: adds prefix
    let addPrefix = Executor.fromFn "AddPrefix" (fun (s: string) -> "PREFIX:" + s)
    let withPrefix = workflow {
        step formatExecutor
        step addPrefix
    }

    // Second outer workflow: adds suffix
    let addSuffix = Executor.fromFn "AddSuffix" (fun (s: string) -> s + ":SUFFIX")
    let withSuffix = workflow {
        step formatExecutor
        step addSuffix
    }

    // Third outer workflow: uppercase
    let toUpper = Executor.fromFn "ToUpper" (fun (s: string) -> s.ToUpper())
    let withUpper = workflow {
        step formatExecutor
        step toUpper
    }

    // Act
    let prefixResult = (withPrefix |> Workflow.InProcess.run 42).GetAwaiter().GetResult()
    let suffixResult = (withSuffix |> Workflow.InProcess.run 42).GetAwaiter().GetResult()
    let upperResult = (withUpper |> Workflow.InProcess.run 42).GetAwaiter().GetResult()

    // Assert
    prefixResult =! "PREFIX:[42]"
    suffixResult =! "[42]:SUFFIX"
    upperResult =! "[42]"  // Already uppercase

[<Test>]
let ``Nested workflow with parallel fanOut inside``() =
    // Arrange: Inner workflow with parallel processing
    let prepare = Executor.fromFn "Prepare" (fun (x: int) -> x)

    let branch1 = Executor.fromFn "Branch1" (fun (x: int) -> x + 100)
    let branch2 = Executor.fromFn "Branch2" (fun (x: int) -> x * 10)

    let combine = Executor.fromFn "Combine" (fun (results: int list) ->
        results |> List.sum)

    let parallelInnerWorkflow = workflow {
        step prepare
        fanOut branch1 branch2
        fanIn combine
    }
    let parallelExecutor = Workflow.InProcess.toExecutor "ParallelInnerWorkflow" parallelInnerWorkflow

    // Outer workflow
    let double = Executor.fromFn "Double" (fun (x: int) -> x * 2)
    let format = Executor.fromFn "Format" (fun (x: int) -> $"Result: {x}")

    let outerWorkflow = workflow {
        step double
        step parallelExecutor
        step format
    }

    // Act: Input 5
    // Double: 5 * 2 = 10
    // Inner parallel: [10 + 100, 10 * 10] = [110, 100]
    // Combine: 210
    // Format: "Result: 210"
    let result = (outerWorkflow |> Workflow.InProcess.run 5).GetAwaiter().GetResult()

    // Assert
    result =! "Result: 210"

[<Test>]
let ``Full report generation pipeline with composition``() =
    // Arrange: Research workflow
    let researchTopic = Executor.fromFn "ResearchTopic" (fun (topic: string) ->
        { Topic = topic; Findings = [$"Key finding about {topic}"; "Supporting data"] })

    let researchWorkflow = workflow { step researchTopic }
    let researchExecutor = Workflow.InProcess.toExecutor "ResearchWorkflow" researchWorkflow

    // Analysis workflow
    let analyzeResearch = Executor.fromFn "AnalyzeResearch" (fun (research: Research) ->
        {
            Research = research
            Conclusion = $"Based on {research.Findings.Length} findings, outlook is positive"
            Confidence = 0.9
        })

    let analysisWorkflow = workflow {
        step researchExecutor
        step analyzeResearch
    }
    let analysisExecutor = Workflow.InProcess.toExecutor "AnalysisWorkflow" analysisWorkflow

    // Report workflow (composes analysis workflow)
    let generateReport = Executor.fromFn "GenerateReport" (fun (analysis: Analysis) ->
        {
            Analysis = analysis
            Summary = $"Report on '{analysis.Research.Topic}': {analysis.Conclusion}"
            Recommendations = ["Continue monitoring"; "Invest cautiously"]
        })

    let reportWorkflow = workflow {
        step analysisExecutor
        step generateReport
    }

    // Act
    let result = (reportWorkflow |> Workflow.InProcess.run "Renewable Energy").GetAwaiter().GetResult()

    // Assert
    result.Analysis.Research.Topic =! "Renewable Energy"
    result.Analysis.Confidence =! 0.9
    result.Summary.Contains("Renewable Energy") =! true
    result.Recommendations.Length =! 2

[<Test>]
let ``Workflow can be passed directly without toExecutor``() =
    // Arrange: Inner workflow that doubles
    let inner = workflow {
        step (fun (x: int) -> x * 2 |> Task.fromResult)
    }

    // Outer workflow that uses inner directly (no Workflow.InProcess.toExecutor needed!)
    let outer = workflow {
        step (fun (x: int) -> x + 1 |> Task.fromResult)
        step inner
        step (fun (x: int) -> x.ToString() |> Task.fromResult)
    }

    // Act: 5 -> +1 -> 6 -> *2 -> 12 -> "12"
    let result = (outer |> Workflow.InProcess.run 5).GetAwaiter().GetResult()

    // Assert
    result =! "12"

[<Test>]
let ``Multiple nested workflows can be chained directly``() =
    // Arrange: Create two inner workflows
    let addTen = workflow {
        step (fun (x: int) -> x + 10 |> Task.fromResult)
    }

    let multiplyByThree = workflow {
        step (fun (x: int) -> x * 3 |> Task.fromResult)
    }

    // Chain them directly in outer workflow
    let outer = workflow {
        step (fun (x: int) -> x |> Task.fromResult)
        step addTen
        step multiplyByThree
    }

    // Act: 5 -> 5 -> +10 -> 15 -> *3 -> 45
    let result = (outer |> Workflow.InProcess.run 5).GetAwaiter().GetResult()

    // Assert
    result =! 45

[<Test>]
let ``Nested workflow with multiple steps works directly``() =
    // Arrange: Inner workflow with multiple steps
    let inner = workflow {
        step (fun (s: string) -> s.ToUpper() |> Task.fromResult)
        step (fun (s: string) -> s + "!" |> Task.fromResult)
    }

    let outer = workflow {
        step (fun (s: string) -> "Hello, " + s |> Task.fromResult)
        step inner
    }

    // Act
    let result = (outer |> Workflow.InProcess.run "world").GetAwaiter().GetResult()

    // Assert: "Hello, world" -> "HELLO, WORLD" -> "HELLO, WORLD!"
    result =! "HELLO, WORLD!"
