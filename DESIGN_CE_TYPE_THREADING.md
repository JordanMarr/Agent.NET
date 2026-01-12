# **DESIGN_CE_TYPE_THREADING.md**  
### *Authoritative spec for implementing the Agent.NET workflow computation expression*

This document defines how the Agent.NET workflow computation expression (CE) must thread generic type parameters to enforce the architectural invariants in `AGENTNET_ARCHITECTURE.md`.

It is written for AI collaborators (Claude, Copilot, etc.).  
If you are modifying the CE builder, you must follow this document exactly.

---

# **0. Purpose**

The CE builder must enforce, at compile time:

1. **Typed pipeline invariant**  
   Every step’s output type must match the next step’s input type.

2. **Event boundary invariant**  
   `awaitEvent` can only appear when the current output type is `unit`.

3. **No wildcard or erased phantom types**  
   The CE must never use `_`, `obj`, or any erased type in `WorkflowState<'input,'output>`.

4. **Context is a runtime concern**  
   The CE must not encode context usage in phantom types.

If the CE is implemented correctly, **ill‑typed workflows will not compile**.

---

# **1. WorkflowState phantom type**

All CE operations thread this type:

```fsharp
type WorkflowState<'input, 'output> =
    private {
        Steps: WorkflowStep list
        // additional fields as needed
    }
```

Rules:

- `'input` is fixed for the entire workflow.
- `'output` is the current output type after the last step.
- No CE operation may erase or ignore `'output`.

---

# **2. CE builder skeleton**

The builder must have this shape:

```fsharp
type WorkflowBuilder() =

    member _.Zero() : WorkflowState<'input, 'input> = ...

    member _.Yield(x: 'a) : WorkflowState<'input, 'a> = ...

    member _.Delay(f: unit -> WorkflowState<'input, 'a>) : WorkflowState<'input, 'a> =
        f ()

    member _.Combine(
        s1: WorkflowState<'input, 'a>,
        s2: WorkflowState<'input, 'b>
    ) : WorkflowState<'input, 'b> = ...

    member _.Run(state: WorkflowState<'input, 'output>) : WorkflowState<'input, 'output> =
        state
```

Notes:

- If you add `For`, `While`, or other CE members, they must follow the same phantom‑type discipline.
- No CE member may use `WorkflowState<'input, _>`.

---

# **3. Step operation (core type threading)**

### **Correct signature**

```fsharp
[<CustomOperation("step")>]
member _.Step
    (
        state: WorkflowState<'input, 'a>,
        executor: Executor<'a, 'b>
    ) : WorkflowState<'input, 'b> =
    ...
```

This is the heart of the typed pipeline.

Rules:

- Input state must be `WorkflowState<'input,'a>`.
- Output state must be `WorkflowState<'input,'b>`.
- `'a` and `'b` must be real type parameters.
- No wildcard or erased type is allowed.

### **Executors**

All executors have the same shape:

```fsharp
type Executor<'a,'b> =
    { Name: string
      Execute: 'a -> WorkflowContext -> Task<'b> }
```

The CE must treat all executors identically:

- Pure executors (`fromFn`, `fromTask`, etc.)  
- Context‑aware executors (`create`)  
- Typed agents (`fromTypedAgent`)

The CE must **not** inspect or special‑case context usage.

---

# **4. awaitEvent (event boundary invariant)**

This is the most important operation in the CE.

### **Correct signature**

```fsharp
[<CustomOperation("awaitEvent")>]
member _.AwaitEvent
    (
        state: WorkflowState<'input, unit>,
        event: EventDefinition<'event>
    ) : WorkflowState<'input, 'event> =
    ...
```

Rules:

- Input state **must** be `WorkflowState<'input, unit>`.
- Output state is `WorkflowState<'input, 'event>`.
- No overload may accept any other `'output` type.
- No wildcard or erased type is allowed.

### **Why this matters**

This enforces:

- The step before an event must return `unit`.
- Any data needed after the event must be stored in context.
- Event boundaries reset the type flow.

If the user writes:

```fsharp
step analyzeStock              // 'a -> StockAnalysis
awaitEvent tradeApprovedEvent  // requires unit
```

The compiler must reject it.

---

# **5. Context usage (runtime concern)**

The CE must not encode context usage in phantom types.

Context is accessed only through executors:

```fsharp
Executor.create "Store" (fun input ctx -> ...)
```

The CE must:

- Allow context‑aware executors anywhere.
- Not require context‑aware executors before events.
- Not track context in phantom types.

Type flow and context flow are orthogonal.

---

# **6. CE combinator rules**

### **6.1 Delay**

Must preserve the same phantom type:

```fsharp
member _.Delay(f: unit -> WorkflowState<'input,'a>) : WorkflowState<'input,'a>
```

### **6.2 Combine**

Must preserve the output type of the second state:

```fsharp
member _.Combine(
    s1: WorkflowState<'input,'a>,
    s2: WorkflowState<'input,'b>
) : WorkflowState<'input,'b>
```

### **6.3 Zero**

Identity workflow:

```fsharp
member _.Zero() : WorkflowState<'input,'input>
```

### **6.4 Yield**

If implemented:

```fsharp
member _.Yield(x: 'a) : WorkflowState<'input,'a>
```

### **6.5 No combinator may erase or widen phantom types**

Forbidden:

```fsharp
WorkflowState<'input,_>
WorkflowState<'input,obj>
WorkflowState<'input,unit>  // unless semantically required (awaitEvent)
```

---

# **7. Anti‑patterns (forbidden)**

### ❌ Wildcards in WorkflowState

```fsharp
WorkflowState<'input, _>
```

### ❌ Erasing output type to obj

```fsharp
WorkflowState<'input, obj>
```

### ❌ Resetting phantom type without semantics

```fsharp
WorkflowState<'input, 'a> -> WorkflowState<'input, 'input>
```

unless explicitly documented (e.g., `Zero`).

### ❌ Allowing awaitEvent to accept any output type

```fsharp
WorkflowState<'input, _>  // forbidden
```

### ❌ Special‑casing context usage in the CE

The CE must not inspect or enforce context usage.

---

# **8. Expected workflow shape**

When implemented correctly:

```fsharp
workflow {
    step analyzeStock              // TradeRequest -> StockAnalysis
    step (Executor.create "Store" (fun analysis ctx ->
        ctx.Set("analysis", analysis)
        Task.FromResult(())
    ))                              // StockAnalysis -> unit
    awaitEvent tradeApprovedEvent   // unit -> ApprovalDecision
    step executeTrade               // ApprovalDecision -> TradeResult
}
```

If the user writes:

```fsharp
workflow {
    step analyzeStock
    awaitEvent tradeApprovedEvent
}
```

The compiler must reject it because the current output type is `StockAnalysis`, not `unit`.

---

# **9. Summary for implementers (Claude)**

When implementing or modifying the CE builder:

- **Always** thread `WorkflowState<'input,'output>` exactly.
- **Never** use `_` or `obj` in phantom types.
- **step** must refine `'a -> 'b`.
- **awaitEvent** must require `'output = unit`.
- **All CE combinators** must preserve phantom types.
- **Do not** encode context usage in phantom types.
- **Do not** widen or erase types.
- **Do not** introduce new CE members without following these rules.

If you are unsure about any signature, **stop and ask**.

---
