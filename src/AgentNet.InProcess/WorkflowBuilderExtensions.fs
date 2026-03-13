namespace AgentNet.InProcess

open System.Threading.Tasks
open AgentNet

[<AutoOpen>]
module WorkflowBuilderExtensions =

    type Exec = obj -> WorkflowContext -> Task<obj>
    type Decorator = Exec -> Exec

    /// Compose decorators left‑to‑right: (d1 >-> d2) applies d1 first, then d2.
    let (>->) (d1: Decorator) (d2: Decorator) : Decorator =
        fun exec -> exec |> d1 |> d2

    type WorkflowBuilder with

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
