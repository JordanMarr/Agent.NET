namespace AgentNet

open System
open System.Collections.Concurrent
open System.IO
open System.Reflection
open System.Xml.Linq
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns

/// Parameter information extracted from function signature and XML docs
type ParamInfo = {
    Name: string
    Description: string
    Type: Type
}

/// Values supplied by Tool.inject, keyed by MethodInfo parameter position. Deliberately opaque:
/// injected values are often secrets (API keys, connections), so they are not publicly readable
/// and never appear when a ToolDef is printed (%A / ToString) or serialized. Only the bound
/// positions are visible.
[<Sealed; StructuredFormatDisplay("{Display}")>]
type ToolBindings internal (values: Map<int, obj>) =
    /// No injected parameters
    static member Empty = ToolBindings(Map.empty)
    member internal _.Values = values
    member internal _.Add(position: int, value: obj) = ToolBindings(values |> Map.add position value)
    /// True when nothing has been injected
    member _.IsEmpty = values.IsEmpty
    /// The MethodInfo parameter positions that have been injected
    member _.Positions = values |> Map.toList |> List.map fst
    member this.Display = this.ToString()
    override this.ToString() =
        if values.IsEmpty then "none"
        else this.Positions |> List.map string |> String.concat ", " |> sprintf "<injected: %s>"
    override _.Equals(other) =
        match other with
        | :? ToolBindings as o -> values = o.Values
        | _ -> false
    override _.GetHashCode() = hash values

/// Represents an AI tool that can be invoked by an agent
type ToolDef = {
    Name: string
    Description: string
    /// Parameters visible to the model (injected parameters are excluded)
    Parameters: ParamInfo list
    /// The underlying method. Tool.inject does not rewrite it, so it still declares any injected parameters.
    MethodInfo: MethodInfo
    /// Values supplied by Tool.inject. Bound parameters are excluded from the schema shown to the
    /// model and filled in at invocation time.
    Bindings: ToolBindings
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

    /// Formats a type the way it appears in an XML documentation member ID:
    /// nested types use '.' (not '+'), and generic arguments are written as {A,B} without the `N arity.
    static let rec xmlDocTypeName (t: Type) : string =
        if t.IsByRef then
            xmlDocTypeName (t.GetElementType()) + "@"
        elif t.IsPointer then
            xmlDocTypeName (t.GetElementType()) + "*"
        elif t.IsArray then
            let suffix =
                match t.GetArrayRank() with
                | 1 -> "[]"
                | rank -> "[" + String.concat "," (Array.create rank "0:") + "]"
            xmlDocTypeName (t.GetElementType()) + suffix
        elif t.IsGenericParameter then
            (if isNull t.DeclaringMethod then "`" else "``") + string t.GenericParameterPosition
        else
            // Walk outermost -> innermost; each generic type in the chain consumes its own
            // share of the flattened generic argument list.
            let rec chain (x: Type) = if x.IsNested then chain x.DeclaringType @ [ x ] else [ x ]
            let genericArgs = t.GetGenericArguments()
            let segments, _ =
                (([], 0), chain t)
                ||> List.fold (fun (acc, consumed) x ->
                    match x.Name.LastIndexOf '`' with
                    | -1 -> x.Name :: acc, consumed
                    | i ->
                        let arity = int (x.Name.Substring(i + 1))
                        let args =
                            genericArgs[consumed .. consumed + arity - 1]
                            |> Array.map xmlDocTypeName
                            |> String.concat ","
                        $"{x.Name.Substring(0, i)}{{{args}}}" :: acc, consumed + arity)
            let name = segments |> List.rev |> String.concat "."
            if String.IsNullOrEmpty t.Namespace then name else $"{t.Namespace}.{name}"

    /// Gets the XML documentation member name for a method
    static let getXmlMemberName (mi: MethodInfo) =
        let typeName = xmlDocTypeName mi.DeclaringType
        let paramTypes =
            mi.GetParameters()
            |> Array.map (fun p -> xmlDocTypeName p.ParameterType)
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
                Bindings = ToolBindings.Empty
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
                Bindings = ToolBindings.Empty
            }
        | None ->
            failwithf "Could not extract method info from quotation: %A" expr

    /// Sets the tool description
    static member describe (description: string) (tool: ToolDef) : ToolDef =
        { tool with Description = description }

    /// Partially applies the leftmost not-yet-injected parameter of the tool's underlying method,
    /// producing a new ToolDef whose exposed Parameters are 1 shorter. The dep is recorded in
    /// Bindings and rides on the ToolDef: it is hidden from the model's schema and supplied at
    /// invoke time, and is collected normally along with the ToolDef (safe to call per request).
    static member inject (dep: 'dep) (tool: ToolDef) : ToolDef =
        let target =
            tool.MethodInfo.GetParameters()
            |> Array.tryFind (fun p -> not (tool.Bindings.Values.ContainsKey p.Position))
        match target with
        | None ->
            failwithf "Tool '%s' has no parameters to inject into." tool.Name
        | Some p ->
            match box dep with
            | null -> ()
            | v when p.ParameterType.IsInstanceOfType v -> ()
            | v ->
                failwithf "Tool '%s': cannot inject a value of type '%s' into parameter '%s' of type '%s'."
                    tool.Name (v.GetType().FullName) p.Name p.ParameterType.FullName
            { tool with
                Bindings = tool.Bindings.Add(p.Position, box dep)
                Parameters = tool.Parameters |> List.skip 1 }
