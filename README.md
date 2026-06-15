# Agent.NET

**Elegant agent workflows for .NET, designed in F#.**

Typed. Declarative. Durable.

[![AgentNet](https://img.shields.io/nuget/v/AgentNet.svg?label=AgentNet)](https://www.nuget.org/packages/AgentNet)
[![AgentNet.InProcess.Polly](https://img.shields.io/nuget/v/AgentNet.InProcess.Polly.svg?label=AgentNet.InProcess.Polly)](https://www.nuget.org/packages/AgentNet.InProcess.Polly)
[![License](https://img.shields.io/github/license/JordanMarr/Agent.NET?v=1)](LICENSE)

---

## What is Agent.NET?

**Agent.NET** is an F#‑native authoring layer built on top of the [Microsoft Agent Framework](https://github.com/microsoft/agent-framework).  
MAF provides a powerful, low‑level foundation for building agent systems — durable state, orchestration primitives, tool execution, and a flexible runtime model.  

**Agent.NET builds on those capabilities with a higher‑level, ergonomic workflow DSL designed for clarity, composability, and developer experience.**  
Where MAF offers the essential building blocks, Agent.NET provides the expressive authoring model that makes agent workflows feel natural to write and reason about.

## What can you do with Agent.NET?

### 1. Create chat agents with tools (`ChatAgent`)
Simple interface: `string -> Task<string>`. Tools are plain F# functions with metadata from XML docs. 

```fsharp
let agent =
    ChatAgent.create "You are a helpful assistant."
    |> ChatAgent.withTools [searchTool; calculatorTool]
    |> ChatAgent.build chatClient

let! text = agent.Chat("Summarize the latest quarterly report.")
```
_[Learn more ->](#tools-quotation-based-metadata-extraction)_

### 2. Create typed agents as functions (`TypedAgent<'input,'output>`)
Wrap a `ChatAgent` with format/parse functions for use in workflows or anywhere you'd call a service.

```fsharp
let analyzeAgent: TypedAgent<CustomerMessage, SentimentResult> = 
    TypedAgent.create formatCustomerMessage parseSentimentResult chatAgent

let! sentiment = analyzeAgent.Invoke message
```
_[Learn more ->](#typedagent-structured-inputoutput-for-workflows)_

### 3. Create workflows (`workflow`)
Strongly typed orchestration mixing deterministic .NET code with LLM calls. Run in-process, or durably with MAF checkpoint/resume — from the same definition.

```fsharp
let myWorkflow = workflow {
    step loadData
    fanOut analyst1 analyst2 analyst3
    fanIn summarize
}
```
_[Learn more ->](#workflows-computation-expression-for-orchestration)_

---

## 🚀 Durable Workflows (Minimal Example)

The **same** workflow definition runs two ways: **in-process** (held in the current process), or **durably** — checkpointed at each step, suspended at `awaitEvent`, and resumed later from the checkpoint (in a different process if needed). Durability is powered by the Microsoft Agent Framework's native checkpoint/resume model — **no Azure Durable Functions dependency**.

A workflow with a suspension point — here an async OCR service we fire and then await a callback from:

```fsharp
let ocrWorkflow =
    workflow {
        name "PdfOcr"
        step downloadPdf                              // PdfRef    -> byte[]
        step requestOcr                               // byte[]    -> unit       (fire; yields the process)
        awaitEvent "OcrComplete" eventOf<OcrResult>   // unit      -> OcrResult   (SUSPEND until callback)
        step storeResult                              // OcrResult -> string
    }
```

Run it durably with a checkpoint store. `start` runs until the workflow completes or suspends; `resume` continues from the checkpoint when the awaited event arrives:

```fsharp
open AgentNet.InProcess

// A checkpoint store: in-memory, file system, or your own ICheckpointStore (e.g. SQL/Blob).
let checkpoints = Workflow.Durable.fileSystemJsonCheckpoints "/var/agentnet/checkpoints"

// sessionId is host-owned — derive it from the inbound event id for idempotency + callback correlation.
match! Workflow.Durable.start checkpoints sessionId pdfRef ocrWorkflow with
| Workflow.Durable.Completed result ->
    // finished without suspending
| Workflow.Durable.Suspended (awaiting, checkpoint) ->
    // checkpoint is persisted; this process can exit. Later, when the OCR callback arrives:
    let! result =
        Workflow.Durable.resume checkpoints ocrWorkflow checkpoint (fun req ->
            if req.EventName = "OcrComplete" then Some (box ocrResult) else None)
```

This is the shape:
- **Declarative workflow definition** — one expression per workflow
- **Typed steps** — plain .NET functions (with or without agents)
- **Explicit suspension** via `awaitEvent` (human-in-the-loop, async service callbacks) — no hidden replay, no determinism rules on your step code
- **Durable execution** via MAF checkpointing — suspend, persist, resume across process restarts
- **`ctx.CorrelationId`** — the run's durable session id, so a fire step can wire an external callback back to the right run

> **Idempotency note:** durable resume re-delivers the step *after* an `awaitEvent` **at-least-once** (as with any durable system), so make post-`awaitEvent` side effects idempotent. The [`Samples.DurableOcr`](./src/Samples.DurableOcr) project shows the full cycle and the dedup pattern.

---

## Installation

**AgentNet** — agents, the workflow DSL, and both in-process and durable (checkpoint/resume) execution
```bash
dotnet add package AgentNet
```

**AgentNet.InProcess.Polly** _(optional)_ — advanced Polly resilience decorators for the in-process runner
```bash
dotnet add package AgentNet.InProcess.Polly
```

> Migrating from `0.x`-era previews: the `AgentNet.InProcess` and `AgentNet.Durable` packages have been merged into **`AgentNet`**. Drop those references and use `AgentNet` — your `open AgentNet.InProcess` code keeps compiling.

---

## Features

### Railway‑Oriented Programming (`tryStep`)

`tryStep` brings typed early‑exit semantics into the `workflow` DSL.
Use it when a step may return a `Result` and you want the workflow to short‑circuit on `Error`.

[Learn more →](#railway-oriented-programming-with-trystep)

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

### Tools: Dependency Injection (`Tool.inject`)

Most real-world tool functions need a dependency — a database, an HTTP client, a domain service. But the agent shouldn't see (or be able to fill in) that dependency: it's a host concern, not a model concern.

`Tool.inject` partially applies the **leftmost parameter** of a tool's underlying function with a value you supply, and returns a new `ToolDef` whose metadata and method signature are exactly one parameter shorter. The captured dependency is forwarded to the original function at invoke time.

```fsharp
/// <summary>Looks up a user by id</summary>
/// <param name="db">The database connection</param>
/// <param name="userId">The user's id</param>
let lookupUser (db: IDb) (userId: int) : string =
    db.GetUserName userId

let lookupUserTool =
    Tool.createWithDocs <@ lookupUser @>
    |> Tool.inject realDb
// The agent now sees a 1-parameter tool: { Name = "lookupUser"; Parameters = [userId: int] }
// At invoke time, `realDb` is passed automatically; the model only supplies `userId`.
```

**Why `Tool.inject`?**
- **Hide infrastructure from the model** — the LLM only sees parameters it can meaningfully reason about
- **Per-request dependencies** — capture a tenant-scoped service, a request-scoped logger, etc. by injecting a fresh value each time you build the agent
- **No wrapper boilerplate** — you don't need to hand-write a closure-shaped tool function just to thread a dependency through
- **XML metadata still works** — descriptions on the remaining parameters survive the injection

`Tool.inject` is composable in pipelines and works whether the dependency is the only parameter or one of many. You can also inject into functions whose remaining input is `unit`:

```fsharp
let nowFromClock (clock: IClock) () : string = clock.Now()

let nowTool =
    Tool.create <@ nowFromClock @>
    |> Tool.inject systemClock
    |> Tool.describe "Returns the current time"
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
    
let! report = Workflow.InProcess.run claimData claimsWorkflow
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

Build fault-tolerant workflows with built-in decorators:

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

### Polly Integration (InProcess Runtime Only)

AgentNet includes **built‑in resilience operations** — `retry`, `timeout`, `fallback` — that compile into the workflow graph, so they work consistently whether the workflow runs in-process or durably. Because they're part of the definition, they're checkpointed along with the rest of the workflow.

For **advanced, runtime‑only resilience scenarios** — such as circuit breakers, hedging, rate limiting, or composite resilience strategies — you can optionally integrate **Polly** through the `AgentNet.InProcess.Polly` extension package.

Polly policies run **only in the InProcess runtime**, and are applied using the standard decorator mechanism:

```fsharp
open Polly
open AgentNet.InProcess.Polly
open PollyDecorators

let retryPolicy =
    ResiliencePipelineBuilder()
        .AddRetry(fun r ->
            r.MaxRetryAttempts <- 3
        )
        .Build()

let resilientWorkflow = workflow {
    step unreliableStep
    decorate (policy retryPolicy)
}
```

### Composing Multiple Polly Strategies

```fsharp
let combinedPolicy =
    ResiliencePipelineBuilder()
        .AddRetry(fun r ->
            r.MaxRetryAttempts <- 3
            r.Delay <- TimeSpan.FromSeconds 1.
        )
        .AddTimeout(fun t ->
            t.Timeout <- TimeSpan.FromSeconds 30.
        )
        .Build()

workflow {
    step callExternalApi
    decorate (policy combinedPolicy)
    step processResult
}
```

### Cancellation

AgentNet automatically threads the workflow’s `CancellationToken` into all Polly policies. This ensures:

- Polly timeouts cancel the workflow token  
- Steps observing `ctx.CancellationToken` abort promptly  
- External cancellation via `runWithCancellation` flows into Polly  
- Retry loops stop immediately when cancellation is signaled  

```fsharp
use cts = new CancellationTokenSource()
cts.CancelAfter(TimeSpan.FromSeconds 10.)

let! result =
    myWorkflow
    |> Workflow.InProcess.runWithCancellation cts.Token input
```

---

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
    step (Workflow.InProcess.toExecutor "InnerStep" innerWorkflow)
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
// In-process (returns a Task<'output>)
let! result = Workflow.InProcess.run "initial input" myWorkflow

// Need a blocking call? Await the Task explicitly:
let result = (Workflow.InProcess.run "initial input" myWorkflow).GetAwaiter().GetResult()
```

## Railway-Oriented Programming with `tryStep`

Workflows often need to perform validation or business‑rule checks that may fail.  
`tryStep` brings Railway-Oriented Programming directly into the main workflow DSL, giving you short‑circuiting error handling without switching to a different computation expression or monadic style.

### When a `tryStep` returns:

- `Ok value` → the workflow continues with `value`  
- `Error err` → the workflow **exits immediately**, returning `Error err` from `tryRun`  

This gives you the classic “railway switch” behavior with minimal ceremony.

### Example

```fsharp
// Custom error type for the workflow
type ProcessingError =
    | ParseError of string
    | ValidationError of string

let parse (raw: string) =
    if raw.Length > 0
    then Ok { Id = "doc"; Content = raw }
    else Error (ParseError "Empty input")
    |> Task.fromResult

let validate (doc: Document) =
    if doc.Content.Contains("valid")
    then Ok { Doc = doc; IsValid = true; Errors = [] }
    else Error (ValidationError "Missing 'valid' keyword")
    |> Task.fromResult

let save (validated: ValidatedDoc) =
    printfn "Saving Document"
    { Doc = validated; WordCount = 1; Summary = "Saved" }
    |> Task.fromResult

let documentWorkflow = workflow {
    tryStep parse
    tryStep validate
    step save
}
```

### Running the workflow

```fsharp
let! result = Workflow.InProcess.tryRun rawInput documentWorkflow
```

- If any `tryStep` returns `Error`, the workflow stops immediately  
- `tryRun` returns `Result<'ok, 'err>`  
- `run` (without `Result`) surfaces the early‑exit as a thrown signal instead — use `tryRun` when you want the typed `Result`  

### Why `tryStep` feels so natural

- **No monadic boilerplate** — you stay in the main `workflow` CE  
- **No type contagion** — only the steps that need `Result` use it  
- **Clear, predictable control flow** — early exit is explicit and typed  
- **One DSL** — `tryStep` is part of the same `workflow` CE; no separate monadic style to switch into  

### Step types supported by `tryStep`

- Functions returning `Task<Result<'o,'e>>` or `Async<Result<'o,'e>>`  
- `TypedAgent<'i,'o>` (automatically wrapped in `Ok`)  
- Any normal step can follow a `tryStep` — the workflow only exits when a `tryStep` returns `Error`  

---

## API Reference

### Types

| Type | Description |
|------|-------------|
| `ToolDef` | Tool definition with name, description, parameters, and MethodInfo |
| `ParamInfo` | Parameter metadata: name, description, and type |
| `ChatAgentConfig` | Agent configuration: name, instructions, and tools |
| `ChatAgent` | Built agent with `Chat: string -> CancellationToken -> Task<string>` and `ChatFull` |
| `TypedAgent<'i,'o>` | Typed wrapper around ChatAgent with format/parse functions |
| `ChatResponse` | Full response with `Text` and `Messages` list |
| `ChatMessage` | Message with `Role` and `Content` |
| `ChatRole` | Union type: User, Assistant, System, Tool |
| `WorkflowContext` | Context passed to executors with `RunId`, `State`, `CancellationToken`, `Services` (DI), and `CorrelationId` (durable session id) |
| `Executor<'i,'o>` | Workflow step that transforms input to output |
| `WorkflowDef<'i,'o>` | Composable workflow definition |

### Tool Functions

| Function | Description |
|---------|-------------|
| `Tool.create` | Creates a tool from an F# function using a quotation. |
| `Tool.createWithDocs` | Creates a tool and extracts XML documentation from the quoted function. |
| `Tool.describe` | Overrides or adds a description for a tool. |
| `Tool.inject` | Partially applies the leftmost parameter (a dependency) of a tool's function, returning a new `ToolDef` with one fewer parameter. |


### Agent Functions

| Function | Description |
|---------|-------------|
| `ChatAgent.create` | Creates a chat agent configuration with the given instruction string. |
| `ChatAgent.withName` | Assigns a display name to the agent. |
| `ChatAgent.withTool` | Adds a single tool to the agent configuration. |
| `ChatAgent.withTools` | Adds multiple tools to the agent configuration. |
| `ChatAgent.build` | Builds a `ChatAgent` using an `IChatClient` and the configuration. |
| `TypedAgent.create` | Wraps a `ChatAgent` with format/parse functions for typed input/output. |
| `TypedAgent.invoke` | Invokes a typed agent: structured input in, structured output out. |
| `TypedAgent.invokeWithCancellation` | Invokes a typed agent with a `CancellationToken` for cooperative cancellation. |


### Workflow CE Keywords

| Keyword | Description |
|---------|-------------|
| `step` | Add a step to the workflow |
| `tryStep` | Execute a step that returns Result; short‑circuits the workflow on Error |
| `fanOut` | Execute multiple executors in parallel |
| `fanIn` | Combine parallel results into one |
| `route` | Conditional routing based on pattern matching |
| `retry` | Retry failed steps N times |
| `timeout` | Fail if step exceeds duration |
| `fallback` | Use alternative executor on failure |
| `backoff` | Set retry delay strategy |
| `policy` | Apply a Polly `ResiliencePipeline` to the preceding step (retry, timeout, circuit breaker, rate limiter, etc.) |

### Workflow Functions

| Function | Description |
|---------|-------------|
| `Workflow.InProcess.run` | Runs a workflow in‑process (held in the current process) and returns the final output. Throws on `tryStep` errors. |
| `Workflow.InProcess.runWithCancellation` | Like `run`, but accepts an external `CancellationToken` that flows into every step and Polly policy. |
| `Workflow.InProcess.runWithServices` / `runWith` | Like `run`, seeding each step's `ctx.Services` with an `IServiceProvider` (and, for `runWith`, a `CancellationToken`). |
| `Workflow.InProcess.runWithResponses` | Drives a suspending (`awaitEvent`) workflow to completion in-process by supplying responses via a callback — the in-process counterpart to durable resume. |
| `Workflow.InProcess.tryRun` | Runs a workflow in‑process and returns `Result<'output,'error>` with early‑exit handling. |
| `Workflow.Durable.start` | Starts a durable run under a host-owned `sessionId`; checkpoints each step and runs until completion or the first `awaitEvent` suspension. Returns `Completed` or `Suspended (pending, checkpoint)`. |
| `Workflow.Durable.resume` | Resumes a durable run from a `CheckpointInfo`, answering awaited events via a `respond` callback. Runs to completion or the next suspension. |
| `Workflow.Durable.inMemoryCheckpoints` / `fileSystemJsonCheckpoints` | Create a `CheckpointManager` backed by in-memory or on-disk JSON storage. Provide your own `ICheckpointStore` for SQL/Blob/etc. |


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
let stockAnalysisWF = workflow {
    step fetchStockData
    fanOut technicalAnalysis fundamentalAnalysis sentimentAnalysis
    fanIn synthesizeReports
    step generateRecommendation
}

// In-process execution (quick-running workflows)
let! result = Workflow.InProcess.run input stockAnalysisWF

// Durable execution (long-running; checkpointed suspend/resume)
let! outcome = Workflow.Durable.start checkpoints sessionId input stockAnalysisWF
```

### Why a Semantic Layer?

| Direct MAF (C#) | Agent.NET Workflow (F#) |
|-----------------|------------------------|
| Verbose executor classes | Normal F# functions |
| Manual graph wiring | Declarative `step`, `fanOut`, `fanIn` |
| Stringly-typed edges | Compiler-enforced type transitions |
| Resilience via Polly boilerplate | Built-in `retry`, `timeout`, `fallback`, or bring your own Polly `policy` |

### Two Execution Modes

Agent.NET supports both execution models from a single workflow definition:

| Mode | API | Description |
|------|-----|-------------|
| **In-process** | `Workflow.InProcess.run` | Short-lived workflows executed (and held) within the current process. |
| **Durable** | `Workflow.Durable.start` / `resume` | MAF-native checkpointing: each step is checkpointed, the run suspends at `awaitEvent`, and resumes from the checkpoint later — across process restarts, persisted to your `ICheckpointStore`. No Azure dependency. |

*Same workflow. Your choice of execution model.*

---


## Dependencies

Agent.NET is lightweight, platform‑agnostic, and free of Azure‑specific hosting requirements — both in-process and durable execution run on the Microsoft Agent Framework alone.

### **AgentNet**  
The whole library — agents, the workflow DSL, and in-process + durable (checkpoint/resume) execution.

- **Microsoft.Agents.AI** — agent primitives  
- **Microsoft.Agents.AI.Workflows** — workflow graph, in‑process execution, and checkpointing  
- **Microsoft.Extensions.AI.Abstractions** — AI service abstractions for .NET

### **AgentNet.InProcess.Polly** _(optional)_  
Advanced Polly resilience decorators for the in-process runner.

- **Polly.Core** — circuit breakers, hedging, rate limiting, composite strategies  

---

## License

MIT License - see [LICENSE](LICENSE) for details.

---

## Contributing

Contributions are welcome! Please feel free to submit issues and pull requests.

---

## Roadmap

### Workflow State Management

`WorkflowContext` now carries `Services` (DI) and `CorrelationId` (the durable session id), both seeded per run. The remaining gap is the `State` dictionary: each step receives a fresh context, so `State` changes don't propagate between steps. Cross-step state should currently flow through step inputs/outputs (which *is* what gets checkpointed durably); a future release will offer a friendlier typed-state API on top.

| Feature | Status | Description |
|---------|--------|-------------|
| **`State` propagation** | Planned | Make `WorkflowContext.State` set in one step available to later steps (and checkpointed durably). |
| **Strongly-typed state** | Planned | A `runWithState` API for passing typed state between steps and seeding a workflow with initial state. |
| **F# DU checkpoint serialization** | Investigating | F# discriminated unions don't yet round-trip through MAF's JSON checkpoint serializer (records do). |

---

*Built with F# and a belief that AI tooling should be elegant.*
