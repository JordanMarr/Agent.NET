namespace AgentNet.Durable

open AgentNet
open Microsoft.Agents.AI.Workflows

/// Module for compiling AgentNet workflows to MAF format
[<RequireQualifiedAccess>]
module DurableWorkflow =

    /// Compiles a workflow definition to MAF Workflow using WorkflowBuilder.
    /// Returns a Workflow that can be executed with InProcessExecution.RunAsync.
    ///
    /// NOTE: This is currently a stub. Full implementation requires C# interop
    /// due to F# limitations with inheriting from generic C# classes (Executor<TIn, TOut>).
    /// See: https://github.com/JordanMarr/Agent.NET/issues/XXX
    let toMAF<'input, 'output> (_workflow: WorkflowDef<'input, 'output>) : Workflow =
        failwith "DurableWorkflow.toMAF is not yet implemented. MAF compilation requires C# interop for Executor<TIn,TOut> inheritance. See AgentNet.Durable documentation for status."
