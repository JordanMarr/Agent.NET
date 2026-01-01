# AgentNet Workflow DSL Design

## Vision

Replace MAF's ceremonial `WorkflowBuilder` pattern with a declarative F# DSL that leverages:
- Pattern matching for routing
- Discriminated unions for type-safe decisions
- Computation expressions for composition
- Type inference to eliminate boilerplate

## Design Goals

1. **Declarative over Imperative** - Describe the workflow shape, not the construction steps
2. **Type-Safe Routing** - Use DUs and pattern matching, not `Func<object?, bool>`
3. **Composable** - Build complex workflows from simple, reusable pieces
4. **Minimal Ceremony** - No factories, no builder lambdas, no explicit casts

---

## Core Syntax

### Simple Sequential Workflow

```fsharp
let myWorkflow = workflow {
    start researcher
    next analyzer
    next writer
}
```

> **Note:** We use `next` instead of `then` because `then` is a reserved keyword in F#.

### Conditional Routing (Pattern Matching)

```fsharp
// Define a discriminated union for decisions
type SpamDecision =
    | Spam of reason: string
    | NotSpam
    | Uncertain

let emailWorkflow = workflow {
    start spamDetector

    route (function
        | Spam _    -> spamHandler
        | NotSpam   -> emailAssistant |> andThen sendEmail
        | Uncertain -> manualReview)
}
```

### Parallel Fan-Out / Fan-In

```fsharp
let analysisWorkflow = workflow {
    start dataLoader

    fanOut [
        technicalAnalyst
        fundamentalAnalyst
        sentimentAnalyst
    ]

    fanIn summaryWriter
}
```

> **Note:** We use `fanOut`/`fanIn` instead of `parallel`/`aggregate` because `parallel` is reserved in F#.

### Error Handling & Resilience

```fsharp
let resilientWorkflow = workflow {
    start riskyAgent
    retry 3
    backoff (Exponential (TimeSpan.FromSeconds 1., 2.0))
    timeout (TimeSpan.FromMinutes 5.)
    fallback safeAgent
    next outputHandler
}
```

The resilience modifiers apply to the previous step (`riskyAgent` in this case).

### Composition (Nested Workflows)

```fsharp
let research = workflow {
    start researcher
    next factChecker
}

let writing = workflow {
    start drafter
    next editor
    next publisher
}

let fullPipeline = workflow {
    start research        // Embed workflow as step
    next analyzer
    next writing          // Embed another workflow
}
```

---

## Executor Definition

### Lightweight: From Function

```fsharp
// Simple function becomes an executor
let uppercase =
    Executor.fromFn "Uppercase" (fun (input: string) ->
        input.ToUpperInvariant())

// Async function
let spamDetector =
    Executor.fromAsync "SpamDetector" (fun (email: string) -> async {
        let! result = spamAgent.Chat email
        return parseSpamResult result
    })
```

### With Context Access

```fsharp
let emailAssistant =
    Executor.create "EmailAssistant" (fun input ctx -> async {
        // Read from shared state
        let! email = ctx.ReadState<Email> input.EmailId

        // Call agent
        let! response = assistantAgent.Chat email.Content

        // Write to shared state
        do! ctx.WriteState input.EmailId { Response = response }

        return { Response = response }
    })
```

### From Existing Agent

```fsharp
// Wrap an existing AIAgent as an executor
let writerExecutor = Executor.fromAgent "Writer" writerAgent
```

---

## Routing Patterns

### Simple Condition

```fsharp
workflow {
    start detector

    when isValid   -> processor
    otherwise      -> errorHandler
}
```

### Multi-Way Branch (Pattern Match)

```fsharp
workflow {
    start classifier

    route (function
        | Category.Tech     -> techWriter
        | Category.Finance  -> financeWriter
        | Category.General  -> generalWriter)
}
```

### Guard Conditions

```fsharp
workflow {
    start analyzer

    route [
        when (fun r -> r.Score > 0.8)  -> highConfidence
        when (fun r -> r.Score > 0.5)  -> mediumConfidence
        otherwise                      -> lowConfidence
    ]
}
```

### Dynamic Fan-Out

```fsharp
workflow {
    start taskSplitter

    fanOut (fun task ->
        task.Subtasks |> List.map getWorkerFor)

    fanIn resultAggregator
}
```

---

## Type Definitions

```fsharp
/// An executor that transforms 'input to 'output
type Executor<'input, 'output> = {
    Name: string
    Execute: 'input -> WorkflowContext -> Async<'output>
}

/// A workflow definition (not yet built)
type WorkflowDef<'input, 'output> = {
    Start: Executor<'input, 'intermediate>
    Steps: WorkflowStep list
    Outputs: Executor list
}

/// Individual workflow steps
type WorkflowStep =
    | Sequential of Executor
    | Conditional of ('a -> Executor)
    | Parallel of Executor list
    | FanIn of Executor
    | Retry of count: int * backoff: BackoffStrategy
    | Timeout of TimeSpan
    | Fallback of Executor

/// Backoff strategies for retry
type BackoffStrategy =
    | Immediate
    | Linear of TimeSpan
    | Exponential of initial: TimeSpan * multiplier: float
```

---

## Compilation to MAF

The CE would compile down to MAF's `WorkflowBuilder`:

```fsharp
// This F# DSL:
let myWorkflow = workflow {
    start spamDetector
    route (function
        | Spam _ -> spamHandler
        | NotSpam -> emailAssistant)
}

// Compiles to something like:
let build () =
    let builder = WorkflowBuilder(spamDetectorExecutor)
    builder.AddEdge(spamDetectorExecutor, spamHandlerExecutor,
        condition = fun obj ->
            match obj :?> SpamDecision with
            | Spam _ -> true
            | _ -> false)
    builder.AddEdge(spamDetectorExecutor, emailAssistantExecutor,
        condition = fun obj ->
            match obj :?> SpamDecision with
            | NotSpam -> true
            | _ -> false)
    builder.Build()
```

The ugliness is hidden - users write beautiful F#, the CE generates the MAF ceremony.

---

## Example: Full Email Processing Workflow

```fsharp
open AgentNet.Workflow

// Domain types
type SpamResult = Spam of string | NotSpam | Uncertain
type EmailAnalysis = { Id: string; Content: string; Decision: SpamResult }

// Executors from agents
let spamDetector = Executor.fromAgent "SpamDetector" spamDetectorAgent
let emailAssistant = Executor.fromAgent "EmailAssistant" emailAssistantAgent
let spamHandler = Executor.fromFn "SpamHandler" (fun e -> $"Blocked: {e.Id}")
let uncertainHandler = Executor.fromFn "UncertainHandler" (fun e -> $"Review: {e.Id}")
let emailSender = Executor.fromAsync "EmailSender" sendEmailAsync

// The workflow - clean and declarative!
let emailWorkflow = workflow {
    start spamDetector

    route (fun analysis ->
        match analysis.Decision with
        | Spam reason -> spamHandler
        | NotSpam     -> emailAssistant |> andThen emailSender
        | Uncertain   -> uncertainHandler)

    outputs [ spamHandler; emailSender; uncertainHandler ]
}

// Run it
let result = emailWorkflow |> Workflow.run "Check this email..."
```

---

## Comparison: C# vs F#

### C# (MAF Native)
```csharp
private static Func<object?, bool> GetCondition(SpamDecision expected) =>
    result => result is SpamResult r && r.Decision == expected;

var workflow = new WorkflowBuilder(spamDetectionExecutor)
    .AddEdge(spamDetectionExecutor, emailAssistantExecutor,
        condition: GetCondition(SpamDecision.NotSpam))
    .AddEdge(emailAssistantExecutor, sendEmailExecutor)
    .AddEdge(spamDetectionExecutor, handleSpamExecutor,
        condition: GetCondition(SpamDecision.Spam))
    .AddEdge(spamDetectionExecutor, handleUncertainExecutor,
        condition: GetCondition(SpamDecision.Uncertain))
    .WithOutputFrom(handleSpamExecutor, sendEmailExecutor, handleUncertainExecutor)
    .Build();
```

### F# (AgentNet DSL)
```fsharp
let emailWorkflow = workflow {
    start spamDetector

    route (function
        | Spam _    -> spamHandler
        | NotSpam   -> emailAssistant => emailSender
        | Uncertain -> uncertainHandler)
}
```

**Lines of code: ~15 vs ~5**
**Ceremony: High vs None**
**Type safety: Runtime casts vs Compile-time matching**

---

## Open Questions

1. **Syntax for `andThen` / `=>`** - How to express sequential sub-flows within a branch?
2. **State management** - How to expose `WorkflowContext` elegantly?
3. **Error types** - Should we use `Result<'a, 'error>` throughout?
4. **Streaming** - How to handle MAF's `StreamingRun` pattern?
5. **Checkpointing** - Expose MAF's checkpoint/resume capability?

---

## Next Steps

1. [x] Prototype the `workflow` CE with basic `start` and `next`
2. [x] Add `route` with pattern matching support
3. [x] Implement `fanOut` / `fanIn` for fan-out/fan-in
4. [x] Add resilience (`retry`, `timeout`, `backoff`, `fallback`)
5. [ ] Build compilation layer to MAF's `WorkflowBuilder`
6. [ ] Test with real multi-agent scenarios
