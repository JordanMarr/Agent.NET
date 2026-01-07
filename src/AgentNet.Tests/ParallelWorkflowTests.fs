/// Tests for parallel fan-out/fan-in workflows
/// Based on the parallel examples from docs/WorkflowDSL-Design.md
module AgentNet.Tests.ParallelWorkflowTests

open NUnit.Framework
open Swensen.Unquote
open AgentNet

// Domain types for parallel tests
type StockData = { Symbol: string; Price: float }

type AnalystReport = { Analyst: string; Rating: string; Score: int }

type Summary = { Reports: AnalystReport list; Consensus: string; AverageScore: float }

type DataPacket = { Id: int; Value: string }

[<Test>]
let ``FanOut executes all executors and FanIn aggregates results``() =
    // Arrange: All functions use Task.fromResult pattern
    let loadData (symbol: string) = { Symbol = symbol; Price = 150.0 } |> Task.fromResult
    let technicalAnalyst (data: StockData) = { Analyst = "Technical"; Rating = "Buy"; Score = 8 } |> Task.fromResult
    let fundamentalAnalyst (data: StockData) = { Analyst = "Fundamental"; Rating = "Hold"; Score = 6 } |> Task.fromResult
    let sentimentAnalyst (data: StockData) = { Analyst = "Sentiment"; Rating = "Buy"; Score = 7 } |> Task.fromResult

    let summarize (reports: AnalystReport list) =
        let avgScore = reports |> List.averageBy (fun r -> float r.Score)
        let consensus = if avgScore >= 7.0 then "Buy" else "Hold"
        { Reports = reports; Consensus = consensus; AverageScore = avgScore } |> Task.fromResult

    let parallelWorkflow = workflow {
        step "LoadData" loadData
        fanOut technicalAnalyst fundamentalAnalyst sentimentAnalyst
        fanIn summarize
    }

    // Act
    let result = Workflow.runSync "AAPL" parallelWorkflow

    // Assert
    result.Reports.Length =! 3
    result.Reports |> List.exists (fun r -> r.Analyst = "Technical") =! true
    result.Reports |> List.exists (fun r -> r.Analyst = "Fundamental") =! true
    result.Reports |> List.exists (fun r -> r.Analyst = "Sentiment") =! true
    result.Consensus =! "Buy"
    result.AverageScore =! 7.0

[<Test>]
let ``FanOut with two executors``() =
    // Arrange
    let prepare = Executor.fromFn "Prepare" (fun (x: int) -> x * 2)
    let addTen = Executor.fromFn "AddTen" (fun (x: int) -> x + 10)
    let multiplyThree = Executor.fromFn "MultiplyThree" (fun (x: int) -> x * 3)
    let combine = Executor.fromFn "Combine" (fun (results: int list) -> results |> List.sum)

    let parallelWorkflow = workflow {
        step prepare
        fanOut addTen multiplyThree
        fanIn combine
    }

    // Act: input 5 -> prepare: 10 -> fanOut: [20, 30] -> combine: 50
    let result = Workflow.runSync 5 parallelWorkflow

    // Assert
    result =! 50

[<Test>]
let ``FanOut preserves order of results``() =
    // Arrange: Create executors that tag their output with index
    let identity = Executor.fromFn "Identity" id

    let tag0 = Executor.fromFn "Tag0" (fun (s: string) -> $"0:{s}")
    let tag1 = Executor.fromFn "Tag1" (fun (s: string) -> $"1:{s}")
    let tag2 = Executor.fromFn "Tag2" (fun (s: string) -> $"2:{s}")

    let join = Executor.fromFn "Join" (fun (results: string list) ->
        String.concat "," results)

    let parallelWorkflow = workflow {
        step identity
        fanOut tag0 tag1 tag2
        fanIn join
    }

    // Act
    let result = Workflow.runSync "X" parallelWorkflow

    // Assert: Results should be in executor order
    result =! "0:X,1:X,2:X"

[<Test>]
let ``FanOut followed by additional processing``() =
    // Arrange
    let init = Executor.fromFn "Init" (fun (n: int) -> n)

    let double = Executor.fromFn "Double" (fun (n: int) -> n * 2)
    let triple = Executor.fromFn "Triple" (fun (n: int) -> n * 3)

    let sum = Executor.fromFn "Sum" (fun (nums: int list) -> List.sum nums)

    let format = Executor.fromFn "Format" (fun (total: int) -> $"Total: {total}")

    let parallelWorkflow = workflow {
        step init
        fanOut double triple
        fanIn sum
        step format
    }

    // Act: 10 -> fanOut: [20, 30] -> sum: 50 -> format: "Total: 50"
    let result = Workflow.runSync 10 parallelWorkflow

    // Assert
    result =! "Total: 50"

[<Test>]
let ``FanOut with custom record types``() =
    // Arrange
    let createPackets = Executor.fromFn "CreatePackets" (fun (prefix: string) ->
        { Id = 1; Value = prefix })

    let processA = Executor.fromFn "ProcessA" (fun (p: DataPacket) ->
        { p with Value = p.Value + "-A" })
    let processB = Executor.fromFn "ProcessB" (fun (p: DataPacket) ->
        { p with Value = p.Value + "-B" })

    let merge = Executor.fromFn "Merge" (fun (packets: DataPacket list) ->
        packets |> List.map (fun p -> p.Value) |> String.concat "|")

    let parallelWorkflow = workflow {
        step createPackets
        fanOut processA processB
        fanIn merge
    }

    // Act
    let result = Workflow.runSync "DATA" parallelWorkflow

    // Assert
    result =! "DATA-A|DATA-B"

[<Test>]
let ``FanOut with list syntax and + operator for 6+ branches``() =
    // Arrange: 6 branches requires list syntax with step/+ operator
    let init = Executor.fromFn "Init" (fun (x: int) -> x)

    let add1 = Executor.fromFn "Add1" (fun (x: int) -> x + 1)
    let add2 = Executor.fromFn "Add2" (fun (x: int) -> x + 2)
    let add3 = Executor.fromFn "Add3" (fun (x: int) -> x + 3)
    let add4 = Executor.fromFn "Add4" (fun (x: int) -> x + 4)
    let add5 = Executor.fromFn "Add5" (fun (x: int) -> x + 5)
    let add6 = Executor.fromFn "Add6" (fun (x: int) -> x + 6)

    let sum = Executor.fromFn "Sum" (fun (nums: int list) -> List.sum nums)

    let parallelWorkflow = workflow {
        step init
        fanOut [+add1; +add2; +add3; +add4; +add5; +add6]
        fanIn sum
    }

    // Act: 10 -> fanOut: [11, 12, 13, 14, 15, 16] -> sum: 81
    let result = Workflow.runSync 10 parallelWorkflow

    // Assert
    result =! 81

[<Test>]
let ``FanOut with list syntax, Task.fromResult and + operator for 6+ branches``() =
    // Arrange: 6 branches requires list syntax with step/+ operator
    let init = Executor.fromFn "Init" (fun (x: int) -> x)

    let add1 (x: int) = x + 1 |> Task.fromResult
    let add2 (x: int) = x + 2 |> Task.fromResult
    let add3 (x: int) = x + 3 |> Task.fromResult
    let add4 (x: int) = x + 4 |> Task.fromResult
    let add5 (x: int) = x + 5 |> Task.fromResult
    let add6 (x: int) = x + 6 |> Task.fromResult
    let sum (nums: int list) = List.sum nums |> Task.fromResult

    let parallelWorkflow = workflow {
        step init
        fanOut [+add1; +add2; +add3; +add4; +add5; +add6]
        fanIn sum
    }

    // Act: 10 -> fanOut: [11, 12, 13, 14, 15, 16] -> sum: 81
    let result = Workflow.runSync 10 parallelWorkflow

    // Assert
    result =! 81

