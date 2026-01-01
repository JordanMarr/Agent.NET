# AgentNet Result Workflow DSL Design

## Vision

Extend the `workflow` DSL with Railway-Oriented Programming (ROP) support, enabling F#-idiomatic error handling that:
- Automatically unwraps `Result<'T, 'Error>` values between steps
- Short-circuits on `Error`, skipping remaining steps
- Composes cleanly with existing `workflow` definitions
- Provides an alternative to MAF's exception-based error handling

## Design Goals

1. **Railway-Oriented Programming** - Happy path flows through; errors short-circuit
2. **Implicit Unwrapping** - Both `Async` and `Result` are automatically unwrapped
3. **Flexible Executor Creation** - Support map/bind semantics for different use cases
4. **Composable** - Nest workflows within workflows, mixing `workflow` and `resultWorkflow`
5. **Zero Dependencies** - No external libraries required (other than MAF)

---

## Core Syntax

### Simple Sequential Result Workflow

```fsharp
let documentWorkflow = resultWorkflow {
    start parseDocument       // returns Result<Document, WorkflowError>
    next validateDocument     // receives Document, returns Result
    next enrichDocument       // receives Document, returns Result
    next saveDocument         // receives Document, returns Result
}
// Execution returns: Async<Result<SavedDocument, WorkflowError>>
```

Each step receives the **unwrapped** value from the previous step. If any step returns `Error`, the workflow short-circuits immediately.

### Mixing Map and Bind Semantics

```fsharp
// Bind: function returns Result (can fail)
let validate = ResultExecutor.bind "Validate" (fun doc ->
    if doc.IsValid then Ok doc
    else Error (ValidationError "Invalid document"))

// Map: function returns plain value (always succeeds, wrapped in Ok)
let enrich = ResultExecutor.map "Enrich" (fun doc ->
    { doc with Metadata = generateMetadata doc })

// Both work seamlessly in the same workflow
let workflow = resultWorkflow {
    start parseDocument
    next validate           // bind semantics
    next enrich             // map semantics (auto-wrapped in Ok)
    next saveDocument
}
```

### Async Variations

```fsharp
// All four combinations are supported:

// Sync + no Result (map)
let transform = ResultExecutor.map "Transform" (fun x -> x.ToUpper())

// Sync + Result (bind)
let validate = ResultExecutor.bind "Validate" (fun x ->
    if isValid x then Ok x else Error ValidationFailed)

// Async + no Result (mapAsync)
let fetch = ResultExecutor.mapAsync "Fetch" (fun id -> async {
    let! data = httpClient.GetAsync id
    return data
})

// Async + Result (bindAsync)
let process = ResultExecutor.bindAsync "Process" (fun data -> async {
    let! result = riskyOperation data
    return if result.Success then Ok result.Value else Error result.Error
})
```

All are normalized internally to `Async<Result<'output, 'error>>`.

---

## ResultExecutor Module

### Creation Functions

```fsharp
module ResultExecutor =
    /// Map: 'input -> 'output (wrapped in Ok)
    let map (name: string) (fn: 'input -> 'output)
        : ResultExecutor<'input, 'output, 'error>

    /// Bind: 'input -> Result<'output, 'error> (used directly)
    let bind (name: string) (fn: 'input -> Result<'output, 'error>)
        : ResultExecutor<'input, 'output, 'error>

    /// MapAsync: 'input -> Async<'output> (wrapped in Ok)
    let mapAsync (name: string) (fn: 'input -> Async<'output>)
        : ResultExecutor<'input, 'output, 'error>

    /// BindAsync: 'input -> Async<Result<'output, 'error>> (used directly)
    let bindAsync (name: string) (fn: 'input -> Async<Result<'output, 'error>>)
        : ResultExecutor<'input, 'output, 'error>

    /// From an existing Agent (map semantics - agents don't fail with Result)
    let fromAgent (name: string) (agent: Agent)
        : ResultExecutor<string, string, 'error>
```

### Normalization Table

| Creation Function | Input Signature | Internal Signature |
|-------------------|-----------------|-------------------|
| `map` | `'i -> 'o` | `'i -> Async<Result<'o, 'e>>` |
| `bind` | `'i -> Result<'o, 'e>` | `'i -> Async<Result<'o, 'e>>` |
| `mapAsync` | `'i -> Async<'o>` | `'i -> Async<Result<'o, 'e>>` |
| `bindAsync` | `'i -> Async<Result<'o, 'e>>` | `'i -> Async<Result<'o, 'e>>` |

This mirrors how the existing `Executor` module normalizes sync functions to `Async<'output>`.

---

## Workflow Composition

### resultWorkflow inside resultWorkflow

```fsharp
let parsing = resultWorkflow {
    start readFile
    next parseJson
    next validateSchema
}

let processing = resultWorkflow {
    start (ResultWorkflow.toExecutor "Parsing" parsing)  // embedded!
    next transformData      // receives unwrapped value
    next saveResult
}
```

The parent `resultWorkflow` automatically unwraps the child's `Result`.

### workflow inside resultWorkflow

```fsharp
let legacyWorkflow = workflow {
    start step1
    next step2
}
// Returns: Async<'output>

let modernWorkflow = resultWorkflow {
    start prepare
    next (Workflow.toResultExecutor "Legacy" legacyWorkflow)  // wrapped in Ok
    next validate
}
```

The `Workflow.toResultExecutor` function wraps the output in `Ok`.

### resultWorkflow inside workflow

```fsharp
let resultPart = resultWorkflow {
    start validate
    next process
}
// Returns: Async<Result<'output, 'error>>

let mainWorkflow = workflow {
    start prepare
    next (ResultWorkflow.toExecutor "ResultPart" resultPart)
    next finalize   // receives Result<'output, 'error> - handle manually!
}
```

The parent `workflow` is Result-agnostic, so it passes `Result<T, E>` through as a value.

### Composition Summary

| Parent CE | Child CE | Conversion Function | Next Step Receives |
|-----------|----------|--------------------|--------------------|
| `resultWorkflow` | `resultWorkflow` | `ResultWorkflow.toExecutor` | Unwrapped value |
| `resultWorkflow` | `workflow` | `Workflow.toResultExecutor` | Unwrapped value |
| `workflow` | `workflow` | `Workflow.toExecutor` | Unwrapped value |
| `workflow` | `resultWorkflow` | `ResultWorkflow.toExecutor` | `Result<T, E>` |

---

## Type Definitions

```fsharp
/// An executor that transforms 'input to Result<'output, 'error>
type ResultExecutor<'input, 'output, 'error> = {
    Name: string
    Execute: 'input -> WorkflowContext -> Async<Result<'output, 'error>>
}

/// A result workflow definition
type ResultWorkflowDef<'input, 'output, 'error> = {
    Steps: ResultWorkflowStep<'error> list
}

/// Individual result workflow steps
type ResultWorkflowStep<'error> =
    | Step of name: string * execute: (obj -> WorkflowContext -> Async<Result<obj, 'error>>)
    | Route of router: (obj -> WorkflowContext -> Async<Result<obj, 'error>>)
    | Parallel of executors: (obj -> WorkflowContext -> Async<Result<obj, 'error>>) list
    | Resilient of settings: ResilienceSettings * inner: ResultWorkflowStep<'error>
```

---

## Execution Model

### Workflow.run vs ResultWorkflow.run

```fsharp
// Regular workflow
Workflow.run : 'input -> WorkflowDef<'input, 'output> -> Async<'output>

// Result workflow
ResultWorkflow.run : 'input -> ResultWorkflowDef<'input, 'output, 'error>
                   -> Async<Result<'output, 'error>>
```

### Execution Loop (Pseudocode)

```fsharp
let run input workflow =
    async {
        let ctx = WorkflowContext.create()
        let mutable current: obj = box input

        for step in workflow.Steps do
            let! result = executeStep step current ctx
            match result with
            | Ok value -> current <- value      // unwrap and continue
            | Error e -> return Error e         // short-circuit!

        return Ok (unbox current)
    }
```

---

## Error Type Considerations

### Unified Error Type

All steps in a `resultWorkflow` must share the same error type `'error`:

```fsharp
type WorkflowError =
    | ParseError of string
    | ValidationError of string
    | NetworkError of exn
    | Timeout

let workflow = resultWorkflow {
    start parseStep       // Result<_, WorkflowError>
    next validateStep     // Result<_, WorkflowError>
    next fetchStep        // Result<_, WorkflowError>
}
```

### Error Type Conversion

If steps have different error types, convert at the executor level:

```fsharp
let fetchStep = ResultExecutor.bindAsync "Fetch" (fun input -> async {
    let! result = httpFetch input
    return result |> Result.mapError (fun e -> NetworkError e)
})
```

---

## Comparison with workflow CE

| Aspect | `workflow` | `resultWorkflow` |
|--------|-----------|------------------|
| Async unwrapping | Implicit | Implicit |
| Result unwrapping | Manual | Implicit |
| Short-circuit on Error | No | Yes |
| Return type | `Async<'output>` | `Async<Result<'output, 'error>>` |
| Error handling | Exceptions / manual | Railway-oriented |

---

## Comparison with FsToolkit.ErrorHandling

| Aspect | FsToolkit `asyncResult` | AgentNet `resultWorkflow` |
|--------|------------------------|---------------------------|
| Unwrap mechanism | `let!` / `let` | Implicit (all steps unwrap) |
| Composition | Via `let!` bindings | Via custom operations |
| Executor model | N/A | First-class `ResultExecutor` |
| Workflow semantics | General purpose | Tailored for agent pipelines |
| Dependencies | External package | None (self-contained) |

---

## Example: Document Processing Pipeline

```fsharp
open AgentNet.ResultWorkflow

// Define error types
type DocError =
    | ParseFailed of string
    | ValidationFailed of string
    | EnrichmentFailed of string
    | SaveFailed of string

// Define executors
let parseDocument = ResultExecutor.bind "Parse" (fun (stream: Stream) ->
    try
        Ok (JsonDocument.Parse stream)
    with ex ->
        Error (ParseFailed ex.Message))

let validateDocument = ResultExecutor.bind "Validate" (fun doc ->
    if doc.RootElement.TryGetProperty("id", &_)
    then Ok doc
    else Error (ValidationFailed "Missing 'id' field"))

let enrichDocument = ResultExecutor.mapAsync "Enrich" (fun doc -> async {
    let! metadata = fetchMetadata doc
    return doc.AddMetadata metadata
})

let saveDocument = ResultExecutor.bindAsync "Save" (fun doc -> async {
    try
        do! database.Save doc
        return Ok doc.Id
    with ex ->
        return Error (SaveFailed ex.Message)
})

// The workflow - clean and declarative!
let documentPipeline = resultWorkflow {
    start parseDocument
    next validateDocument
    next enrichDocument
    next saveDocument
}

// Run it
let processDocument (stream: Stream) = async {
    match! ResultWorkflow.run stream documentPipeline with
    | Ok docId -> printfn $"Saved document: {docId}"
    | Error e -> printfn $"Failed: {e}"
}
```

---

## Implementation Notes

### No External Dependencies

We implement minimal Result helpers ourselves:

```fsharp
module internal Result =
    let map f = function Ok x -> Ok (f x) | Error e -> Error e
    let bind f = function Ok x -> f x | Error e -> Error e
    let mapError f = function Ok x -> Ok x | Error e -> Error (f e)

module internal AsyncResult =
    let map f ar = async {
        let! r = ar
        return Result.map f r
    }
    let bind f ar = async {
        let! r = ar
        match r with
        | Ok x -> return! f x
        | Error e -> return Error e
    }
```

This keeps the library self-contained (~15 lines of code).

---

## Next Steps

1. [ ] Implement `ResultExecutor` module with `map`, `bind`, `mapAsync`, `bindAsync`
2. [ ] Implement `ResultWorkflowBuilder` CE with `start`, `next`, `route`, `fanOut`, `fanIn`
3. [ ] Implement `ResultWorkflow.run` and `ResultWorkflow.runSync`
4. [ ] Add `ResultWorkflow.toExecutor` for composition into `resultWorkflow`
5. [ ] Add `Workflow.toResultExecutor` for embedding `workflow` into `resultWorkflow`
6. [ ] Add resilience modifiers (`retry`, `timeout`, `backoff`, `fallback`)
7. [ ] Test with real multi-agent scenarios
8. [ ] Document error handling patterns and best practices
