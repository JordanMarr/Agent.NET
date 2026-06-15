namespace AgentNet.InProcess

open System.Threading.Tasks
open AgentNet

[<AutoOpen>]
module WorkflowBuilderExtensions =

    type Exec = obj -> WorkflowContext -> Task<obj>
    type Decorator = Exec -> Exec

    type WorkflowBuilder with

        /// Applies a runtime-only decorator to the previously defined step.
        /// Decorators wrap the step’s ExecuteInProcess function (outermost decorator runs first)
        /// and are intended for behaviors like logging, retries, metrics, or tracing.
        /// This does not modify the workflow AST or Durable execution semantics.
        [<CustomOperation("decorate")>]
        member _.Decorate
            (
                state: WorkflowState<'input, 'output, 'error>,
                decorator: Decorator
            ) : WorkflowState<'input, 'output, 'error> =

            let steps = state.PackedSteps
            let prefix, last =
                match List.rev steps with
                | last :: restRev -> List.rev restRev, last
                | [] -> failwith "decorate: no previous step to decorate"

            let decoratedStep =
                { last with ExecuteInProcess = decorator last.ExecuteInProcess }

            {
                Name = state.Name
                PackedSteps = prefix @ [ decoratedStep ]
            }
