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

- ~~**Packaging:** does `AgentNet.Durable` survive as a separate package?~~ **RESOLVED 2026-06-14** —
  collapse to `AgentNet` + `AgentNet.Interop`; delete the InProcess/Durable packages, preserve namespaces.
  See Phase 6.
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
- [x] **Sitting 1 done 2026-06-13** — `awaitEvent` → MAF `RequestPort` compilation + in-process responder.
  - `toMAFCore` now emits a `RequestPort(portId, typeof<WorkflowUnit>, OutputType)` for `awaitEvent` via
    `op_Implicit` to `ExecutorBinding`; the node list is uniform `ExecutorBinding`. Plain `run` pre-checks
    `hasAwaitEvent` and throws a clear "suspends… use runWithResponses" message.
  - `runWithResponses (respond: PendingRequest -> obj)` drives a suspending workflow to completion in-process
    via `RunStreamingAsync` + `WatchStreamAsync` + `SendResponseAsync` (the "bonus" — awaitEvent in-process).
  - **Discovered + fixed a latent bug:** F# `unit` boxes to `null` and MAF **drops null messages**, so any
    `unit`-emitting intermediate step (the whole event-boundary pattern) silently stalled in-process. Added a
    `WorkflowUnit` non-null surrogate, mapped `unit ↔ WorkflowUnit` at the obj boundary (`boundaryBox` /
    `boundaryUnbox` in core `Workflow.fs`; `mafType` for declared types + port request type in InProcess).
    Also unwrap the `ExternalResponse` the port forwards downstream (`unwrapInput`). New regression test for
    plain `unit → unit` routing. 131/131 green; full solution builds.
- [x] **Sitting 2 done 2026-06-13** — durable run/resume over `CheckpointManager`.
  - `Workflow.Durable` module (in `AgentNet.InProcess`): `DurableRunResult<'o>` (Completed | Suspended of
    pending * checkpoint); `start` (runs/checkpoints until completion or first awaitEvent suspension);
    `resume` (from `CheckpointInfo`, answering events via a `respond` callback). Built on streaming run +
    `LastCheckpoint` + `ResumeStreamingAsync`. Factories `inMemoryCheckpoints` / `fileSystemJsonCheckpoints`
    so callers need no direct MAF reference.
  - Mechanism validated: in-memory suspend→resume→complete, and a **record** payload round-trips through a
    real on-disk JSON checkpoint (`DurableCheckpointTests.fs`). Dropped the `awaitEvent` `IsAbstract` guard
    (F# DUs are abstract CLR types — that check wrongly blocked them). `WorkflowUnit` ctor made public so it
    survives JSON checkpoints. 134 pass / 1 skipped.
- [ ] **F# DU checkpoint serialization — gap, NOT a small fix (spike concluded 2026-06-14).** Records
      round-trip through the JSON checkpoint; F# DUs do not. Full spike findings:
  - MAF does **not** ignore our options (my first read was wrong). `JsonMarshaller` (built by
    `CreateJson(store, customOptions)`) consults `customOptions` via `_externalOptions.TryGetTypeInfo` as a
    fallback in `LookupTypeInfo`.
  - Configuring `customOptions` with `DefaultJsonTypeInfoResolver()` + `JsonFSharpConverter()` **fixed the
    suspension checkpoint** — `start` on a DU-typed workflow now serializes and suspends fine.
  - But it still fails on **resume**: MAF records each value's **concrete runtime type** in `PortableValue`,
    which for a DU value is the union **case** subtype (`ApprovalOutcome+Approved`). `JsonFSharpConverter`
    recognizes the union type, not a bare case type, so STJ falls back to reflection and throws. Using the
    generic `ExternalRequest.CreateResponse<union>` does **not** help — `PortableValue` uses `value.GetType()`.
  - So the fix needs a **converter that bridges union case-type → union** (claim case types in `CanConvert`
    and delegate to the union converter) — ~tens of lines, fiddly, plus the `FSharp.SystemTextJson` dep.
    **Decision: deferred.** Per project goal, durable-without-DUs is the win; records cover the common case.
  - **Clean future path (preferred):** when MAF adopts an STJ with native F# DU support, MAF's own internal
    marshaller would likely handle the case-type value with no converter and no dependency. Revisit then, or
    build the bridge converter earlier if a user needs DU event payloads.
  - State: experiment reverted; no dependency added; skipped test documents the gap.
- [x] **Lambda-ID: kept as a warning, not escalated to an error (2026-06-14).** On inspection, lambda
      durable IDs (`DurableId.forStep` hashes the closure type name) are deterministic *within a build*, so
      they're stable across processes running the same binary — the normal resume case. They only shift
      across source changes/redeploys, so a hard error would wrongly break valid same-version durable use.
      Improved the `warnIfLambda` message to name the real risk (cross-redeploy resume). A hard error would
      also need an `IsLambda` flag threaded through `PackedTypedStep`/the CE — deferred as optional polish.
- [ ] Remove the temporary error-surfacing diagnostic in `driveStreaming` once the DU marshaller lands (or
      keep it — it's genuinely useful for surfacing checkpoint-serialization failures).

**Phase 5 is functionally complete** (DU-via-JSON is a documented, deferred follow-up; lambda is resolved).

### Phase 6 — Consolidation & demolition ✅ done 2026-06-14

All steps below landed; build green, 128 pass / 1 skip. Final project set: `AgentNet` (F#, contains both
`AgentNet` and `AgentNet.InProcess` namespaces + the durable module, bundles the interop dll),
`AgentNet.Interop` (C#), `AgentNet.InProcess.Polly` (F#), `AgentNet.Tests`, `StockAdvisor{FS,CS}`.
Deleted: `AgentNet.Durable`, `AgentNet.Durable.Interop`, `Samples.DurableFunctions`, all `Microsoft.DurableTask.*`.

**Packaging decision (resolved 2026-06-14):** collapse to **`AgentNet` (F#) + `AgentNet.Interop` (C#)**.
MAF-native durability adds no dependency beyond what core already references (core already references the
C# interop + `Microsoft.Agents.AI.Workflows`), so the InProcess/Durable package split no longer earns its
keep. **Preserve the `AgentNet.InProcess` namespace + `Workflow.InProcess`/`Workflow.Durable` modules** so
the only break for users is the *package reference* (drop `AgentNet.InProcess`), not their `open` lines.
`AgentNet.InProcess.Polly` keeps its name (signals in-process-only; the `Workflow.InProcess` module still
exists) and re-points its ProjectReference to `AgentNet`.

Dependency-safe order (build green between steps):
- [ ] **Rename** `AgentNet.InProcess.Interop` → `AgentNet.Interop` (dir + csproj + assembly/package id);
      update ProjectReferences in `AgentNet`, `AgentNet.InProcess`, tests. (Namespace is already `AgentNet.Interop`.)
- [ ] **Rework/park `Samples.DurableFunctions`** — it uses the DTFx `Workflow.Durable.run`; convert to a
      plain host demonstrating `start → suspend → resume`, or temporarily drop from the solution to unblock.
- [ ] **Update `AgentNet.Tests`** — drop `AgentNet.Durable`/`AgentNet.Durable.Interop` references; migrate
      or remove DTFx-only tests (`DurableWorkflowTests` uses `DurableWorkflow.containsDurableOperations` etc.).
- [ ] **Delete the DTFx projects** — `AgentNet.Durable` (fsproj) and `AgentNet.Durable.Interop`
      (`DurableExecutors.cs`, `DurableExecutorFactory`, `IExecutor`); remove `Microsoft.DurableTask.*` from
      `Directory.Build.targets` + the projects.
- [ ] **Fold `AgentNet.InProcess` into `AgentNet`** — move `Workflow.InProcess.fs` (+ the InProcess
      `WorkflowBuilderExtensions.fs`) into the `AgentNet` project (keep `namespace AgentNet.InProcess`),
      fix compile order; delete the `AgentNet.InProcess` project; re-point `AgentNet.InProcess.Polly`, tests,
      and `StockAdvisorFS` ProjectReferences to `AgentNet`.
- [ ] **Bundle the C# interop dll** into the `AgentNet` nuget (the existing `BuildOutputInPackage` trick).
- [ ] Version-management sweep (`Directory.Build.props`/`targets`) for the changed package set.

### Phase 7 — Tests, sample & docs
- [x] `DurableWorkflowTests.fs` migrated off DTFx; `awaitEvent` works in-process (`AwaitEventInProcessTests`)
      and durably (`DurableCheckpointTests`).
- [x] **Correlation id**: added `WorkflowContext.CorrelationId` (the durable session id; empty in-process),
      and `Workflow.Durable.start` now takes an explicit `sessionId` (host-owned, for idempotency +
      callback correlation). Threaded into start/resume.
- [x] **New sample `Samples.DurableOcr`** (console): models download PDF → fire OCR (returns unit, uses
      `ctx.CorrelationId`) → `awaitEvent "OcrComplete"` → store. Uses `fileSystemJsonCheckpoints`; shows
      start → suspend (checkpoint to disk) → callback → resume → complete. Replaces the deleted DTFx sample.
- [ ] Update `README.md` feature matrix and durable docs (package names, `Workflow.Durable.start/resume`,
      the honest `step → awaitEvent` shape, idempotency note).
- [ ] Version bump (`Directory.Build.props` `AgentNetVersion`) — release decision, left at rc.8 for now.

**⚠️ Important finding — at-least-once resume (2026-06-14):** durable resume re-executes the step
*immediately after* an `awaitEvent` **more than once** (MAF emits two `ExecutorInvoked`/`WorkflowOutputEvent`
for it on resume; our `respond` is called once). This is the standard durable at-least-once activity
semantic (same as Durable Functions) — so **any side-effecting step after an `awaitEvent` must be
idempotent.** The OCR sample demonstrates the mitigation (dedup on `CorrelationId`). Our durable tests
assert only the final output, so they don't surface it. Follow-ups: (a) document this prominently; (b)
investigate whether our `resume` path can reduce it toward exactly-once, or whether it's inherent to
`ResumeStreamingAsync`. 

**Minor follow-up — noisy lambda warning:** `DurableId.isLambda` false-positives on *named* module-level
functions passed by value to `step` (F# wraps them in `FSharpFunc`, whose type looks compiler-generated),
so the warning cries wolf (fired for the sample's clean `downloadPdf`). Worth tightening the heuristic.

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
| 2026-06-13 | 5a | Request ports + in-process responder. `awaitEvent`→MAF `RequestPort` in `toMAFCore`; `runWithResponses` drives suspending workflows in-process (awaitEvent bonus works end-to-end). Found+fixed latent unit-routing bug (F# unit→null, MAF drops null) via `WorkflowUnit` surrogate at the obj boundary + `ExternalResponse` unwrap. New `AwaitEventInProcessTests.fs` (3). 131/131 green; full solution builds. Files: `src/AgentNet/Workflow.fs`, `src/AgentNet.InProcess/Workflow.InProcess.fs`, tests. Next: Phase 5b (durable start/resume over CheckpointManager + JsonFSharpConverter + lambda escalation). |
| 2026-06-14 | 5b-cleanup | Removed the dead `FSharp.SystemTextJson` dep; `fileSystemJsonCheckpoints` uses plain options (records round-trip natively). 134 pass / 1 skip. |
| 2026-06-15 | 7 | Correlation id (`WorkflowContext.CorrelationId` + host-owned `sessionId` on `Workflow.Durable.start`) for callback routing/idempotency. New `Samples.DurableOcr` console sample (PDF→fire-OCR→awaitEvent→store) on a file-system checkpoint store, demonstrating suspend→resume + idempotent post-await step. **Found: durable resume is at-least-once for the step after `awaitEvent`** (MAF re-executes it) → idempotency required; sample shows the dedup. 128/1 green. Remaining: README/docs. |
| 2026-06-14 | 6 | **Consolidation done.** Removed DTFx (deleted `AgentNet.Durable` + `.Interop` + `Samples.DurableFunctions`, dropped `Microsoft.DurableTask.*`, migrated/trimmed tests). Renamed `AgentNet.InProcess.Interop` → `AgentNet.Interop`. Folded `AgentNet.InProcess` → `AgentNet` (kept `AgentNet.InProcess` namespace, so only package refs break). Re-pointed Polly/StockAdvisorFS/Tests; bundled interop into `AgentNet`; collapsed versions to `AgentNetVersion`; updated CLAUDE.md packages note. Build green, 128/1. Remaining: Phase 7 (README/docs + a new MAF-checkpoint sample to replace the deleted DTFx one). |
| 2026-06-14 | spike | DU-checkpoint spike concluded: simple options fix is **insufficient**. Resolver+converter fixes the suspension checkpoint, but resume fails — MAF records the DU value's concrete *case* subtype in PortableValue and JsonFSharpConverter doesn't claim bare case types (generic CreateResponse<union> doesn't change it). Needs a case→union bridge converter (deferred) or MAF adopting native-DU STJ. Experiment reverted; no dep. §5 updated. |
| 2026-06-13 | 5b | Durable run/resume over `CheckpointManager`: `Workflow.Durable.{start,resume}` + `DurableRunResult` + checkpoint-manager factories (`inMemoryCheckpoints`, `fileSystemJsonCheckpoints` w/ FSharp.SystemTextJson). Mechanism proven (in-memory + record-through-disk-JSON). Dropped `awaitEvent` IsAbstract guard; made `WorkflowUnit` ctor public. 134 pass / 1 skip. **Discovered: F# DU serialization through MAF's checkpoint marshaller does NOT honor CreateJson's JsonSerializerOptions — the headline premise needs a custom IWireMarshaller (tracked, skipped test).** Files: `src/AgentNet.InProcess/*`, `src/AgentNet/WorkflowBuilder.fs`, `Workflow.fs`, tests. Next: solve DU marshalling, then lambda escalation. |
