// ============================================================================
// MIGRATED TO CORE
// ----------------------------------------------------------------------------
// The `awaitEvent` / `delayFor` workflow custom operations and the `eventOf`
// type-witness helper used to live here. As of the MAF-native durable rework
// they have moved into core (AgentNet.WorkflowBuilder / AgentNet.WorkflowCE) so
// they are available in both in-process and durable execution modes.
// See DURABLE_REWORK_PLAN.md (decision #6) and ARCHITECTURAL_INVARIANTS.md §4.
//
// This module is intentionally left empty (kept so `open AgentNet.Durable`
// continues to resolve). It can be removed entirely in the Phase 6 cleanup.
// ============================================================================

[<AutoOpen>]
module AgentNet.Durable.WorkflowBuilderExtensions
