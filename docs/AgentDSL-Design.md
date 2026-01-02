# AgentNet F# Library Spec

## Overview
An F# library wrapping Microsoft Agent Framework to provide a clean, idiomatic way to create AI agents.

## Design Philosophy
- **Tools** → Quotation-based (automatic metadata extraction)
- **Agents** → Pipeline style (simple, functional)
- **Workflows** → Computation expression (complex control flow)

## Current API

### Tool Definition (Quotation-Based)
```fsharp
/// Gets current stock information including price and metrics
let getStockInfo (symbol: string) : string =
    $"Price for {symbol}: $178.50"

// Option 1: Auto-extract description from XML docs (recommended)
let stockInfoTool = Tool.createWithDocs <@ getStockInfo @>

// Option 2: Manual description
let stockInfoTool =
    Tool.create <@ getStockInfo @>
    |> Tool.describe "Gets current stock information"
```

The quotation `<@ getStockInfo @>` automatically extracts:
- Function name → Tool name
- Parameter names and types → Tool parameters (via MethodInfo)
- XML doc summary → Tool description (with `createWithDocs`)

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
| `ToolDef` | Tool definition with name, description, and MethodInfo |
| `AgentConfig` | Agent configuration (name, instructions, tools) |
| `ChatAgent` | Built agent with `Chat: string -> Async<string>` |
| `Executor<'i,'o>` | Workflow step that transforms input to output |
| `WorkflowDef<'i,'o>` | Composable workflow definition |
| `ResultExecutor<'i,'o,'e>` | Executor returning `Result<'o,'e>` |
| `ResultWorkflowDef<'i,'o,'e>` | Workflow with error short-circuiting |

## Design Decisions
1. **Quotations for tools** - Automatic name/param extraction, no sync issues
2. **XML docs for descriptions** - `createWithDocs` extracts from `///` comments
3. **Pipeline for agents** - Simple configuration, functional style
4. **CE for workflows** - Complex control flow benefits from declarative syntax
5. **No attributes** - Metadata extracted from quotations and XML docs
6. **Pure functions first** - Define F# functions, then wrap with quotation
7. **Type-safe workflows** - Input/output types threaded through builder
8. **Composition via `toExecutor`** - Workflows can be nested

## Dependencies
- Microsoft.Agents.AI
- Microsoft.Extensions.AI
