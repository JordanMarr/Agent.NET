namespace AgentNet

open System
open System.Collections.Concurrent
open System.IO
open System.Reflection
open System.Reflection.Emit
open System.Threading
open System.Xml.Linq
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns

/// Parameter information extracted from function signature and XML docs
type ParamInfo = {
    Name: string
    Description: string
    Type: Type
}

/// Represents an AI tool that can be invoked by an agent
type ToolDef = {
    Name: string
    Description: string
    Parameters: ParamInfo list
    MethodInfo: MethodInfo
}

/// Functions for building tools using a pipeline approach
type Tool private () =

    // Cache for loaded XML documentation per assembly
    static let xmlDocCache = ConcurrentDictionary<Assembly, XDocument option>()

    /// Tries to load the XML documentation file for an assembly
    static let tryLoadXmlDoc (assembly: Assembly) =
        xmlDocCache.GetOrAdd(assembly, fun asm ->
            try
                let assemblyPath = asm.Location
                let xmlPath = Path.ChangeExtension(assemblyPath, ".xml")
                if File.Exists(xmlPath) then
                    Some (XDocument.Load(xmlPath))
                else
                    None
            with _ -> None
        )

    /// Gets the XML documentation member name for a method
    static let getXmlMemberName (mi: MethodInfo) =
        let typeName = mi.DeclaringType.FullName.Replace("+", ".")
        let paramTypes =
            mi.GetParameters()
            |> Array.map (fun p -> p.ParameterType.FullName)
            |> String.concat ","
        if String.IsNullOrEmpty(paramTypes) then
            $"M:{typeName}.{mi.Name}"
        else
            $"M:{typeName}.{mi.Name}({paramTypes})"

    /// Tries to find the member element in XML documentation
    static let tryFindMemberElement (mi: MethodInfo) =
        tryLoadXmlDoc mi.DeclaringType.Assembly
        |> Option.bind (fun doc ->
            let memberName = getXmlMemberName mi
            let ns = XNamespace.None
            doc.Descendants(ns + "member")
            |> Seq.tryFind (fun el ->
                el.Attribute(XName.Get("name"))
                |> Option.ofObj
                |> Option.map (fun a -> a.Value = memberName)
                |> Option.defaultValue false
            )
        )

    /// Extracts the summary text from a member element
    static let extractSummary (memberEl: XElement) =
        memberEl.Element(XName.Get("summary"))
        |> Option.ofObj
        |> Option.map (fun s -> s.Value.Trim())
        |> Option.defaultValue ""

    /// Extracts parameter descriptions from a member element
    static let extractParamDescriptions (memberEl: XElement) =
        memberEl.Elements(XName.Get("param"))
        |> Seq.choose (fun paramEl ->
            paramEl.Attribute(XName.Get("name"))
            |> Option.ofObj
            |> Option.map (fun nameAttr -> nameAttr.Value, paramEl.Value.Trim())
        )
        |> Map.ofSeq

    /// Builds ParamInfo list from MethodInfo, optionally with XML descriptions
    static let buildParamInfo (mi: MethodInfo) (descriptions: Map<string, string>) =
        mi.GetParameters()
        |> Array.map (fun p ->
            let desc = descriptions |> Map.tryFind p.Name |> Option.defaultValue ""
            { Name = p.Name; Description = desc; Type = p.ParameterType }
        )
        |> Array.toList

    /// Extracts MethodInfo from a quotation
    static let extractMethodInfo (expr: Expr) =
        let rec extract (e: Expr) =
            match e with
            | Lambda(_, body) -> extract body
            | Call(_, mi, _) -> Some mi
            | _ -> None
        extract expr

    // Dependency injection plumbing for Tool.inject.
    // Captured deps are stored by GUID and looked up by emitted IL via GetDep.
    static let depStore = ConcurrentDictionary<string, obj>()

    static let injectedAssemblyModule : ModuleBuilder =
        let asmName = AssemblyName("AgentNet.Tool.Injected")
        let asmBuilder = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.Run)
        asmBuilder.DefineDynamicModule("InjectedToolsModule")

    static let injectedTypeCounter = ref 0

    /// Creates a tool from a quoted function expression
    static member create (expr: Expr<'a -> 'b>) : ToolDef =
        match extractMethodInfo expr with
        | Some mi ->
            let parameters = buildParamInfo mi Map.empty
            {
                Name = mi.Name
                Description = ""
                Parameters = parameters
                MethodInfo = mi
            }
        | None ->
            failwithf "Could not extract method info from quotation: %A" expr

    /// Creates a tool from a quoted function expression, using XML docs for description
    static member createWithDocs (expr: Expr<'a -> 'b>) : ToolDef =
        match extractMethodInfo expr with
        | Some mi ->
            let memberEl = tryFindMemberElement mi
            let description = memberEl |> Option.map extractSummary |> Option.defaultValue ""
            let paramDescs = memberEl |> Option.map extractParamDescriptions |> Option.defaultValue Map.empty
            let parameters = buildParamInfo mi paramDescs
            {
                Name = mi.Name
                Description = description
                Parameters = parameters
                MethodInfo = mi
            }
        | None ->
            failwithf "Could not extract method info from quotation: %A" expr

    /// Sets the tool description
    static member describe (description: string) (tool: ToolDef) : ToolDef =
        { tool with Description = description }

    /// Called from emitted IL. Must be public so the freshly-generated forwarder method,
    /// which lives in a separate dynamic assembly, can call it.
    static member GetDep(key: string) : obj = depStore[key]

    /// Partially applies the leftmost parameter of the tool's underlying method, producing a
    /// new ToolDef whose exposed metadata and invocation are 1 parameter shorter. The captured
    /// dep is stored in a private dictionary keyed by GUID; a freshly-emitted static method
    /// retrieves it and forwards to the original method at invoke time. Returns a first-class
    /// MethodInfo (non-null DeclaringType) so reflection consumers that check DeclaringType
    /// behave correctly.
    static member inject (dep: 'dep) (tool: ToolDef) : ToolDef =
        let mi = tool.MethodInfo
        let allParams = mi.GetParameters()
        if allParams.Length < 1 then
            failwithf "Tool '%s' has no parameters to inject into." tool.Name

        let depParamType = allParams[0].ParameterType
        let remainingParamTypes = allParams |> Array.skip 1 |> Array.map (fun p -> p.ParameterType)

        let key = Guid.NewGuid().ToString("N")
        depStore[key] <- box dep

        let typeIdx = Interlocked.Increment(injectedTypeCounter)
        let typeName = $"InjectedTool_{tool.Name}_{typeIdx}_{key}"
        let typeBuilder =
            injectedAssemblyModule.DefineType(
                typeName,
                TypeAttributes.Public ||| TypeAttributes.Class ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed
            )

        let methodName = mi.Name
        let methodBuilder =
            typeBuilder.DefineMethod(
                methodName,
                MethodAttributes.Public ||| MethodAttributes.Static,
                mi.ReturnType,
                remainingParamTypes
            )

        // Preserve original parameter names + attributes on remaining params
        for i in 0 .. remainingParamTypes.Length - 1 do
            let p = allParams[i + 1]
            methodBuilder.DefineParameter(i + 1, p.Attributes, p.Name) |> ignore

        let il = methodBuilder.GetILGenerator()
        let getDepMethod =
            typeof<Tool>.GetMethod("GetDep", BindingFlags.Public ||| BindingFlags.Static)

        // Load the captured dep: push key, call GetDep, cast/unbox to dep type
        il.Emit(OpCodes.Ldstr, key)
        il.EmitCall(OpCodes.Call, getDepMethod, null)
        if depParamType.IsValueType then
            il.Emit(OpCodes.Unbox_Any, depParamType)
        else
            il.Emit(OpCodes.Castclass, depParamType)

        // Load the remaining args
        for i in 0 .. remainingParamTypes.Length - 1 do
            match i with
            | 0 -> il.Emit(OpCodes.Ldarg_0)
            | 1 -> il.Emit(OpCodes.Ldarg_1)
            | 2 -> il.Emit(OpCodes.Ldarg_2)
            | 3 -> il.Emit(OpCodes.Ldarg_3)
            | n when n < 256 -> il.Emit(OpCodes.Ldarg_S, byte n)
            | n -> il.Emit(OpCodes.Ldarg, int16 n)

        il.EmitCall(OpCodes.Call, mi, null)
        il.Emit(OpCodes.Ret)

        let finalType = typeBuilder.CreateType()
        let finalMethod = finalType.GetMethod(methodName, BindingFlags.Public ||| BindingFlags.Static)

        let remainingParams = tool.Parameters |> List.skip 1

        { tool with
            MethodInfo = finalMethod
            Parameters = remainingParams }
