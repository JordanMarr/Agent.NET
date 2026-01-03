namespace AgentNet

open System.Threading.Tasks

/// Configuration for a chat agent
type ChatAgentConfig = {
    Name: string option
    Instructions: string
    Tools: ToolDef list
}

/// Represents an AI agent that can chat and use tools
type ChatAgent = {
    Config: ChatAgentConfig
    Chat: string -> Task<string>
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
