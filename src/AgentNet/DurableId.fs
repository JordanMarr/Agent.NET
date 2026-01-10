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

    /// Tries to extract the original function name by inspecting the closure's IL.
    /// F# closures wrap the original function, and the Invoke method calls it directly.
    /// Only returns names for methods defined in the same assembly (not BCL calls).
    let private tryGetCalledMethodName (fnType: Type) (inputType: Type) : string option =
        try
            let invokeMethod = fnType.GetMethod("Invoke", [| inputType |])
            if isNull invokeMethod then None
            else
                let body = invokeMethod.GetMethodBody()
                if isNull body then None
                else
                    let il = body.GetILAsByteArray()
                    if isNull il || il.Length < 5 then None
                    else
                        // Find call (0x28) or callvirt (0x6F) instruction that calls
                        // a method in the same assembly (not a BCL method)
                        let mutable result = None
                        for i in 0 .. il.Length - 5 do
                            if result.IsNone && (il.[i] = 0x28uy || il.[i] = 0x6Fuy) then
                                // Next 4 bytes are the metadata token (little-endian)
                                let token =
                                    int il.[i+1] |||
                                    (int il.[i+2] <<< 8) |||
                                    (int il.[i+3] <<< 16) |||
                                    (int il.[i+4] <<< 24)
                                let resolvedMethod = fnType.Module.ResolveMethod(token)
                                if not (isNull resolvedMethod) &&
                                   not (isNull resolvedMethod.DeclaringType) &&
                                   resolvedMethod.DeclaringType.Assembly = fnType.Assembly then
                                    // Only use methods from the same assembly
                                    result <- Some resolvedMethod.Name
                        result
        with _ -> None

    /// Gets a human-readable display name from a function.
    /// Extracts the original function name by inspecting the closure's IL.
    let getDisplayName<'a, 'b> (fn: 'a -> 'b) : string =
        let fnType = fn.GetType()
        match tryGetCalledMethodName fnType typeof<'a> with
        | Some name when not (name.Contains("@")) && name <> "Invoke" -> name
        | _ ->
            // Fallback to type-based name if we can't extract the method name
            let inputName = typeof<'a>.Name
            let outputName = typeof<'b>.Name
            $"Step<{inputName}, {outputName}>"
