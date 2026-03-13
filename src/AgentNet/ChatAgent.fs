namespace AgentNet

open System.Collections.Generic
open System.Threading.Tasks

/// Configuration for a chat agent
type ChatAgentConfig = {
    Name: string option
    Instructions: string
    Tools: ToolDef list
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

/// Represents an AI agent that can chat and use tools.
type ChatAgent = {
    /// The configuration used to construct this agent (instructions, tools, etc.)
    Config: ChatAgentConfig
    /// Sends a message to the agent and returns only the assistant's final text.
    Chat: string -> System.Threading.CancellationToken -> Task<string>
    /// Sends a message to the agent and returns the full structured response.
    ChatFull: string -> System.Threading.CancellationToken -> Task<ChatResponse>
    /// Streams incremental updates from the agent, including text deltas, 
    /// reasoning deltas, tool-call updates, and a final completion event.
    ChatStream: string -> IAsyncEnumerable<ChatStreamEvent>
}

/// Pipeline functions for creating chat agents
type ChatAgent with

    /// Creates an agent config with the given instructions
    static member create (instructions: string) : ChatAgentConfig =
        { Name = None; Instructions = instructions; Tools = [] }

    /// Sets the agent's name
    static member withName (name: string) (config: ChatAgentConfig) : ChatAgentConfig =
        { config with Name = Some name }

    /// Adds a single tool to the agent
    static member withTool (tool: ToolDef) (config: ChatAgentConfig) : ChatAgentConfig =
        { config with Tools = config.Tools @ [tool] }

    /// Adds a list of tools to the agent
    static member withTools (tools: ToolDef list) (config: ChatAgentConfig) : ChatAgentConfig =
        { config with Tools = config.Tools @ tools }
