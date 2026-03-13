namespace AgentNet

open System.Collections.Generic
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Microsoft.Extensions.AI

/// Integration with Microsoft Agent Framework
[<RequireQualifiedAccess>]
module MAF =

    /// Converts an AgentNet ToolDef to a Microsoft.Extensions.AI AIFunction
    let private toolToAIFunction (tool: ToolDef) : AIFunction =
        // NOTE: tool.Parameters contains param descriptions extracted from XML docs,
        // but AIFunctionFactoryOptions doesn't currently support passing them through.
        // Tracking issue: https://github.com/microsoft/agent-framework/issues/1864
        let options = AIFunctionFactoryOptions(
            Name = tool.Name,
            Description = tool.Description
        )
        // Use the (MethodInfo, object, options) overload for static methods
        // The explicit annotation ensures correct overload resolution
        AIFunctionFactory.Create(method = tool.MethodInfo, target = null, options = options)

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

        let chat (msg: string) ct = task {
            let! session = mafAgent.CreateSessionAsync(ct)
            let! response = mafAgent.RunAsync(msg, session, null, ct)
            return response.Text
        }

        let chatFull (msg: string) ct = task {
            let! session = mafAgent.CreateSessionAsync(ct)
            let! response = mafAgent.RunAsync(msg, session, null, ct)
            let messages : AgentNet.ChatMessage list = [
                { Role = AgentNet.ChatRole.User; Content = msg }
                { Role = AgentNet.ChatRole.Assistant; Content = response.Text }
            ]
            return { Text = response.Text; Messages = messages }
        }

        let chatStream (message: string) =
            { new IAsyncEnumerable<ChatStreamEvent> with
                member _.GetAsyncEnumerator(ct) =
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
                                    let stream = mafAgent.RunStreamingAsync(message, session, null, ct)
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
                                                { Role = AgentNet.ChatRole.User; Content = message }
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
