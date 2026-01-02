namespace AgentNet

open System
open System.Collections.Concurrent
open System.IO
open System.Reflection
open System.Xml.Linq
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns

/// Represents an AI tool that can be invoked by an agent
type ToolDef = {
    Name: string
    Description: string
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

    /// Tries to get the summary text from XML documentation
    static let tryGetXmlSummary (mi: MethodInfo) =
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
            |> Option.bind (fun memberEl ->
                memberEl.Element(ns + "summary")
                |> Option.ofObj
                |> Option.map (fun s -> s.Value.Trim())
            )
        )

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
            {
                Name = mi.Name
                Description = ""
                MethodInfo = mi
            }
        | None ->
            failwithf "Could not extract method info from quotation: %A" expr

    /// Creates a tool from a quoted function expression, using XML docs for description
    static member createWithDocs (expr: Expr<'a -> 'b>) : ToolDef =
        match extractMethodInfo expr with
        | Some mi ->
            let description = tryGetXmlSummary mi |> Option.defaultValue ""
            {
                Name = mi.Name
                Description = description
                MethodInfo = mi
            }
        | None ->
            failwithf "Could not extract method info from quotation: %A" expr

    /// Sets the tool description
    static member describe (description: string) (tool: ToolDef) : ToolDef =
        { tool with Description = description }
