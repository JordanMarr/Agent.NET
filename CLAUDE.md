ARCHITECTURE

All workflow execution goes through Microsoft Agent Framework (MAF). A workflow is compiled to a MAF
WorkflowBuilder graph via `toMAF` and executed by MAF. Building a workflow is a pure description — steps
do not run at construction time. See `ARCHITECTURAL_INVARIANTS.md` for the invariants and
`DESIGN_CE_TYPE_THREADING.md` for the CE type-threading spec (the SRTP / phantom-type machinery — do not
break it).

DURABLE REWORK IN PROGRESS (durable-rework branch)

`AgentNet.Durable` is being reworked from its bespoke Azure Durable Functions runtime to MAF-native
checkpoint/resume durability (dropping the `Microsoft.DurableTask` dependency). **`DURABLE_REWORK_PLAN.md`
is the source of truth for this effort — read it before touching durable execution.** Earlier "do not
modify" guardrails on `Workflow.Durable.run` / `toMAF` / the durable runtime were stale and have been
removed; that code is being deliberately replaced.

PACKAGES

- `AgentNet` (F#) — the whole library: definitions, CE, agent integration, and in-process + durable
  (MAF checkpoint) workflow execution. Namespaces `AgentNet` and `AgentNet.InProcess` both live here.
- `AgentNet.Interop` (C#) — MAF `Executor` subclasses; bundled into the `AgentNet` package.
- `AgentNet.InProcess.Polly` (F#) — optional Polly resilience decorators (in-process only).

VERSION MANAGEMENT

- `Directory.Build.props` — `AgentNetVersion` (referenced via `$(...)` by `AgentNet` and
  `AgentNet.InProcess.Polly`; bumped together as one release wave).
- `Directory.Build.targets` — Microsoft Agent Framework and dependency versions (uses `Update=` to
  override PackageReference versions).
