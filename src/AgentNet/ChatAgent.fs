namespace AgentNet

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI

/// Configuration for a chat agent
type ChatAgentConfig = {
    Name: string option
    Instructions: string
    Tools: ToolDef list
    ChatOptions: ChatOptions option
}

/// Role of a participant in a chat conversation
type ChatRole =
    | User
    | Assistant
    | System
    | Tool

/// A message in a chat conversation
type ChatMessage = {
    Role: ChatRole
    Content: string
}

/// Full response from a chat agent including conversation history
type ChatResponse = {
    Text: string
    Messages: ChatMessage list
}

/// Rich representation of a tool call streaming update
type ToolCallUpdate =
    { Id: string
      Name: string option
      ArgumentsJsonDelta: string option
      IsStart: bool
      IsEnd: bool }

/// Streaming events emitted by a chat agent
type ChatStreamEvent =
    | TextDelta of string
    | ToolCallDelta of ToolCallUpdate
    | ReasoningDelta of string
    | Completed of ChatResponse


/// Internal helpers for merging ChatOptions defaults with per-call overrides
module internal ChatOptionsHelpers =
    let merge (defaults: ChatOptions option) (overrides: ChatOptions) =
        match defaults, overrides with
        | None, null -> null
        | Some d, null -> d
        | None, o -> o
        | Some d, o ->
            let merged = d.Clone()
            if o.Temperature.HasValue then merged.Temperature <- o.Temperature
            if o.TopP.HasValue then merged.TopP <- o.TopP
            if o.TopK.HasValue then merged.TopK <- o.TopK
            if o.MaxOutputTokens.HasValue then merged.MaxOutputTokens <- o.MaxOutputTokens
            if o.FrequencyPenalty.HasValue then merged.FrequencyPenalty <- o.FrequencyPenalty
            if o.PresencePenalty.HasValue then merged.PresencePenalty <- o.PresencePenalty
            if o.Seed.HasValue then merged.Seed <- o.Seed
            if o.ModelId <> null then merged.ModelId <- o.ModelId
            if o.ResponseFormat <> null then merged.ResponseFormat <- o.ResponseFormat
            if o.StopSequences <> null && o.StopSequences.Count > 0 then
                merged.StopSequences <- ResizeArray(o.StopSequences)
            for tool in o.Tools do merged.Tools.Add(tool)
            if o.AdditionalProperties <> null then
                if merged.AdditionalProperties = null then
                    merged.AdditionalProperties <- AdditionalPropertiesDictionary()
                for kvp in o.AdditionalProperties do
                    merged.AdditionalProperties.[kvp.Key] <- kvp.Value
            merged

/// Represents an AI agent that can chat and use tools.
type ChatAgent(config, chat, chatFull, chatStream) = 

    /// The configuration used to construct this agent (instructions, tools, etc.)
    member this.Config : ChatAgentConfig = 
        config

    /// Sends a message to the agent and returns only the assistant's final text.
    member this.Chat(msg: string, ?ct: CancellationToken) : Task<string> = 
        let ct = defaultArg ct CancellationToken.None
        chat msg ct

    /// Sends a message to the agent and returns the full structured response.
    member this.ChatFull(msg: string, ?ct: CancellationToken) : Task<ChatResponse> = 
        let ct = defaultArg ct CancellationToken.None
        chatFull msg ct

    /// Streams incremental updates from the agent, including text deltas, 
    /// reasoning deltas, tool-call updates, and a final completion event.
    member this.ChatStream(msg: string, ?ct: CancellationToken) : IAsyncEnumerable<ChatStreamEvent> = 
        let ct = defaultArg ct CancellationToken.None
        chatStream msg ct


/// Pipeline functions for creating chat agents
type ChatAgent with

    /// Creates an agent config with the given instructions
    static member create (instructions: string) : ChatAgentConfig =
        { Name = None; Instructions = instructions; Tools = []; ChatOptions = None }

    /// Sets the agent's name
    static member withName (name: string) (config: ChatAgentConfig) : ChatAgentConfig =
        { config with Name = Some name }

    /// Adds a single tool to the agent
    static member withTool (tool: ToolDef) (config: ChatAgentConfig) : ChatAgentConfig =
        { config with Tools = config.Tools @ [tool] }

    /// Adds a list of tools to the agent
    static member withTools (tools: ToolDef list) (config: ChatAgentConfig) : ChatAgentConfig =
        { config with Tools = config.Tools @ tools }

    /// Sets ChatOptions (temperature, top-p, model, etc.) for the agent
    static member withChatOptions (options: ChatOptions) (config: ChatAgentConfig) : ChatAgentConfig =
        { config with ChatOptions = Some options }
