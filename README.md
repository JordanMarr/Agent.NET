# Agent.NET

**Elegant agent workflows for .NET, designed in F#.**

[![AgentNet](https://img.shields.io/nuget/v/AgentNet.svg?label=AgentNet)](https://www.nuget.org/packages/AgentNet)
[![AgentNet.Durable](https://img.shields.io/nuget/v/AgentNet.Durable.svg?label=AgentNet.Durable)](https://www.nuget.org/packages/AgentNet.Durable)
[![License](https://img.shields.io/github/license/JordanMarr/Agent.NET?v=1)](LICENSE)

---

## What is Agent.NET?

**Agent.NET** is an F# library built on the [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) that provides **agents**, **tools**, and **workflows**.

## What can you do with Agent.NET?

### 1. Create chat agents with tools (`ChatAgent`)
Simple interface: `string -> Task<string>`. Tools are plain F# functions with metadata from XML docs. 

```fsharp
let agent =
    ChatAgent.create "You are a helpful assistant."
    |> ChatAgent.withTools [searchTool; calculatorTool]
    |> ChatAgent.build chatClient
```
_[Learn more ->](#tools-quotation-based-metadata-extraction)_

### 2. Create typed agents as functions (`TypedAgent<'input,'output>`)
Wrap a `ChatAgent` with format/parse functions for use in workflows or anywhere you'd call a service.

```fsharp
let typedAgent = TypedAgent.create formatInput parseOutput chatAgent
let! result = typedAgent.Invoke(myInput)  // Typed in, typed out
```
_[Learn more ->](#typedagent-structured-inputoutput-for-workflows)_

### 3. Create workflows (`workflow` / `resultWorkflow`)
Strongly typed orchestration mixing deterministic .NET code with LLM calls. Run in-process or on Azure Durable Functions.

```fsharp
let myWorkflow = workflow {
    step loadData
    fanOut analyst1 analyst2 analyst3
    fanIn summarize
}
```
_[Learn more ->](#workflows-computation-expression-for-orchestration)_

---

## 🚀 Durable Workflows in Azure (Minimal Example)

Agent.NET workflows run anywhere — in‑memory for local execution, or durably on Azure using Durable Functions.

From the `Samples.DurableFunctions` project:

```fsharp
/// A durable trade approval workflow defined with Agent.NET
let tradeApprovalWorkflow =
    workflow {
        name "TradeApprovalWorkflow"
        step analyzeStock
        step sendForApproval
        awaitEvent "TradeApproval" eventOf<ApprovalDecision>
        step executeTrade
    }
```

You can host this workflow inside an Azure Durable Functions orchestrator written in F# **or C#**.

<details>
<summary>F# orchestrator</summary>

```fsharp
module TradeApprovalWorkflow

open Microsoft.DurableTask
open Microsoft.Azure.Functions.Worker
open AgentNet.Durable

[<Function("TradeApprovalOrchestrator")>]
let orchestrator ([<OrchestrationTrigger>] ctx: TaskOrchestrationContext) =
    let request = ctx.GetInput<TradeRequest>()
    Workflow.Durable.run ctx request tradeApprovalWorkflow
```

</details>

<details open>
<summary>C# orchestrator</summary>

Call a workflow defined in your F# project:

```csharp
using Microsoft.DurableTask;
using Microsoft.Azure.Functions.Worker;
using AgentNet.Durable;

public static class TradeApprovalOrchestrator
{
    [Function("TradeApprovalOrchestrator")]
    public static Task<TradeResult> Run([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var request = context.GetInput<TradeRequest>();
        return Workflow.Durable.run(context, request, tradeApprovalWorkflow);
    }
}
```

</details>

This is the final shape:  
- **Declarative workflow definition** — one expression per workflow  
- **Typed steps** — plain .NET functions (with or without agents)  
- **Explicit suspension** via `awaitEvent` (human-in-the-loop, external events)  
- **Durable execution** powered by MAF and Azure Durable Functions  
- **Minimal host surface** — the orchestrator simply runs the workflow  

---

## What's Included

| Feature | Description |
|---------|-------------|
| **ChatAgent** | AI agents with automatic tool metadata extraction from F# quotations |
| **TypedAgent** | Structured I/O wrapper for type-safe agent integration in workflows |
| **workflow CE** | Composable pipelines with SRTP type inference, fan-out/fan-in, routing |
| **resultWorkflow CE** | Railway-oriented programming with automatic error short-circuiting |
| **Resilience** | Built-in retry, backoff strategies, timeout, and fallback |

All with clean F# syntax - no attributes, no magic strings, no ceremony.

---

## Installation

**AgentNet** — Agents and in-process workflows
```bash
dotnet add package AgentNet
```

**AgentNet.Durable** — Azure Durable Function workflows
```bash
dotnet add package AgentNet.Durable
```

---

## Quick Start

### 1. Build a ChatAgent

Create a simple `ChatAgent` that works on strings:

```fsharp
open AgentNet

let summarizerAgent =
    ChatAgent.create "You summarize articles into concise bullet points."
    |> ChatAgent.withName "Summarizer"
    |> ChatAgent.build chatClient   // any IChatClient implementation

// Shape: string -> Task<string>
let! text = summarizerAgent.Chat("Summarize the latest quarterly report.")
```

_Learn more: [ChatAgent: pipeline-style configuration ->](#chatagent-pipeline-style-configuration)_

> `ChatAgent` values always have the shape `string -> Task<string>`. To use them inside a
> typed workflow, you adapt them into `TypedAgent<'i,'o>` by providing format/parse functions.

### 2. Wrap it as a TypedAgent

Turn the string-based agent into a typed function that can be composed into the workflow:

```fsharp
// Typed domain model
type Article = { Id: string; Title: string; Body: string }
type Summary = { Title: string; Summary: string }

// Format typed input into a prompt
let formatArticle (article: Article) =
    $"Summarize the following article titled '{article.Title}':\n\n{article.Body}"

// Parse the model response back into a typed result
let parseSummary (article: Article) (response: string) : Summary =
    { Title = article.Title; Summary = response }

let typedSummarizer : TypedAgent<Article, Summary> =
    TypedAgent.create formatArticle parseSummary summarizerAgent
```

_Learn more: [TypedAgent: structured input/output for workflows ->](#typedagent-structured-inputoutput-for-workflows)_

### 3. Compose a workflow

Use deterministic steps around the `TypedAgent` inside a workflow:

```fsharp
let summarizationWorkflow = workflow {
    step loadArticleFromDb   // string -> Task<Article>
    step typedSummarizer     // Article -> Task<Summary>
    step saveSummaryToDb     // Summary -> Task<unit>
}

let! result = Workflow.InProcess.run "article-123" summarizationWorkflow
```

_Learn more: [Workflows: computation expression for orchestration ->](#workflows-computation-expression-for-orchestration)_

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

### ChatAgent: Pipeline-Style Configuration

Build agents using a clean pipeline:

```fsharp
let stockAdvisor =
    ChatAgent.create """
        You are a stock market analyst. You help users understand
        stock performance, analyze trends, and compare investments.
        Always provide balanced, factual analysis.
        """
    |> ChatAgent.withName "StockAdvisor"
    |> ChatAgent.withTool getStockInfoTool
    |> ChatAgent.withTool getHistoricalPricesTool
    |> ChatAgent.withTool calculateVolatilityTool
    |> ChatAgent.withTools [compareTool; analysisTool]  // Add multiple at once
    |> ChatAgent.build chatClient
```

Use your agent:

```fsharp
// Async chat
let! response = stockAdvisor.Chat("Compare AAPL and MSFT performance")

// Access the underlying config if needed
printfn $"Agent: {stockAdvisor.Config.Name}"
```

### TypedAgent: Structured Input/Output for Workflows

While `ChatAgent` works with strings (`string -> Task<string>`), workflows often need typed data flowing between steps. `TypedAgent` wraps a `ChatAgent` with format/parse functions to enable strongly-typed workflows.

> For a minimal example of wrapping a `ChatAgent` into a `TypedAgent` and using it in a workflow,
> see the [Quick Start](#quick-start). The example below shows a more involved stock comparison scenario.

```fsharp
// Domain types for your workflow
type StockPair = { Stock1: StockData; Stock2: StockData }
type AnalysisResult = { Pair: StockPair; Analysis: string }

// Define a function to format the typed input into a prompt.
let formatStockPair (pair: StockPair) =
    $"""Compare these two stocks:
    {pair.Stock1.Symbol}: {pair.Stock1.Info}
    {pair.Stock2.Symbol}: {pair.Stock2.Info}"""

// Define a function that the AI can use to return a typed output.
let parseAnalysisResult (pair: StockPair) (response: string) =
    { Pair = pair; Analysis = response }

// Create the typed agent
let typedAnalyst = TypedAgent.create formatStockPair parseAnalysisResult stockAnalystAgent
```

**Using TypedAgent standalone:**

```fsharp
// Invoke with typed input, get typed output
let! result = typedAnalyst.Invoke(stockPair)
printfn $"Analysis: {result.Analysis}"
```

**Using TypedAgent in workflows:**

The real power is using `TypedAgent` as a strongly-typed step in a workflow:

```fsharp
let comparisonWorkflow = workflow {
    step fetchBothStocks   // async/task function
    step typedAnalyst      // TypedAgent works directly
    step generateReport    // sync function (returns Task.fromResult)
}

let input = { Symbol1 = "AAPL"; Symbol2 = "MSFT" }
let! report = Workflow.InProcess.run input comparisonWorkflow
```

The workflow is fully type-safe: the compiler ensures each step's output type matches the next step's input type. Steps can be named for debugging/logging, or unnamed for brevity.

### Workflows: Computation Expression for Orchestration

The `workflow` CE generalizes the patterns from the Quick Start to more complex multi-agent scenarios. Orchestrate complex multi-agent scenarios with elegant, readable syntax.

The `step` operation directly accepts:
- **Task functions** (`'a -> Task<'b>`)
- **Async functions** (`'a -> Async<'b>`)
- **TypedAgents** (`TypedAgent<'a,'b>`)
- **Other workflows** (`WorkflowDef<'a,'b>`)

No wrapping required—just pass them in. The compiler ensures each step's output type matches the next step's input type, catching mismatches at compile time.

#### Sequential Pipelines

```fsharp
let reportWorkflow = workflow {
    step researcher      // Gather information
    step analyst         // Analyze findings
    step writer          // Write the report
    step editor          // Polish and refine
}
```

<details>
<summary>C# MAF equivalent</summary>

```csharp
var builder = new WorkflowBuilder(researcherExecutor);
builder.AddEdge(researcherExecutor, analystExecutor);
builder.AddEdge(analystExecutor, writerExecutor);
builder.AddEdge(writerExecutor, editorExecutor);
builder.WithOutputFrom(editorExecutor);
var workflow = builder.Build();
```
</details>

#### Parallel Fan-Out / Fan-In

Process data in parallel, then combine results:

```fsharp
let claimsWorkflow = workflow {
    step extractClaims
    fanOut 
        checkPolicy 
        assessRisk 
        detectFraud
    fanIn aggregateResults
    step generateReport
}
    
let report = Workflow.runSync claimData claimsWorkflow
```

> **Note:** `fanOut` supports 2-5 direct arguments. For 6+ branches, use list syntax with the `+` operator, which converts each item to a unified `Step` type (enabling mixed executors, functions, angents, and workflows in the same list):
> ```fsharp
> fanOut [+analyst1; +analyst2; +analyst3; +analyst4; +analyst5; +analyst6]
> ```

<details>
<summary>C# MAF equivalent</summary>

```csharp
var builder = new WorkflowBuilder(extractClaimsExecutor);
builder.AddEdge(extractClaimsExecutor, checkPolicyExecutor);
builder.AddEdge(extractClaimsExecutor, assessRiskExecutor);
builder.AddEdge(extractClaimsExecutor, detectFraudExecutor);
builder.AddEdge(checkPolicyExecutor, aggregateResultsExecutor);
builder.AddEdge(assessRiskExecutor, aggregateResultsExecutor);
builder.AddEdge(detectFraudExecutor, aggregateResultsExecutor);
builder.AddEdge(aggregateResultsExecutor, generateReportExecutor);
builder.WithOutputFrom(generateReportExecutor);
var workflow = builder.Build();
```
</details>

#### Conditional Routing

Route to different agents based on content:

```fsharp
type Priority = Urgent | Normal | LowPriority

let triageWorkflow = workflow {
    step classifier
    route (function
        | Urgent -> urgentHandler
        | Normal -> standardHandler
        | LowPriority -> batchHandler)
}
```

<details>
<summary>C# MAF equivalent</summary>

```csharp
// Create transitions with explicit filters
var urgentTransition = new Transition(
    classifierExecutor,
    urgentHandlerExecutor
);
urgentTransition.Filter = result => result is Urgent;

var normalTransition = new Transition(
    classifierExecutor,
    standardHandlerExecutor
);
normalTransition.Filter = result => result is Normal;

var lowTransition = new Transition(
    classifierExecutor,
    batchHandlerExecutor
);
lowTransition.Filter = result => result is LowPriority;

// Add transitions to the workflow
builder.AddTransition(urgentTransition);
builder.AddTransition(normalTransition);
builder.AddTransition(lowTransition);

// Declare possible outputs
builder.WithOutputFrom(
    urgentHandlerExecutor,
    standardHandlerExecutor,
    batchHandlerExecutor
);

var workflow = builder.Build();
```

</details>

#### Resilience: Retry, Timeout, Fallback

Build fault-tolerant workflows:

```fsharp
let resilientWorkflow = workflow {
    step primaryAgent
    retry 3                              // Retry up to 3 times
    timeout (TimeSpan.FromSeconds 30.0)  // Timeout after 30s
    fallback backupAgent                 // Use backup if all else fails
}
```

Combine resilience with other operations:

```fsharp
let robustAnalysis = workflow {
    step loadData
    fanOut analyst1 analyst2 analyst3
    retry 2
    fanIn combiner
    timeout (TimeSpan.FromMinutes 5.0)
    fallback cachedResults
}
```

<details>
<summary>C# MAF + Polly equivalent</summary>

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

// Then wire into your executor...
```
</details>

#### Composition: Nest Workflows

Workflows are composable - nest them freely:

```fsharp
let innerWorkflow = workflow {
    step stepA
    step stepB
}

// Direct nesting - just pass the workflow!
let outerWorkflow = workflow {
    step preprocess
    step innerWorkflow
    step postprocess
}

// Or use toExecutor when you want explicit naming
let namedOuter = workflow {
    step preprocess
    step (Workflow.toExecutor "InnerStep" innerWorkflow)
    step postprocess
}
```

<details>
<summary>C# MAF equivalent</summary>

```csharp
// Build the inner workflow
var innerBuilder = new WorkflowBuilder(stepAExecutor);
innerBuilder.AddEdge(stepAExecutor, stepBExecutor);
innerBuilder.WithOutputFrom(stepBExecutor);
var innerWorkflow = innerBuilder.Build();

// Convert the inner workflow into an executor
var innerExecutor = new WorkflowExecutor("InnerStep", innerWorkflow);

// Build the outer workflow
var outerBuilder = new WorkflowBuilder(preprocessExecutor);
outerBuilder.AddEdge(preprocessExecutor, innerExecutor);
outerBuilder.AddEdge(innerExecutor, postprocessExecutor);
outerBuilder.WithOutputFrom(postprocessExecutor);

var outerWorkflow = outerBuilder.Build();
```

</details>

#### Running Workflows

```fsharp
// Synchronous
let result = Workflow.runSync "initial input" myWorkflow

// Asynchronous
let! result = Workflow.InProcess.run "initial input" myWorkflow
```

### Result Workflows: Railway-Oriented Programming

For workflows where any step can fail, use `resultWorkflow` for automatic short-circuit handling of errors:

```fsharp
// Model custom error types for your workflow (or use a simple string error).
type ValidationError =
    | ParseError of string
    | ValidationFailed of string
    | SaveError of string

// Functions that return Task<Result<...>> work directly (bind semantics)
let parseDocument (raw: string) : Task<Result<Document, ValidationError>> = task { ... }
let validateSchema (doc: Document) : Task<Result<ValidatedDoc, ValidationError>> = task { ... }
let addMetadata (doc: ValidatedDoc) : Task<Result<EnrichedDoc, ValidationError>> = task { ... }

// Functions that DON'T return Result use 'ok' wrapper (map semantics)
let saveToDatabase (doc: EnrichedDoc) : Task<SavedDoc> = task { ... }

let documentWorkflow = resultWorkflow {
    step parseDocument       // Task<Result<...>> - auto bind semantics
    step validateSchema      // Task<Result<...>> - auto bind semantics
    step addMetadata         // Task<Result<...>> - auto bind semantics
    step (ok saveToDatabase) // Task<...> - wrapped in Ok via 'ok' wrapper
}

let result = ResultWorkflow.runSync rawInput documentWorkflow
// Returns: Result<SavedDoc, ValidationError>
// Short-circuits on first Error, no manual error checking needed!
```

**Step types:**
- Functions returning `Task<Result<'o, 'e>>` or `Async<Result<'o, 'e>>` - auto bind semantics
- Functions returning `Task<'o>` or `Async<'o>` - use `ok` wrapper for map semantics
- `ResultExecutor<'i, 'o, 'e>` - direct passthrough (for explicit naming or backwards compatibility)
- `TypedAgent<'i, 'o>` - auto wrapped in Ok

---

## API Reference

### Types

| Type | Description |
|------|-------------|
| `ToolDef` | Tool definition with name, description, parameters, and MethodInfo |
| `ParamInfo` | Parameter metadata: name, description, and type |
| `ChatAgentConfig` | Agent configuration: name, instructions, and tools |
| `ChatAgent` | Built agent with `Chat: string -> Task<string>` and `ChatFull: string -> Task<ChatResponse>` |
| `TypedAgent<'i,'o>` | Typed wrapper around ChatAgent with format/parse functions |
| `ChatResponse` | Full response with `Text` and `Messages` list |
| `ChatMessage` | Message with `Role` and `Content` |
| `ChatRole` | Union type: User, Assistant, System, Tool |
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
// ChatAgent - for interactive chat
ChatAgent.create: string -> ChatAgentConfig                              // Instructions
ChatAgent.withName: string -> ChatAgentConfig -> ChatAgentConfig
ChatAgent.withTool: ToolDef -> ChatAgentConfig -> ChatAgentConfig
ChatAgent.withTools: ToolDef list -> ChatAgentConfig -> ChatAgentConfig
ChatAgent.build: IChatClient -> ChatAgentConfig -> ChatAgent

// TypedAgent - for structured workflows
TypedAgent.create: ('i -> string) -> ('i -> string -> 'o) -> ChatAgent -> TypedAgent<'i,'o>
TypedAgent.invoke: 'i -> TypedAgent<'i,'o> -> Task<'o>
```

### Workflow CE Keywords

| Keyword | Description |
|---------|-------------|
| `step` | Add a step to the workflow |
| `fanOut` | Execute multiple executors in parallel |
| `fanIn` | Combine parallel results into one |
| `route` | Conditional routing based on pattern matching |
| `retry` | Retry failed steps N times |
| `timeout` | Fail if step exceeds duration |
| `fallback` | Use alternative executor on failure |
| `backoff` | Set retry delay strategy |

### Workflow Functions

```fsharp
Workflow.runInProcess: 'i -> WorkflowDef<'i,'o> -> Task<'o>
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

## Workflow: A Semantic Layer for MAF

Agent.NET's `workflow` CE is a **semantic layer** for Microsoft Agent Framework (MAF). Define your workflow once in expressive F#, then choose how to run it:

```fsharp
let stockAnalysis = workflow {
    step fetchStockData
    fanOut technicalAnalysis fundamentalAnalysis sentimentAnalysis
    fanIn synthesizeReports
    step generateRecommendation
}

// In-memory execution (quick-running workflows)
let! result = Workflow.InProcess.run input stockAnalysis

// MAF durable execution (long-running, durable workflows)
let mafWorkflow = Workflow.Durable.run ctx request stockAnalysis
```

### Why a Semantic Layer?

| Direct MAF (C#) | Agent.NET Workflow (F#) |
|-----------------|------------------------|
| Verbose executor classes | Normal F# functions |
| Manual graph wiring | Declarative `step`, `fanOut`, `fanIn` |
| Stringly-typed edges | Compiler-enforced type transitions |
| Resilience via Polly boilerplate | Built-in `retry`, `timeout`, `fallback` |

### Two Execution Modes

Agent.NET supports both execution models from a single workflow definition:

| Mode | API | Description |
|------|-----|-------------|
| **In-memory** | `Workflow.InProcess.run` | Used for short-lived workflows executed within the current process. |
| **MAF Durable** | `Workflow.Durable.run` | Runs on MAF's durable runtime (backed by Azure Durable Functions) with automatic checkpointing, replay, and fault tolerance. |

*Same workflow. Your choice of execution model.*

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
