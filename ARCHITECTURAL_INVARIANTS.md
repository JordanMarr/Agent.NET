# CLAUDE.md  

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Architectural invariants and non‑negotiable rules for AI collaborators

---

## 0. Purpose

This document defines the **architectural invariants** of Agent.NET / AgentNet.Durable.

These are **hard constraints**, not suggestions:

- If a change conflicts with these rules, the change is **invalid**, even if tests pass.
- If you are unsure whether a change violates an invariant, you must stop and ask for clarification instead of “fixing” it by improvisation.

This file exists so that future AI collaborators (including you, Claude) **preserve the architecture** instead of gradually rewriting it.

---

## 1. Core architecture

### 1.1 MAF is the execution engine

**Invariants:**

- **All workflow step execution goes through MAF.**
- MAF is responsible for:
  - **Step execution**
  - **Routing**
  - **Parallel branches**
  - **Resilience wrappers** (retries, timeouts, fallbacks, etc.)

**Non‑negotiable:**

- You **must not**:
  - Introduce a custom interpreter for workflow steps.
  - Execute steps “directly” in a loop instead of using MAF.
  - Bypass MAF when running workflows.

If you find yourself thinking, _“Let’s just interpret the steps ourselves,”_ stop. That is a violation of the architecture.

---

### 1.2 DurableTask is the suspension engine

DurableTask (DTFx) owns **suspension, replay, and resumption**.

**Invariants:**

- Durable orchestrators are **not** normal async functions.
- They are **deterministic workflows disguised as async**.
- Only DTFx is allowed to:
  - Suspend orchestrators.
  - Resume orchestrators.
  - Control which thread they run on.
  - Await durable primitives.

**Non‑negotiable:**

- The durable executor must **never**:
  - Await durable primitives.
  - Wrap durable primitives in `Task.Run`, `ContinueWith`, or other schedulers.
  - Create its own replay model.

DurableTask is the **only** suspension engine. MAF and Agent.NET must cooperate with it, not re‑implement it.

---

### 1.3 Workflow graph is declarative

Workflows are modeled as a **graph of steps** (the `WorkflowStep` union).

**Invariants:**

- The graph describes *what* should happen, not *how* it executes.
- No step should run at graph construction time.
- No side effects at graph construction time.
- Execution happens later, through:
  - MAF (in‑process execution), or
  - DurableTask + durable executor (durable orchestration).

**Non‑negotiable:**

- You must **not**:
  - Execute business logic when building the workflow.
  - Use graph construction to perform I/O or durable operations.
  - Mutate external state during graph construction.

The graph is a **pure description**, not a runtime.

---

## 2. Cold vs hot async

This is the heart of AgentNet.Durable’s design.

### 2.1 Cold async model (what we are)

Agent.NET workflows follow a **cold, declarative async model**, similar to F# `Async`:

- Steps represent **computation descriptions**, not running tasks.
- `AwaitEvent`, `Delay`, and other durable operations:
  - **describe** a suspension.
  - return durable primitives.
- DTFx decides when/where the actual wait and resume happen.

**Invariants:**

- A workflow step is **cold** until the runtime executes it.
- The durable executor must pass **declarative intentions** to DTFx, not run “hot” tasks.

### 2.2 Hot async is forbidden in orchestrators

“Hot” async (e.g., C# `Task` started immediately, resumed on thread pool) is **not allowed** inside durable orchestration.

**Forbidden in durable execution:**

- `Task.Run(...)`
- `Task.Delay(...)`
- `ContinueWith(...)`
- `Task.WhenAll(...)` on **non‑durable** tasks
- Any custom scheduler that moves work off DTFx’s orchestrator thread
- Awaiting **non‑durable** tasks inside the orchestrator

If you think, _“I’ll just use a continuation,”_ that is almost certainly illegal here.

---

## 3. Durable boundaries

This is where most subtle bugs appear. These rules are strict.

### 3.1 AwaitEvent

`AwaitEvent` is the core bridge between the workflow graph and DTFx external events.

**Correct model:**

- The CE builds a node that says:
  - “At this point, call `ctx.WaitForExternalEvent<'T>(eventName)`.”
- The durable executor:
  - Calls that method.
  - Returns the resulting **durable primitive** to DTFx.
- DTFx owns suspension and resumption.

**Invariants:**

- `AwaitEvent` must:
  - Create a function that, given `TaskOrchestrationContext`, returns `Task<'T>` / `Task` from **DurableTask primitives only**.
  - **Not** await inside this function.
  - **Not** wrap in `Task<obj>` via continuations.

**Forbidden patterns (do not introduce these):**

```fsharp
// ❌ Illegal: awaiting inside the durable executor
let exec = fun ctxObj -> task {
    let ctx = ctxObj :?> TaskOrchestrationContext
    let! result = ctx.WaitForExternalEvent<'T>(eventName)
    return box result
}
```

```fsharp
// ❌ Illegal: using ContinueWith + reflection
let fn = Func<obj, Task<obj>>(fun _ ->
    let durableTask = invoke ctx
    durableTask.ContinueWith(fun (t: Task) ->
        let resultProp = t.GetType().GetProperty("Result")
        resultProp.GetValue(t))
)
```

**Allowed pattern (target):**

```fsharp
// ✅ CE side – build node with invoke returning durable primitive
let invoke = fun (ctxObj: obj) ->
    let ctx = ctxObj :?> TaskOrchestrationContext
    ctx.WaitForExternalEvent<'T>(eventName) :> Task

AwaitEvent(durableId, invoke)
```

The durable executor may adapt the delegate shape to MAF, but **must not**:

- Await the task.
- Wrap it in non‑durable continuations.
- Extract results via reflection.

If you need to **change the representation** of `AwaitEvent`, stop and ask for human review.

---

### 3.2 Delay

`Delay` uses durable timers.

**Invariants:**

- Must use `ctx.CreateTimer(...)` from DTFx.
- Must not call `Task.Delay`, `Thread.Sleep`, or similar.
- Must not wrap the durable timer in custom schedulers or continuations.

**Forbidden pattern:**

```fsharp
// ❌ Do not do this inside a durable orchestration
do! Task.Delay(duration)
```

**Preferred pattern:**

```fsharp
// ✅ Use DTFx timer
let fireAt = ctx.CurrentUtcDateTime.Add(duration)
do! ctx.CreateTimer(fireAt, CancellationToken.None)
```

---

### 3.3 No custom durable interpreters

If you find yourself drafting something like:

> “Let’s ignore MAF and just loop over `workflow.Steps` manually, calling `executeStepDurable`…”

That is a **hard violation**.

**Non‑negotiable:**

- Do **not**:
  - Write a custom “durable interpreter” that walks `workflow.Steps`.
  - Replace MAF execution with your own loop.
  - Implement a new mini runtime inside `Workflow.Durable.fs`.

MAF remains the primary executor. Durable integration must be layered **into** that model, not replace it.

If you believe MAF’s model is incompatible with a needed feature, stop and escalate; do not route around it.

---

## 4. Reflection rules

### 4.1 Reflection is forbidden in durable execution

**Forbidden in durable execution path:**

- `t.GetType().GetProperty("Result")`
- `resultProp.GetValue(t)`
- Any `Type.GetType`, `GetProperty`, `GetMethod`, or similar that:
  - inspects tasks at runtime,
  - extracts generic results via reflection,
  - decides behavior based on runtime type shape.

These were explicitly removed and must not return.

### 4.2 Where reflection is allowed

Reflection is allowed **only** in:

- Validation at graph construction time (e.g., in the CE builder).
- Non‑durable paths where determinism and replay are not required.

Examples of allowed reflection:

- Ensuring event types are public, non‑abstract.
- Validating that types meet specific constraints **before** execution.

Never introduce new runtime reflection in the durable execution path.

---

## 5. Executor rules

### 5.1 MAF executor (InProcess)

The in‑process execution uses MAF’s execution model.

**Responsibilities:**

- Execute pure steps.
- Handle parallelism, routing, retries, timeouts, fallbacks, etc.
- Emit appropriate events.

**Invariants:**

- It may use normal .NET tasks, async, parallelism as needed.
- It must not pretend to be DurableTask.
- It must not attempt deterministic replay.

### 5.2 Durable executor

The durable executor integrates the workflow graph with DTFx.

**Responsibilities:**

- Translate certain nodes (e.g., `AwaitEvent`, `Delay`) into DTFx primitives.
- Ensure orchestration code remains deterministic and replay‑safe.
- Respect DTFx’s threading and replay rules.

**Invariants:**

- Must **not** await durable primitives itself.
- Must **return** durable primitives to DTFx.
- Must **not**:
  - use `Task.Run`, `ContinueWith`, `Task.Delay` in orchestrator logic,
  - run user code on arbitrary thread pool threads in orchestrator context.

If you need to adapt a function shape (e.g., to fit MAF’s `Func<obj, Task<obj>>`), do so **without** introducing continuations or reflection.

---

## 6. Forbidden operations (checklist)

The following are **red flags**. If you introduce any of these in durable orchestration code, the change is almost certainly invalid.

- `ContinueWith` on any task used in orchestration.
- `Task.Run` used anywhere near a durable step.
- `Task.Delay` instead of DTFx timers.
- `Task.WhenAll` on non‑durable tasks in orchestrator logic.
- `GetType().GetProperty("Result")` or other reflection to read task results.
- Walking `workflow.Steps` manually to “execute” them.
- Removing or bypassing MAF and replacing it with a direct interpreter.
- Awaiting durable primitives inside the durable executor instead of returning them.
- Calling business logic directly from the orchestrator instead of using the established workflow/MAF execution path.

If you touch any of these areas, you must **stop and ask**.

---

## 7. Extension points (safe areas)

It is generally safe to:

- Add **new workflow nodes** that:
  - are pure,
  - fit into the existing `WorkflowStep` model,
  - do not perform durable operations directly.
- Add **new CE operations** that:
  - build graph nodes,
  - perform type validation only,
  - do not execute logic.
- Add **new durable primitives** (like additional `WaitFor*` patterns) as long as they:
  - return DTFx primitives directly,
  - respect all rules above,
  - do not introduce hot async.
- Extend MAF integration in ways that:
  - preserve MAF as the execution engine,
  - do not add reflection or hot async in durable paths.

---

## 8. Non‑extension points (ask first)

You must **not** modify these without explicit human approval and a clear design:

- The fundamental structure of `WorkflowStep`.
- The cold async / declarative model.
- How MAF is integrated as the executor.
- How DTFx is integrated as the suspension engine.
- The durable boundaries (AwaitEvent, Delay, etc.).
- Any core durable orchestrator behavior.

If a change requires you to alter these, stop and ask for a human decision.

---

## 9. If you are unsure

If you are modifying code in:

- `AgentNet.Durable`
- durable orchestration glue
- workflow execution paths
- anything involving `TaskOrchestrationContext`

…and you’re not 100% sure your change respects:

- cold async semantics  
- MAF as executor  
- DurableTask as suspension engine  
- no reflection in durable execution  
- no hot async in orchestrators  

then:

1. Stop.
2. Explain what you’re trying to achieve.
3. Ask for confirmation or a target design before proceeding.

---

If you’d like, next step we can also add a short **“Philosophy”** appendix that explains in human terms why these rules exist (F# cold async + DTFx determinism + MAF ergonomics), but this version is already strong enough to act as a guardrail.

Do you want to tune any wording (e.g., make it more “Jordan voice”), or add a brief “Quick summary for Claude” section at the top as a TL;DR?