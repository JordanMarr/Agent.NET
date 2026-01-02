# Agent.NET

**A beautiful F# library for building AI agents on .NET**

[![NuGet](https://img.shields.io/nuget/v/Agent.NET.svg)](https://www.nuget.org/packages/Agent.NET)
[![License](https://img.shields.io/github/license/JordanMarr/Agent.NET)](LICENSE)

---

## The Pitch

What if building AI agents looked like this?

```fsharp
/// Gets current stock information
let getStockInfo (symbol: string) : string =
    StockService.GetQuote(symbol)  // Your existing C# service works here

let tool = Tool.createWithDocs <@ getStockInfo @>
```

That's it. Add a small F# project to your solution, wrap your existing services, and you have AI-ready tools. The function name becomes the tool name. The XML docs become the description. The parameter names and types are extracted automatically. No attributes. No magic strings. No sync issues.

And when you need to orchestrate multiple agents?

```fsharp
let analysisWorkflow = workflow {
    start loadData
    fanOut [technicalAnalyst; fundamentalAnalyst; sentimentAnalyst]
    fanIn summarize
    retry 3
    timeout (TimeSpan.FromMinutes 5.0)
}
```

**Agent.NET** wraps the [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) with a clean, idiomatic F# API that makes building AI agents a joy.

---

## Installation

```bash
dotnet add package Agent.NET
```

---

## Quick Start

### 1. Define Your Tools

Write normal F# functions with XML documentation (summary only or summary and params):

```fsharp
open Agent.NET

/// Gets the current weather for a city
let getWeather (city: string) : string =
    let weather = WeatherApi.fetch city
    $"The weather in {city} is {weather}"

/// <summary>Gets the current time in a timezone</summary>
/// <param name="timezone">The timezone (e.g., America/New_York)</param>
let getTime (timezone: string) : string =
    let time = TimeApi.now timezone
    $"The current time is {time}"

// Create tools - metadata extracted automatically!
let weatherTool = Tool.createWithDocs <@ getWeather @>
let timeTool = Tool.createWithDocs <@ getTime @>
```

### 2. Create an Agent

```fsharp
let assistant =
    Agent.create "You are a helpful assistant that provides weather and time information."
    |> Agent.withName "WeatherBot"
    |> Agent.withTools [weatherTool; timeTool]
    |> Agent.build chatClient

// Chat with your agent
let! response = assistant.Chat("What's the weather like in Seattle?")
```

### 3. Orchestrate with Workflows

```fsharp
let researchWorkflow = workflow {
    start researcher
    next analyst
    next writer
}

let result = Workflow.runSync "Research AI trends" researchWorkflow
```

---

## Features

### Tools: Quotation-Based Metadata Extraction

The `<@ @>` quotation syntax captures your function and extracts all metadata automatically:

```fsharp
/// <summary>Searches the knowledge base</summary>
/// <param name="query">The search query</param>
/// <param name="maxResults">Maximum number of results to return</param>
let searchKnowledge (query: string) (maxResults: int) : string =
    KnowledgeBase.search query maxResults

let searchTool = Tool.createWithDocs <@ searchKnowledge @>
// Extracts:
//   Name: "searchKnowledge"
//   Description: "Searches the knowledge base"
//   Parameters: [{Name="query"; Description="The search query"; Type=string}
//                {Name="maxResults"; Description="Maximum number of results"; Type=int}]
```

**Why quotations?**
- Function name becomes tool name (rename the function, tool updates automatically)
- Parameter names preserved (no "arg0", "arg1")
- XML docs become descriptions (documentation lives with the code)
- Type information retained for schema generation

If you prefer manual descriptions:

```fsharp
let searchTool =
    Tool.create <@ searchKnowledge @>
    |> Tool.describe "Searches the knowledge base for relevant documents"
```

### Agents: Pipeline-Style Configuration

Build agents using a clean pipeline:

```fsharp
let stockAdvisor =
    Agent.create """
        You are a stock market analyst. You help users understand
        stock performance, analyze trends, and compare investments.
        Always provide balanced, factual analysis.
        """
    |> Agent.withName "StockAdvisor"
    |> Agent.withTool getStockInfoTool
    |> Agent.withTool getHistoricalPricesTool
    |> Agent.withTool calculateVolatilityTool
    |> Agent.withTools [compareTool; analysisTool]  // Add multiple at once
    |> Agent.build chatClient
```

Use your agent:

```fsharp
// Async chat
let! response = stockAdvisor.Chat("Compare AAPL and MSFT performance")

// Access the underlying config if needed
printfn $"Agent: {stockAdvisor.Config.Name}"
```

### Workflows: Computation Expression for Orchestration

The `workflow` CE is where Agent.NET really shines. Orchestrate complex multi-agent scenarios with elegant, readable syntax.

#### Sequential Pipelines

```fsharp
let reportWorkflow = workflow {
    start researcher      // Gather information
    next analyst          // Analyze findings
    next writer           // Write the report
    next editor           // Polish and refine
}
```

#### Parallel Fan-Out / Fan-In

Process data through multiple agents in parallel, then combine results:

```fsharp
let analysisWorkflow = workflow {
    start dataLoader
    fanOut [
        technicalAnalyst      // Chart patterns, indicators
        fundamentalAnalyst    // Financials, ratios
        sentimentAnalyst      // News, social media
    ]
    fanIn synthesizer         // Combine all perspectives
}
```

#### Conditional Routing

Route to different agents based on content:

```fsharp
type Priority =
    | Urgent of string
    | Normal of string
    | LowPriority of string

let triageWorkflow = workflow {
    start classifier
    route (function
        | Urgent msg -> urgentHandler
        | Normal msg -> standardHandler
        | LowPriority msg -> batchHandler)
}
```

#### Resilience: Retry, Timeout, Fallback

Build fault-tolerant workflows:

```fsharp
let resilientWorkflow = workflow {
    start primaryAgent
    retry 3                              // Retry up to 3 times
    timeout (TimeSpan.FromSeconds 30.0)  // Timeout after 30s
    fallback backupAgent                 // Use backup if all else fails
}
```

Combine resilience with other operations:

```fsharp
let robustAnalysis = workflow {
    start loadData
    fanOut [analyst1; analyst2; analyst3]
    retry 2
    fanIn combiner
    timeout (TimeSpan.FromMinutes 5.0)
    fallback cachedResults
}
```

#### Composition: Nest Workflows

Workflows are composable - nest them freely:

```fsharp
let innerWorkflow = workflow {
    start stepA
    next stepB
}

let outerWorkflow = workflow {
    start preprocess
    next (innerWorkflow |> Workflow.toExecutor)  // Nest the inner workflow
    next postprocess
}
```

#### Running Workflows

```fsharp
// Synchronous
let result = Workflow.runSync "initial input" myWorkflow

// Asynchronous
let! result = Workflow.run "initial input" myWorkflow
```

<details>
<summary><strong>Complete Workflow Reference (with C# comparison)</strong></summary>

### All Workflow Patterns

| Pattern | Agent.NET | Description |
|---------|-----------|-------------|
| **Sequential** | `start a` ➔ `next b` ➔ `next c` | Chain steps in order |
| **Parallel** | `fanOut [a; b; c]` | Execute multiple steps simultaneously |
| **Aggregate** | `fanIn combiner` | Combine parallel results |
| **Routing** | `route (function \| Case1 -> a \| Case2 -> b)` | Conditional branching |
| **Retry** | `retry 3` | Retry on failure |
| **Backoff** | `backoff Backoff.Exponential` | Delay strategy between retries |
| **Timeout** | `timeout (TimeSpan.FromSeconds 30.)` | Fail if too slow |
| **Fallback** | `fallback backupStep` | Alternative on failure |
| **Compose** | `next (innerWorkflow \|> Workflow.toExecutor)` | Nest workflows |

### Side-by-Side: Agent.NET vs C# MAF

**Sequential Pipeline**

*Agent.NET:*
```fsharp
let pipeline = workflow {
    start researcher
    next analyst
    next writer
}
```

*C# with MAF:*
```csharp
var graph = new AgentGraphBuilder();
graph.AddNode("researcher", researcherAgent);
graph.AddNode("analyst", analystAgent);
graph.AddNode("writer", writerAgent);
graph.AddEdge("researcher", "analyst");
graph.AddEdge("analyst", "writer");
graph.AddConditionalEdge("writer", _ => EndWorkflow);
var workflow = graph.Build();
```

---

**Parallel Fan-Out / Fan-In**

*Agent.NET:*
```fsharp
let analysis = workflow {
    start loader
    fanOut [technical; fundamental; sentiment]
    fanIn summarizer
}
```

*C# with MAF:*
```csharp
var graph = new AgentGraphBuilder();
graph.AddNode("loader", loaderAgent);
graph.AddNode("technical", technicalAgent);
graph.AddNode("fundamental", fundamentalAgent);
graph.AddNode("sentiment", sentimentAgent);
graph.AddNode("summarizer", summarizerAgent);
graph.AddEdge("loader", "technical");
graph.AddEdge("loader", "fundamental");
graph.AddEdge("loader", "sentiment");
graph.AddEdge("technical", "summarizer");
graph.AddEdge("fundamental", "summarizer");
graph.AddEdge("sentiment", "summarizer");
graph.AddConditionalEdge("summarizer", _ => EndWorkflow);
var workflow = graph.Build();
```

---

**Resilience**

*Agent.NET:*
```fsharp
let resilient = workflow {
    start unreliableService
    retry 3
    backoff Backoff.Exponential
    timeout (TimeSpan.FromSeconds 30.)
    fallback cachedResult
}
```

*C# with MAF + Polly:*
```csharp
var retryPolicy = Policy
    .Handle<Exception>()
    .WaitAndRetryAsync(3, attempt =>
        TimeSpan.FromSeconds(Math.Pow(2, attempt)));

var timeoutPolicy = Policy
    .TimeoutAsync(TimeSpan.FromSeconds(30));

var fallbackPolicy = Policy<string>
    .Handle<Exception>()
    .FallbackAsync(cachedResult);

var combinedPolicy = Policy.WrapAsync(fallbackPolicy, timeoutPolicy, retryPolicy);

var result = await combinedPolicy.ExecuteAsync(async () =>
    await unreliableService.RunAsync(input));
```

---

*The patterns are the same. The ceremony is not.*

</details>

### Result Workflows: Railway-Oriented Programming

For workflows where any step can fail, use `resultWorkflow` for automatic short-circuit handling of errors:

```fsharp
type ValidationError =
    | ParseError of string
    | ValidationFailed of string
    | SaveError of string

let documentWorkflow = resultWorkflow {
    start (ResultExecutor.bind "Parse" parseDocument)
    next (ResultExecutor.bind "Validate" validateSchema)
    next (ResultExecutor.bind "Enrich" addMetadata)
    next (ResultExecutor.map "Save" saveToDatabase)
}

let result = ResultWorkflow.runSync rawInput documentWorkflow
// Returns: Result<Document, ValidationError>
// Short-circuits on first Error, no manual error checking needed!
```

**ResultExecutor helpers:**
- `bind` - For functions returning `Result<'a, 'e>`
- `map` - For functions returning `'a` (wrapped in `Ok` automatically)
- `bindAsync` / `mapAsync` - Async versions

---

## API Reference

### Types

| Type | Description |
|------|-------------|
| `ToolDef` | Tool definition with name, description, parameters, and MethodInfo |
| `ParamInfo` | Parameter metadata: name, description, and type |
| `AgentConfig` | Agent configuration: name, instructions, and tools |
| `ChatAgent` | Built agent with `Chat: string -> Async<string>` |
| `Executor<'i,'o>` | Workflow step that transforms input to output |
| `WorkflowDef<'i,'o>` | Composable workflow definition |
| `ResultExecutor<'i,'o,'e>` | Executor returning `Result<'o,'e>` |
| `ResultWorkflowDef<'i,'o,'e>` | Workflow with error short-circuiting |

### Tool Functions

```fsharp
Tool.create: Expr<'a -> 'b> -> ToolDef
Tool.createWithDocs: Expr<'a -> 'b> -> ToolDef  // Extracts XML docs
Tool.describe: string -> ToolDef -> ToolDef
```

### Agent Functions

```fsharp
Agent.create: string -> AgentConfig                           // Instructions
Agent.withName: string -> AgentConfig -> AgentConfig
Agent.withTool: ToolDef -> AgentConfig -> AgentConfig
Agent.withTools: ToolDef list -> AgentConfig -> AgentConfig
Agent.build: IChatClient -> AgentConfig -> ChatAgent
```

### Workflow CE Keywords

| Keyword | Description |
|---------|-------------|
| `start` | Begin workflow with an executor |
| `next` | Chain the next sequential step |
| `fanOut` | Execute multiple executors in parallel |
| `fanIn` | Combine parallel results into one |
| `route` | Conditional routing based on pattern matching |
| `retry` | Retry failed steps N times |
| `timeout` | Fail if step exceeds duration |
| `fallback` | Use alternative executor on failure |
| `backoff` | Set retry delay strategy |

### Workflow Functions

```fsharp
Workflow.run: 'i -> WorkflowDef<'i,'o> -> Async<'o>
Workflow.runSync: 'i -> WorkflowDef<'i,'o> -> 'o
Workflow.toExecutor: WorkflowDef<'i,'o> -> Executor<'i,'o>
```

---

## XML Documentation Format

For `Tool.createWithDocs` to extract parameter descriptions, use explicit `<summary>` tags:

```fsharp
/// <summary>Searches for documents matching the query</summary>
/// <param name="query">The search query string</param>
/// <param name="limit">Maximum results to return</param>
let search (query: string) (limit: int) : string = ...
```

> **Note:** F# requires `<summary>` tags when using `<param>` tags. Without `<summary>`, the param tags become part of the summary text.

---

## Examples

See the [StockAdvisorFS](./src/StockAdvisorFS) sample project for a complete example including:
- Multiple tools with XML documentation
- Agent configuration
- Real-world usage patterns

---

## Design Philosophy

1. **Quotations for tools** - Automatic metadata extraction, no sync issues
2. **XML docs for descriptions** - Documentation lives with the code
3. **Pipeline for agents** - Simple, composable configuration
4. **CE for workflows** - Complex control flow deserves declarative syntax
5. **No attributes** - Metadata comes from code, not decorators
6. **Pure functions first** - Write normal F#, then wrap with quotations
7. **Type-safe throughout** - Catch errors at compile time
8. **Composition over inheritance** - Workflows nest inside workflows

---

## Dependencies

- [Microsoft.Agents.AI](https://github.com/microsoft/agent-framework) - Microsoft Agent Framework
- [Microsoft.Extensions.AI](https://www.nuget.org/packages/Microsoft.Extensions.AI) - AI abstractions for .NET

---

## License

MIT License - see [LICENSE](LICENSE) for details.

---

## Contributing

Contributions are welcome! Please feel free to submit issues and pull requests.

---

*Built with F# and a belief that AI tooling should be elegant.*
