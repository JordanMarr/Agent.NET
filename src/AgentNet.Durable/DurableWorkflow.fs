namespace AgentNet.Durable

open System
open System.Threading.Tasks
open AgentNet
open AgentNet.Durable.Interop

// Type aliases to avoid conflicts between AgentNet and MAF
type MAFExecutor = Microsoft.Agents.AI.Workflows.Executor
type MAFWorkflow = Microsoft.Agents.AI.Workflows.Workflow
type MAFWorkflowBuilder = Microsoft.Agents.AI.Workflows.WorkflowBuilder

/// Module for compiling AgentNet workflows to MAF format
[<RequireQualifiedAccess>]
module DurableWorkflow =

    /// Converts an AgentNet WorkflowStep to a MAF Executor
    let rec private toMAFExecutor (step: WorkflowStep) : MAFExecutor =
        match step with
        | Step (durableId, name, execute) ->
            // Wrap the execute function, creating an AgentNet context for execution
            let fn = Func<obj, Task<obj>>(fun input ->
                let ctx = WorkflowContext.create()
                execute input ctx)
            ExecutorFactory.CreateStep(name, fn)

        | Route (durableId, router) ->
            // Router selects and executes the appropriate branch
            let fn = Func<obj, Task<obj>>(fun input ->
                let ctx = WorkflowContext.create()
                router input ctx)
            ExecutorFactory.CreateStep("Router", fn)

        | Parallel branches ->
            // Create parallel executor from branches
            let branchFns =
                branches
                |> List.map (fun (id, exec) ->
                    Func<obj, Task<obj>>(fun input ->
                        let ctx = WorkflowContext.create()
                        exec input ctx))
                |> ResizeArray
            ExecutorFactory.CreateParallel("Parallel", branchFns)

        | Resilient (settings, inner) ->
            // TODO: Map resilience settings to MAF ExecutorOptions
            // For now, just create the inner executor
            toMAFExecutor inner

    /// Compiles a workflow definition to MAF Workflow using WorkflowBuilder.
    /// Returns a Workflow that can be executed with InProcessExecution.RunAsync.
    ///
    /// IMPORTANT: The workflow must have a Name set for MAF compilation.
    /// Use: Workflow.withName "MyWorkflow" myWorkflow
    let toMAF<'input, 'output> (workflow: WorkflowDef<'input, 'output>) : MAFWorkflow =
        match workflow.Name with
        | None ->
            failwith "Workflow must have a Name set for MAF compilation. Use: Workflow.withName \"MyWorkflow\" myWorkflow"
        | Some name ->
            match workflow.Steps with
            | [] -> failwith "Workflow must have at least one step"
            | firstStep :: restSteps ->
                // Create executors for all steps
                let firstExecutor = toMAFExecutor firstStep
                let restExecutors = restSteps |> List.map toMAFExecutor

                // Build workflow using MAFWorkflowBuilder
                let mutable builder = MAFWorkflowBuilder(firstExecutor).WithName(name)

                // Add edges between consecutive executors
                let mutable prev = firstExecutor
                for exec in restExecutors do
                    builder <- builder.AddEdge(prev, exec)
                    prev <- exec

                // Mark the last executor as output
                builder <- builder.WithOutputFrom(prev)

                // Build and return the workflow
                builder.Build()
