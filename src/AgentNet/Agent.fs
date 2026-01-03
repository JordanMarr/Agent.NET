namespace AgentNet

open System
open System.Threading.Tasks

/// Configuration for an agent
type AgentConfig = {
    Name: string option
    Instructions: string
    Tools: ToolDef list
}

/// Represents an AI agent that can chat and use tools
type ChatAgent = {
    Config: AgentConfig
    Chat: string -> Task<string>
}

/// Pipeline functions for creating agents
type Agent =

    /// Creates an agent config with the given instructions
    static member create (instructions: string) : AgentConfig =
        { Name = None; Instructions = instructions; Tools = [] }

    /// Sets the agent's name
    static member withName (name: string) (config: AgentConfig) : AgentConfig =
        { config with Name = Some name }

    /// Adds a single tool to the agent
    static member withTool (tool: ToolDef) (config: AgentConfig) : AgentConfig =
        { config with Tools = config.Tools @ [tool] }

    /// Adds a list of tools to the agent
    static member withTools (tools: ToolDef list) (config: AgentConfig) : AgentConfig =
        { config with Tools = config.Tools @ tools }
