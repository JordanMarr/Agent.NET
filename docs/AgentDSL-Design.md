# AgentNet F# Library Spec

## Overview
An F# library wrapping Microsoft Agent Framework to provide a clean, idiomatic way to create AI agents.

## Design Philosophy
- **Tools** → Pipeline style (simple, functional)
- **Agents** → Pipeline style (simple, functional)
- **Workflows** → Computation expression (complex control flow)

## Current API

### Tool Definition (Pipeline Style)
```fsharp
let getStockInfo (symbol: string) : string =
    $"Price for {symbol}: $178.50"

let stockInfoTool =
    Tool.create getStockInfo
    |> Tool.withName "getStockInfo"
    |> Tool.describe "Gets current stock information"
    |> Tool.describeParam "symbol" "The stock ticker symbol"
```

### Agent Definition (Pipeline Style)
```fsharp
let stockAdvisor =
    Agent.create "You analyze stocks and provide investment advice."
    |> Agent.withName "StockAdvisor"
    |> Agent.withTool stockInfoTool
    |> Agent.withTools [historicalTool; volatilityTool]
    |> Agent.build chatClient

// Use the agent
let! response = stockAdvisor.Chat "Compare AAPL vs MSFT"
```

### Workflow Definition (CE Style)
```fsharp
// Sequential workflow
let researchWorkflow = workflow {
    start researcher
    next analyzer
    next writer
}

// Parallel fan-out/fan-in
let analysisWorkflow = workflow {
    start loadData
    fanOut [technicalAnalyst; fundamentalAnalyst; sentimentAnalyst]
    fanIn summarize
}

// Conditional routing
let routingWorkflow = workflow {
    start classifier
    route (function
        | HighConfidence _ -> fastPath
        | LowConfidence _ -> reviewPath
        | Inconclusive -> manualReview)
}

// Resilience
let resilientWorkflow = workflow {
    start unreliableStep
    retry 3
    timeout (TimeSpan.FromSeconds 30.)
    fallback fallbackStep
}

// Run workflows
let result = Workflow.runSync "input" researchWorkflow
```

### Result Workflow (Railway-Oriented)
```fsharp
let validationWorkflow = resultWorkflow {
    start (ResultExecutor.bind "Parse" parseDocument)
    next (ResultExecutor.bind "Validate" validateDocument)
    next (ResultExecutor.map "Save" saveDocument)
}

let result = ResultWorkflow.runSync input validationWorkflow
// Result<Output, Error> with short-circuit on Error
```

## Project Structure
```
src/
├── AgentNet/                    # F# library
│   ├── Tool.fs                  # Tool type + pipeline functions
│   ├── Agent.fs                 # AgentConfig, ChatAgent, Agent pipeline
│   ├── Workflow.fs              # Executor, workflow CE, Workflow module
│   ├── ResultWorkflow.fs        # ResultExecutor, resultWorkflow CE
│   └── AgentFramework.fs        # MAF integration (Agent.build)
├── AgentNet.Tests/              # NUnit tests
└── StockAdvisorFS/              # F# example
```

## Key Types

| Type | Description |
|------|-------------|
| `Tool` | Function with metadata (name, description, params) |
| `AgentConfig` | Agent configuration (name, instructions, tools) |
| `ChatAgent` | Built agent with `Chat: string -> Async<string>` |
| `Executor<'i,'o>` | Workflow step that transforms input to output |
| `WorkflowDef<'i,'o>` | Composable workflow definition |
| `ResultExecutor<'i,'o,'e>` | Executor returning `Result<'o,'e>` |
| `ResultWorkflowDef<'i,'o,'e>` | Workflow with error short-circuiting |

## Design Decisions
1. **Pipeline for tools and agents** - Simple configuration, functional style
2. **CE for workflows** - Complex control flow benefits from declarative syntax
3. **No attributes** - Tool metadata via pipeline functions
4. **Pure functions first** - Define F# functions, then wrap
5. **Type-safe workflows** - Input/output types threaded through builder
6. **Composition via `toExecutor`** - Workflows can be nested

## Dependencies
- Microsoft.Agents.AI
- Microsoft.Extensions.AI
