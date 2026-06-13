# Durable Rework Plan — MAF-Native Durability

### *Living working document for the `durable-rework` branch*

> **Status:** Planning. No code changed yet. Update the Progress Log at the bottom as work lands.
>
> **API references in this doc were validated against MAF Workflows `1.3.0`.** The checkpoint /
> `RequestPort` surface is the area MAF has changed most. Anything marked 🔎 must be re-confirmed
> against the upgraded version (`1.10.0+`) in Phase 1 before the design that depends on it is locked.

---

## 1. Goal & Decision

Rework `AgentNet.Durable` from its bespoke runtime to **MAF-native durability**, unifying it onto
the same `toMAF` compilation the in-process path already uses. **Azure Durable Functions and the
`Microsoft.DurableTask` dependency are dropped entirely.**

### Why

- The current "durable" runtime is **not actually durable**. `DurableExecutorFactory.CreateStepExecutor`
  (`src/AgentNet.Durable.Interop/DurableExecutors.cs:83`) runs step bodies *inline* in the orchestrator —
  no `CallActivityAsync`, no memoization, and re-executing step bodies on replay violates DF's determinism
  contract. `Workflow.Durable.run` is just a `for` loop over those executors. Only `awaitEvent`
  (`WaitForExternalEvent`) and `delayFor` (`CreateTimer`) were genuinely durable.
- **Azure Durable Functions can't serialize F# DUs** — its serializer is closed and non-customizable.
  MAF's checkpoint path takes our own `JsonSerializerOptions`, so F# DUs round-trip via `JsonFSharpConverter`.
- The F# community dislikes DF's implicit "magic"; MAF-native is explicit and was the original intent.
  The bespoke runtime was a Copilot-led design from when MAF was still alpha.

---

## 2. The Architectural Shift

These are **two different durability paradigms**. The rework swaps one for the other.

| | Azure Durable Functions (old) | MAF Workflows (new) |
|---|---|---|
| Mechanism | Orchestrator **replay** from event history | **Checkpoint** run state at superstep boundaries |
| Step durability | Activities memoized in history | Inter-step data + executor state serialized to a store |
| Human-in-loop | `ctx.WaitForExternalEvent` | `RequestPort` → halt → `RequestInfoEvent` |
| Resume | Implicit via replay | Explicit `ResumeAsync(workflow, checkpointInfo)` + response |
| Persistence | Managed by Azure | We supply an `ICheckpointStore` (in-mem / filesystem / custom) |
| Serializer | Closed, no F# DUs | `System.Text.Json` + our `JsonSerializerOptions` (F# DUs ✅) |

**Key consequence:** "plug into MAF" and "keep `awaitEvent` on `WaitForExternalEvent`" are mutually
exclusive. Going MAF-native means `awaitEvent` becomes a `RequestPort`, and `TaskOrchestrationContext`
disappears.

**What crosses the resume boundary:** only the *data* flowing between steps + executor state (JSON).
Step *code* is **not** serialized — on resume we pass a freshly rebuilt `Workflow` to `ResumeAsync`;
MAF restores state onto a graph we reconstruct.

---

## 3. Locked Design Decisions

1. **DI via `WorkflowContext.Services`.** Add `Services: IServiceProvider` to `WorkflowContext`
   (`src/AgentNet/Executor.fs:7`). The host supplies it at both run and resume; steps resolve deps at
   execution time (`ctx.Services.GetRequiredService<_>()`). The `WorkflowDef` captures no deps → it is
   pure → resume is trivial. `WorkflowContext` is already threaded into every step, so this is additive.
   - **Not doing:** no `Step.inject`. `Tool.inject` stays scoped to ChatAgent tools (it exists only
     because F#-functions-as-tools need partial application; steps already receive `ctx`).

2. **`awaitEvent` → MAF `RequestPort`.** 🔎 Compiles to a request-port node instead of the
   durable-only `AwaitEvent` that currently throws under `InProcess.run` (`src/AgentNet/Workflow.fs:195`).
   **Bonus:** the same node now works in *both* execution modes — in-process you supply responses
   programmatically; durable you checkpoint + resume. This collapses most of the InProcess/Durable split.
   The CE event-boundary invariant (output must be `unit` before `awaitEvent`) is preserved.

3. **`delayFor` → in-process delay (default).** `await Task.Delay(duration)`. Combined with a checkpoint
   before the delay, this survives a crash with "re-run the delay" semantics. We are **not** building a
   crash-durable absolute-time timer (MAF has no managed scheduler; that's the real cost of dropping DF).
   Document the halt-+-externally-scheduled-resume pattern for users who need tier 2.

4. **Serialization:** register `JsonFSharpConverter` on the `JsonSerializerOptions` passed to
   `CheckpointManager.CreateJson`. 🔎

5. **Durable IDs must be stable across processes.** The rebuilt graph's executor IDs must match the
   checkpoint. Lambdas get unstable auto-IDs (`warnIfLambda`, `src/AgentNet/Workflow.fs:369`) — in the
   durable build path, **escalate the lambda warning to a hard error** (or require an explicit stable id).

6. **`awaitEvent` / `delayFor` migrate from `AgentNet.Durable.WorkflowBuilderExtensions` into core**
   (they're plain MAF nodes now). `AgentNet.Durable` shrinks to checkpoint stores + run/resume helpers.

### Proposed run/resume surface

```fsharp
type DurableRunResult<'output> =
    | Completed of 'output
    | Suspended of awaiting: PendingRequest * checkpoint: CheckpointInfo

Workflow.Durable.start  : CheckpointManager -> IServiceProvider -> 'input -> WorkflowDef<'i,'o,'e> -> Task<DurableRunResult<'o>>
Workflow.Durable.resume : CheckpointManager -> IServiceProvider -> CheckpointInfo -> (*typed response*) obj -> WorkflowDef<'i,'o,'e> -> Task<DurableRunResult<'o>>
```

The host maps "session/checkpoint X awaiting event Y" → business state and calls `resume` when the
response arrives — same shape as the trade sample's start/approve/status endpoints, backed by a
`CheckpointManager` instead of `DurableTaskClient`.

---

## 4. Open Questions

### ✅ Resolved in Phase 1 — confirmed against MAF 1.10.0

The checkpoint/`RequestPort` surface survived the 1.3 → 1.10 jump and is cleaner than the 1.3-based plan
assumed. Confirmed signatures (from `Microsoft.Agents.AI.Workflows.xml`, net8.0):

- `CheckpointManager.CreateJson(ICheckpointStore<JsonElement>, JsonSerializerOptions)` — **intact**; our
  `JsonSerializerOptions` (+ `JsonFSharpConverter`) flows in. `CreateInMemory` also available.
- `InProcessExecution.RunAsync<T>(workflow, input, CheckpointManager, runId, ct)` — non-streaming run
  **with checkpointing built in** (no `WithCheckpointing` needed). `runId` = our durable instance id.
- `InProcessExecution.ResumeAsync(workflow, CheckpointInfo, CheckpointManager, ct)` — non-streaming
  resume from a persisted checkpoint (cross-process / crash recovery).
- `Run.ResumeAsync(IEnumerable<ExternalResponse>, ct)` (+ generic overload) and
  `StreamingRun.SendResponseAsync(ExternalResponse)` — supply the response to a halted request port.
- `RequestPort.Create<TRequest,TResponse>(id)` — request-port node for `awaitEvent`.
- `FileSystemJsonCheckpointStore(DirectoryInfo)` — file-backed store ships in-box.

**Composition for durable human-in-the-loop:** halt at request port → checkpoint committed → (process may
die) → `ResumeAsync(workflow, checkpointInfo, checkpointManager)` rebuilds the `Run` → `Run.ResumeAsync(responses)`.
Non-streaming `RunAsync` returns a `Run` whose events can be inspected for `WorkflowOutputEvent`
(Completed) vs `RequestInfoEvent` (Suspended) — so the `DurableRunResult` split needs no streaming.

### Still open

- **Packaging:** does `AgentNet.Durable` survive as a separate package, or fold the thin helpers into
  core + ship Azure-backed `ICheckpointStore` implementations separately? Decide after Phase 5.
- `PendingRequest` shape — how much MAF type (`ExternalRequest`/`RequestPortInfo`) to surface vs. wrap
  in F#-typed terms (event name + expected response type + request id).
- Which checkpoint stores ship in-box beyond in-memory + `FileSystemJsonCheckpointStore` (Azure follow-up).
- 🔎 Exact net-of-`Run` API for reading pending requests off a *restored* run before responding —
  confirm during Phase 5 implementation.
- **Packaging:** does `AgentNet.Durable` survive as a separate package, or fold the thin helpers into
  core + ship Azure-backed `ICheckpointStore` implementations separately? Decide after the API is firm.
- `PendingRequest` shape — how much MAF type (`ExternalRequest`/`RequestPortInfo`) to surface vs. wrap
  in F#-typed terms (event name + expected response type + request id).
- Which checkpoint stores ship in-box: in-memory + `FileSystemJsonCheckpointStore` are free; an
  Azure Blob/Table store is a likely follow-up.

---

## 5. Phased Plan

> Tests follow the implementation here — this is a deliberate redesign, so the implementation is the
> source of truth (overrides the usual "update tests not impl" default for this rework).

### Phase 1 — Upgrade & re-validate  *(no behavior change)* ✅ done 2026-06-13
- [x] Bump MAF `1.3.0 → 1.10.0` in `Directory.Build.targets`; aligned `Microsoft.Extensions.AI`
      `10.5.0 → 10.6.0` (the version MAF 1.10.0 depends on).
- [x] Build green across all TFMs (net8/9/10) — **0 errors**, only pre-existing benign warnings. No
      interop changes needed; `Executors.cs`/`DurableExecutors.cs` still compile against MAF 1.10.
- [x] Tests green — **122/122 passed** on net10.0. Upgrade is behavior-preserving.
- [x] Re-confirmed the checkpoint/`RequestPort`/`ResumeAsync` surface against real 1.10 (see §4). API
      survived and is cleaner than the 1.3-based plan assumed.

### Phase 2 — Retire the old guardrails  *(docs only)* ✅ done 2026-06-12
- [x] Rewrite `CLAUDE.md` "do NOT modify" block (Workflow.Durable.run / toMAF / direct-interpreter / DTFx).
      Stripped the guardrail block, kept VERSION MANAGEMENT, added an ARCHITECTURE note + plan pointer.
- [x] Rewrite `ARCHITECTURAL_INVARIANTS.md` — fresh rewrite around the 5 surviving invariants + the
      MAF checkpoint/resume suspension model (stamped mid-migration, defers to this plan). Removed the
      DTFx "suspension engine", the `WaitForExternalEvent` model, and the §2.2 "no `Task.Delay` in
      orchestrators" rule (which now contradicts decision #3). Added the DI invariant (§5).
- [x] Reconcile `DESIGN_CE_TYPE_THREADING.md` — **reviewed, left untouched.** It documents the unchanged
      CE/SRTP machinery; the event-boundary invariant it specifies survives, and it contains no
      DF/`TaskOrchestrationContext` coupling. (Note: it has *pre-existing* drift — shows a 2-param
      `WorkflowState` vs the code's 3-param `'error` phantom — out of scope for this rework.)

### Phase 3 — DI plumbing ✅ done 2026-06-13
- [x] Added `Services: IServiceProvider` to `WorkflowContext` (`Executor.fs`), empty-provider default in
      `create()`, plus `withServices` / `tryGetService<'T>` / `getRequiredService<'T>` helpers.
- [x] Threaded the provider into the in-process runner: collapsed the duplicated executor/`toMAF`
      builders onto a single context-factory core (`toMAFCore` / `runCore`), added `runWithServices` and
      `runWith` (services + ct), and made `toExecutor` propagate the caller's `Services`+`CancellationToken`
      into nested workflows. Durable run/resume helpers are Phase 5 (don't exist yet).
- [x] New `WorkflowDiTests.fs` (4 tests) proves: injection via `runWithServices`, empty-provider default
      throws on required service, `tryGetService` → None when unregistered, and service propagation into a
      composed nested workflow. Full suite 126/126 green; existing 122 unaffected by the refactor.

### Phase 4 — Unify compilation onto MAF (partial) ✅ independent pieces done 2026-06-13
- [x] Migrated `awaitEvent` / `delayFor` / `eventOf` DSL from `AgentNet.Durable.WorkflowBuilderExtensions`
      into core (`WorkflowBuilder.fs` intrinsic CE members + `WorkflowCE.eventOf`); event-boundary CE
      invariant preserved. Durable extensions file emptied (kept as empty AutoOpen module; remove in Phase 6).
- [x] Emit an in-process delay executor for `delayFor` (`Workflow.InProcess.fs` — `Task.Delay` honoring the
      seeded `CancellationToken`, forwards input unchanged). New `DelayWorkflowTests.fs`; updated two
      obsolete `DurableWorkflowTests` (delay no longer throws in-process; awaitEvent error text changed).
- [→] **Moved to Phase 5 (coupling):** Teach `toMAF` to emit a `RequestPort` for `awaitEvent`. A port makes
      the workflow *suspend*; the plain `run` (scans for `WorkflowOutputEvent`) can't complete it, so this
      can't be landed or tested without the suspend/resume runner. Doing it there lets it be validated
      end-to-end with a real response cycle. (`awaitEvent` still throws a clear message under plain `run`.)
- [→] **Moved to Phase 5:** escalate the durable lambda-ID warning to an error — only matters once a
      checkpointed build path exists.

### Phase 5 — Request ports + durable run/resume *(absorbs the moved Phase 4 items)*
- [ ] Teach the durable/suspendable compile path to emit a MAF `RequestPort` (non-generic
      `RequestPort(id, typeof<unit>, packed.OutputType)` + `BindAsExecutor`, or `AddExternalCall`) for
      `awaitEvent`; wire `prev → port → next` edges.
- [ ] In-process responder run path (`RunStreamingAsync` + `SendResponseAsync` / `RunToCompletionAsync`)
      — this is what makes `awaitEvent` work in-process (the "bonus"), validated without checkpointing.
- [ ] Implement `Workflow.Durable.start` / `resume` + `DurableRunResult` over `CheckpointManager`
      (`RunAsync(.., checkpointManager, runId, ..)` / `ResumeAsync(.., checkpointInfo, checkpointManager)` +
      `Run.ResumeAsync(responses)`).
- [ ] Register `JsonFSharpConverter` on the checkpoint serializer; verify a DU-carrying workflow
      round-trips through a real checkpoint store.
- [ ] Escalate the durable lambda-ID warning to an error on the checkpointed build path.

### Phase 6 — Demolition & sample
- [ ] Delete `src/AgentNet.Durable.Interop/DurableExecutors.cs`, `DurableExecutorFactory`, `IExecutor`,
      and the bespoke `Workflow.Durable.run`/`tryRun` loop.
- [ ] Remove the `Microsoft.DurableTask.Abstractions` dependency from `Directory.Build.targets`.
- [ ] Rework `Samples.DurableFunctions` (trade approval) onto the new API — likely no longer an Azure
      Functions host; a plain host (console / ASP.NET) demonstrating start → suspend → resume.

### Phase 7 — Tests & docs
- [ ] Update `DurableWorkflowTests.fs` and any DTFx-coupled tests to the checkpoint/resume model.
- [ ] Add a test proving an `awaitEvent` workflow runs **in-process** (the bonus) and **durably**.
- [ ] Update `README.md` feature matrix and durable docs.
- [ ] Version bump (`Directory.Build.props`): `AgentNetVersion` + the InProcess/Durable pair together.

---

## 6. Risks / Watch-list

- **1.10 API drift** — biggest unknown; Phase 1 de-risks it before any design is committed.
- **Checkpoint determinism** — non-stable step IDs silently break resume; the lambda-→-error guard
  (decision #5) is load-bearing, not cosmetic.
- **Loss of DF's managed timer** — accepted; documented in decision #3.
- **Cross-process resume requires the host to rebuild the `WorkflowDef` with the same provider** —
  mitigated by decision #1 (pure definition), but the host contract must be documented clearly.

---

## 7. Progress Log

| Date | Phase | Notes |
|------|-------|-------|
| 2026-06-12 | 0 | Plan drafted. Direction confirmed: MAF-native, drop DF. Design decisions §3 locked. |
| 2026-06-12 | 2 | Doc cleanup done **before** Phase 1 (to clear contradictions early). `CLAUDE.md` guardrails stripped; `ARCHITECTURAL_INVARIANTS.md` rewritten; `DESIGN_CE_TYPE_THREADING.md` reviewed & preserved. No production code touched. Next up: Phase 1 (MAF 1.3 → 1.10 upgrade + re-validate 🔎 APIs). |
| 2026-06-13 | 1 | MAF 1.3.0→1.10.0, M.E.AI 10.5.0→10.6.0 (`Directory.Build.targets` only). Restore clean (no NU conflicts), build 0 errors across net8/9/10, 122/122 tests pass. Re-validated 1.10 checkpoint/RequestPort/Resume API — survived & cleaner (§4 resolved). Only version numbers changed; no source touched. Next up: Phase 3 (add `WorkflowContext.Services`). |
| 2026-06-13 | 3 | DI plumbing. `WorkflowContext.Services` + helpers (`Executor.fs`); in-process runner refactored to a single context-factory core + `runWithServices`/`runWith`; `toExecutor` now propagates services+ct to nested workflows. New `WorkflowDiTests.fs` (4 tests). 126/126 green. Files: `src/AgentNet/Executor.fs`, `src/AgentNet.InProcess/Workflow.InProcess.fs`, `src/AgentNet.Tests/*`. Next up: Phase 4 (emit RequestPort for awaitEvent; migrate DSL to core). |
| 2026-06-13 | 4 | **Re-sequenced** after API study: `awaitEvent`→`RequestPort` is inseparable from the suspend/resume runner (a port suspends; plain `run` can't complete it), so it + lambda-ID escalation moved to Phase 5. Landed the independent pieces: migrated `awaitEvent`/`delayFor`/`eventOf` DSL into core (`WorkflowBuilder.fs`/`WorkflowCE`); implemented in-process `delayFor` (`Workflow.InProcess.fs`); emptied `AgentNet.Durable/WorkflowBuilderExtensions.fs`. New `DelayWorkflowTests.fs` (2), updated 2 obsolete `DurableWorkflowTests`. 128/128 green; suite back to ~5s (old 30s-hang delay test removed). Next up: Phase 5 (request ports + run/resume). |
