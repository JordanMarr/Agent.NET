namespace AgentNet

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Reflection
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Microsoft.Extensions.AI

/// Wraps an AIFunction whose leading parameters were bound by Tool.inject. The bound values live
/// here (and so are collected with the function) and are handed to the inner function per invocation
/// via AIFunctionArguments.Context. They must NOT be captured by the AIFunctionFactoryOptions
/// delegates: AIFunctionFactory keeps a static descriptor cache keyed on those delegates, which
/// would pin every injected value for the life of the process.
type internal InjectedAIFunction(inner: AIFunction, bindings: Map<int, obj>) =
    inherit DelegatingAIFunction(inner)

    static member val BindingsKey = obj ()

    override _.InvokeCoreAsync(arguments: AIFunctionArguments, cancellationToken: CancellationToken) =
        if isNull arguments.Context then
            arguments.Context <- Dictionary<obj, obj>()
        arguments.Context[InjectedAIFunction.BindingsKey] <- box bindings
        base.InvokeCoreAsync(arguments, cancellationToken)

/// Integration with Microsoft Agent Framework
[<RequireQualifiedAccess>]
module MAF =

    // The delegates below are part of AIFunctionFactory's descriptor cache key, so they are
    // cached here to be reference-stable: the same tool converts to the same cached descriptor
    // however many times it is injected/converted.

    /// Binding config per set of bound parameter positions. Captures positions only, never values.
    let private parameterBindingCache =
        ConcurrentDictionary<string, Func<ParameterInfo, AIFunctionFactoryOptions.ParameterBindingOptions>>()

    let private getParameterBinding (bindings: ToolBindings) =
        let positions = Set.ofList bindings.Positions
        let key = positions |> Seq.map string |> String.concat ","
        parameterBindingCache.GetOrAdd(key, fun _ ->
            let bindParameter =
                Func<ParameterInfo, AIFunctionArguments, obj>(fun p args ->
                    (args.Context[InjectedAIFunction.BindingsKey] :?> Map<int, obj>)[p.Position])
            Func<ParameterInfo, AIFunctionFactoryOptions.ParameterBindingOptions>(fun p ->
                if positions.Contains p.Position then
                    AIFunctionFactoryOptions.ParameterBindingOptions(
                        ExcludeFromSchema = true,
                        BindParameter = bindParameter)
                else
                    AIFunctionFactoryOptions.ParameterBindingOptions()))

    /// Schema options per (method, param descriptions).
    let private schemaOptionsCache =
        ConcurrentDictionary<struct (MethodInfo * Map<string, string>), AIJsonSchemaCreateOptions>()

    let private getSchemaOptions (mi: MethodInfo) (descriptions: Map<string, string>) =
        schemaOptionsCache.GetOrAdd(struct (mi, descriptions), fun _ ->
            AIJsonSchemaCreateOptions(
                ParameterDescriptionProvider =
                    // null leaves a parameter's schema untouched
                    Func<ParameterInfo, string>(fun p -> descriptions |> Map.tryFind p.Name |> Option.toObj)))

    /// Converts an AgentNet ToolDef to a Microsoft.Extensions.AI AIFunction
    let internal toolToAIFunction (tool: ToolDef) : AIFunction =
        let options = AIFunctionFactoryOptions(
            Name = tool.Name,
            Description = tool.Description
        )
        // Parameters bound by Tool.inject are hidden from the schema and supplied at invoke time.
        if not tool.Bindings.IsEmpty then
            options.ConfigureParameterBinding <- getParameterBinding tool.Bindings
        // Pass param descriptions (e.g. from XML docs) through to the model.
        let descriptions =
            tool.Parameters
            |> List.filter (fun p -> not (String.IsNullOrWhiteSpace p.Description))
            |> List.map (fun p -> p.Name, p.Description)
            |> Map.ofList
        if not descriptions.IsEmpty then
            options.JsonSchemaCreateOptions <- getSchemaOptions tool.MethodInfo descriptions
        // Use the (MethodInfo, object, options) overload for static methods
        // The explicit annotation ensures correct overload resolution
        let fn = AIFunctionFactory.Create(method = tool.MethodInfo, target = null, options = options)
        if tool.Bindings.IsEmpty then fn else InjectedAIFunction(fn, tool.Bindings.Values)

    /// Creates a ChatClientAgent from an AgentNet ChatAgent config
    let createAgent (chatClient: IChatClient) (config: ChatAgentConfig) : AIAgent =
        // Convert tools to AIFunctions and cast to AITool
        let tools =
            config.Tools
            |> List.map (fun t -> toolToAIFunction t :> AITool)
            |> ResizeArray
            :> IList<AITool>

        // Create the agent using the constructor with named parameters
        ChatClientAgent(
            chatClient,
            name = (config.Name |> Option.defaultValue "Agent"),
            instructions = config.Instructions,
            tools = tools) :> AIAgent

    /// Converts FunctionCallContent arguments to a JSON string
    let private argsToJson (args: IDictionary<string, obj>) : string =
        if args = null || args.Count = 0 then "{}"
        else JsonSerializer.Serialize(args)

    /// Maps an AgentResponseUpdate's contents to ChatStreamEvents.
    /// Tracks seen tool call IDs to emit IsStart on first occurrence.
    let private mapUpdateToEvents (seenToolCalls: HashSet<string>) (update: AgentResponseUpdate) : ChatStreamEvent list =
        [ for content in update.Contents do
            match content with
            | :? TextContent as tc when tc.Text <> null ->
                TextDelta tc.Text
            | :? TextReasoningContent as rc when rc.Text <> null ->
                ReasoningDelta rc.Text
            | :? FunctionCallContent as fc ->
                let isStart = seenToolCalls.Add(fc.CallId)
                let argsJson =
                    if fc.Arguments <> null && fc.Arguments.Count > 0
                    then Some (argsToJson fc.Arguments)
                    else None
                ToolCallDelta {
                    Id = fc.CallId
                    Name = if fc.Name <> null then Some fc.Name else None
                    ArgumentsJsonDelta = argsJson
                    IsStart = isStart
                    IsEnd = true
                }
            | _ -> () ]

    /// Builds a fully functional ChatAgent from config and chat client
    let build (chatClient: IChatClient) (config: ChatAgentConfig) : ChatAgent =
        let mafAgent = createAgent chatClient config
        let runOpts =
            match ChatOptionsHelpers.merge config.ChatOptions null with
            | null -> null
            | chatOpts -> ChatClientAgentRunOptions(chatOpts) :> AgentRunOptions

        let chat (msg: string) ct = task {
            let! session = mafAgent.CreateSessionAsync(ct)
            let! response = mafAgent.RunAsync(msg, session, runOpts, ct)
            return response.Text
        }

        let chatFull (msg: string) ct = task {
            let! session = mafAgent.CreateSessionAsync(ct)
            let! response = mafAgent.RunAsync(msg, session, runOpts, ct)
            let messages : AgentNet.ChatMessage list = [
                { Role = AgentNet.ChatRole.User; Content = msg }
                { Role = AgentNet.ChatRole.Assistant; Content = response.Text }
            ]
            return { Text = response.Text; Messages = messages }
        }

        let chatStream (msg: string) ct =
            { new IAsyncEnumerable<ChatStreamEvent> with
                member _.GetAsyncEnumerator(_) =
                    let textAccumulator = StringBuilder()
                    let seenToolCalls = HashSet<string>()
                    let mutable session = Unchecked.defaultof<_>
                    let mutable innerEnumerator : IAsyncEnumerator<AgentResponseUpdate> = null
                    let mutable eventBuffer : ChatStreamEvent list = []
                    let mutable completed = false
                    let mutable current = Unchecked.defaultof<ChatStreamEvent>

                    { new IAsyncEnumerator<ChatStreamEvent> with
                        member _.Current = current
                        member _.MoveNextAsync() =
                            (task {
                                // Initialize on first call
                                if innerEnumerator = null then
                                    let! s = mafAgent.CreateSessionAsync(ct)
                                    session <- s
                                    let stream = mafAgent.RunStreamingAsync(msg, session, runOpts, ct)
                                    innerEnumerator <- stream.GetAsyncEnumerator(ct)

                                // Drain buffered events first
                                match eventBuffer with
                                | next :: rest ->
                                    current <- next
                                    eventBuffer <- rest
                                    return true
                                | [] when completed ->
                                    return false
                                | [] ->
                                    // Pull from inner stream until we have events
                                    let mutable hasEvents = false
                                    while not hasEvents && not completed do
                                        let! hasMore = innerEnumerator.MoveNextAsync()
                                        if hasMore then
                                            let events = mapUpdateToEvents seenToolCalls innerEnumerator.Current
                                            // Accumulate text for the final Completed event
                                            for evt in events do
                                                match evt with
                                                | TextDelta t -> textAccumulator.Append(t) |> ignore
                                                | _ -> ()
                                            match events with
                                            | first :: rest ->
                                                current <- first
                                                eventBuffer <- rest
                                                hasEvents <- true
                                            | [] -> () // Empty update, keep pulling
                                        else
                                            // Stream ended — emit Completed
                                            let fullText = textAccumulator.ToString()
                                            let messages : AgentNet.ChatMessage list = [
                                                { Role = AgentNet.ChatRole.User; Content = msg }
                                                { Role = AgentNet.ChatRole.Assistant; Content = fullText }
                                            ]
                                            current <- Completed { Text = fullText; Messages = messages }
                                            completed <- true
                                            hasEvents <- true
                                    return hasEvents
                            } |> ValueTask<bool>)
                        member _.DisposeAsync() =
                            if innerEnumerator <> null then
                                innerEnumerator.DisposeAsync()
                            else
                                ValueTask()
                    }
            }

        ChatAgent(config, chat, chatFull, chatStream)
            
        

/// Extends ChatAgent type with the build function (requires MAF)
[<AutoOpen>]
module ChatAgentExtensions =

    type ChatAgent with
        /// Builds an agent from config using the specified chat client
        static member build (chatClient: IChatClient) (config: ChatAgentConfig) : ChatAgent =
            MAF.build chatClient config
