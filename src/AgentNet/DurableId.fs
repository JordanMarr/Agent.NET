namespace AgentNet

open System
open System.Reflection
open System.Security.Cryptography
open System.Text

/// Module for generating stable durable IDs from function metadata.
/// These IDs are used internally for durable workflow replay/checkpoint.
/// Users never provide or see these IDs - they are always auto-generated.
[<RequireQualifiedAccess>]
module DurableId =

    /// Computes a truncated SHA-1 hash (4 bytes = 8 hex chars) of the input string
    let private sha1 (text: string) =
        use sha = SHA1.Create()
        Encoding.UTF8.GetBytes(text)
        |> sha.ComputeHash
        |> Array.take 4
        |> Array.map (fun b -> b.ToString("X2"))
        |> String.concat ""

    /// Gets a stable type name for hashing
    let private typeName (t: Type) =
        if isNull t then "Unit"
        else t.FullName |> Option.ofObj |> Option.defaultValue t.Name

    /// Checks if the method appears to be a lambda/closure (compiler-generated)
    let isLambda (mi: MethodInfo) : bool =
        let dt = mi.DeclaringType
        not (isNull dt) && (
            dt.Name.Contains("@") ||
            dt.Name.Contains("<>") ||
            mi.Name.Contains("@")
        )

    /// Extracts MethodInfo from an F# function by getting its Invoke method
    let getMethodInfo<'a, 'b> (fn: 'a -> 'b) : MethodInfo =
        fn.GetType().GetMethod("Invoke", [| typeof<'a> |])

    /// Generates a stable durable ID for a step function
    let forStep<'i, 'o> (fn: 'i -> 'o) : string =
        let mi = getMethodInfo fn
        let declaringTypeName =
            if isNull mi.DeclaringType then "Global"
            else typeName mi.DeclaringType
        let identity = String.concat "|" [
            "Step"
            declaringTypeName
            mi.Name
            typeName typeof<'i>
            typeName typeof<'o>
        ]
        "Node_" + sha1 identity

    /// Generates a stable durable ID for a parallel branch
    let forParallel<'i, 'o> (index: int) (fn: 'i -> 'o) : string =
        let mi = getMethodInfo fn
        let declaringTypeName =
            if isNull mi.DeclaringType then "Global"
            else typeName mi.DeclaringType
        let identity = String.concat "|" [
            $"Parallel{index}"
            declaringTypeName
            mi.Name
            typeName typeof<'i>
            typeName typeof<'o>
        ]
        "Node_" + sha1 identity

    /// Generates a stable durable ID for a route branch
    let forRoute<'i, 'o> (fn: 'i -> 'o) : string =
        let mi = getMethodInfo fn
        let declaringTypeName =
            if isNull mi.DeclaringType then "Global"
            else typeName mi.DeclaringType
        let identity = String.concat "|" [
            "Route"
            declaringTypeName
            mi.Name
            typeName typeof<'i>
            typeName typeof<'o>
        ]
        "Node_" + sha1 identity

    /// Generates a stable durable ID for a TypedAgent
    let forAgent<'i, 'o> (agent: TypedAgent<'i, 'o>) : string =
        let agentType = agent.GetType()
        let identity = String.concat "|" [
            "Agent"
            typeName agentType
            typeName typeof<'i>
            typeName typeof<'o>
        ]
        "Node_" + sha1 identity

    /// Generates a stable durable ID for an Executor (from its Execute function)
    let forExecutor<'i, 'o> (exec: Executor<'i, 'o>) : string =
        let mi = getMethodInfo exec.Execute
        let declaringTypeName =
            if isNull mi.DeclaringType then "Global"
            else typeName mi.DeclaringType
        let identity = String.concat "|" [
            "Executor"
            declaringTypeName
            mi.Name
            typeName typeof<'i>
            typeName typeof<'o>
        ]
        "Node_" + sha1 identity

    /// Generates a stable durable ID for a nested workflow
    let forWorkflow<'i, 'o> () : string =
        let identity = String.concat "|" [
            "Workflow"
            typeName typeof<'i>
            typeName typeof<'o>
        ]
        "Node_" + sha1 identity

    /// Logs a warning if the function is a lambda (compiler-generated)
    let warnIfLambda<'a, 'b> (fn: 'a -> 'b) (id: string) : unit =
        let mi = getMethodInfo fn
        if isLambda mi then
            eprintfn $"Warning: Step '{id}' uses a lambda. Consider extracting to a named function for more readable durable IDs."
