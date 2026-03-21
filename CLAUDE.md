IMPORTANT — ARCHITECTURAL GUARDRAILS

Do NOT modify the following under ANY circumstances:
- Workflow.InProcess.run
- Workflow.Durable.run
- toMAF
- MAFInProcessExecution.RunAsync
- Any code that executes steps directly in a loop
- Any code that bypasses MAF

These functions are architecturally correct and MUST remain unchanged.
If tests fail, update the tests — NOT the implementation.
If you believe a function is incorrect, STOP and ask me before changing it.
Never reintroduce the direct interpreter (looping over packed steps).
All workflow execution MUST go through MAF.

VERSION MANAGEMENT

- `Directory.Build.props` — AgentNet package versions (properties referenced via `$(...)` in .fsproj files)
- `Directory.Build.targets` — Microsoft Agent Framework and dependency versions (uses `Update=` to override PackageReference versions)
- AgentNet.InProcess and AgentNet.InProcess.Polly share `AgentNetInProcessVersion` and must always be bumped together.
