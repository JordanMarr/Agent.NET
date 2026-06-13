# **ARCHITECTURAL_INVARIANTS.md**
### *Architectural invariants and non‑negotiable rules for AI collaborators*

---

## **TL;DR for AI collaborators (read this first)**

Agent.NET has a strict architectural model. These rules are **not optional**. If a proposed change
violates them, the change is invalid even if it compiles, even if tests pass, and even if it "seems to
work." **Do not improvise.** If you are unsure, stop and ask.

### **Core truths you must preserve**

- **MAF is the execution engine.**
  All workflow execution goes through Microsoft Agent Framework. A workflow is compiled to a MAF
  `WorkflowBuilder` graph via `toMAF` and run by MAF — in-process today, and durably via MAF's
  checkpoint/resume model. No custom interpreters. No loops over steps that bypass MAF.

- **The workflow graph is declarative.**
  Building a workflow must never run business logic or perform I/O. The `WorkflowDef` is a pure
  description; nothing executes at construction time.

- **Cold async.**
  Steps describe work; they do not run work until MAF executes them.

- **Typed flow is enforced.**
  The CE builder guarantees each step's output type matches the next step's input type. Ill‑typed
  workflows must fail at **compile time**. (Spec: `DESIGN_CE_TYPE_THREADING.md`.)

- **Event boundaries reset the type flow.**
  The step before `awaitEvent` must return `unit`. Any data needed after the boundary must flow through
  the graph or be stored explicitly — it is not carried implicitly across a suspension point.

> **⚠️ Suspension model is mid-migration.** The durable suspension/resume engine is being reworked from
> Azure Durable Functions (DTFx) to **MAF-native checkpointing**. During the `durable-rework` branch,
> **`DURABLE_REWORK_PLAN.md` is the authority** for anything touching durable execution. The sections
> below describe the target model; the code may not match yet.

---

# **0. Purpose**

This document defines the **architectural invariants** of Agent.NET. These are **hard constraints**, not
suggestions: if a change conflicts with them, the change is invalid even if tests pass. If you are unsure
whether a change violates an invariant, stop and ask rather than "fixing" it by improvisation.

It exists so that future collaborators (human or AI) **preserve the architecture** instead of gradually
rewriting it.

---

# **1. MAF is the execution engine**

**Invariants:**

- All workflow step execution goes through MAF.
- MAF is responsible for: step execution, routing, parallel branches, and resilience wrappers
  (retries, timeouts, fallbacks).
- Both execution modes share **one** compilation (`toMAF`):
  - **In-process** — `InProcessExecution` runs the graph to completion.
  - **Durable** — the same graph run with a MAF `CheckpointManager`, halting at request ports and
    resuming from a persisted checkpoint.

**Non‑negotiable — you must not:**

- Introduce a custom interpreter for workflow steps.
- Execute steps directly in a loop instead of using MAF.
- Maintain a separate, divergent execution path for durable workflows. (The pre-rework bespoke durable
  runtime is exactly what is being removed — do not reintroduce it.)

---

# **2. The workflow graph is declarative (cold async)**

Workflows are modeled as a typed pipeline of packed steps (`WorkflowDef` / `PackedTypedStep`).

**Invariants:**

- The graph describes *what* should happen, not *how* it executes.
- No step runs at graph construction time. No side effects, no I/O at construction time.
- Execution happens later, through MAF.

**Non‑negotiable — you must not:**

- Execute business logic when building the workflow.
- Mutate external state during graph construction.

---

# **3. Typed flow & event boundaries**

## **3.1 Typed flow invariant**

The output type of each step must match the input type of the next. The CE builder enforces this
statically using phantom types (and SRTP for step conversion). Ill‑typed workflows must fail at
**construction time**, not runtime. The authoritative spec is `DESIGN_CE_TYPE_THREADING.md` — do not
weaken the phantom-type threading or erase types to `obj`.

## **3.2 Event boundary invariant**

`awaitEvent` is a suspension point. It does not carry forward the previous step's output.

**Invariants:**

- The step immediately preceding `awaitEvent` **must return `unit`** — enforced by the CE signature.
- `awaitEvent` has the effective shape `unit -> 'EventPayload`.
- Data that must survive across the boundary must flow through the graph or be written to context
  before the boundary and read back after — never carried implicitly through step outputs.

---

# **4. Suspension model — MAF checkpoint/resume (target)**

> Authority during the rework: `DURABLE_REWORK_PLAN.md`.

Durable execution uses MAF's checkpoint/resume model, **not** orchestrator replay:

- **Checkpointing** serializes inter-step data + executor state to an `ICheckpointStore` at superstep
  boundaries. Step *code* is not serialized.
- **`awaitEvent`** compiles to a MAF `RequestPort`: the workflow emits a request, halts, and is
  checkpointed. The same node works in-process (respond programmatically) and durably (persist + resume).
- **Resume** rebuilds the workflow graph and calls `ResumeAsync(workflow, checkpointInfo)` with the
  external response. Because the `WorkflowDef` is a pure description, the resuming process reconstructs an
  identical graph; MAF restores state onto it.
- **`delayFor`** is an in-process delay (`Task.Delay`); combined with a checkpoint it survives a crash
  with "re-run the delay" semantics. There is no managed absolute-time timer (MAF has no scheduler).

**Determinism requirement:** durable IDs must be **stable across processes** so a rebuilt graph matches
its checkpoint. Lambdas produce unstable auto-IDs and are therefore disallowed (error) on the durable
build path; use named functions.

---

# **5. Dependency injection**

- Steps receive a `WorkflowContext` carrying `Services: IServiceProvider`.
- Steps that need dependencies resolve them at execution time
  (`ctx.Services.GetRequiredService<_>()`) — they do **not** capture dependencies in closures.
- This keeps the `WorkflowDef` free of captured deps, so cross-process resume only needs the host to
  supply the same provider. The host supplies `Services` at both run and resume.
- `Tool.inject` is unrelated and stays scoped to ChatAgent tools; there is no `Step.inject`.

---

# **6. If you are unsure**

Stop and ask. Do not improvise a change to execution, suspension, workflow construction, or the CE type
threading. Re-read this document and `DURABLE_REWORK_PLAN.md` first.
