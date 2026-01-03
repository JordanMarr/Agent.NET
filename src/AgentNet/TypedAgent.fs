namespace AgentNet

open System.Threading.Tasks

/// A typed wrapper around ChatAgent for structured input/output in workflows
type TypedAgent<'input, 'output> = {
    Agent: ChatAgent
    FormatInput: 'input -> string
    ParseOutput: 'input -> string -> 'output
}

/// Static methods for creating typed agents
type TypedAgent =
    /// Creates a typed agent by wrapping a ChatAgent with format/parse functions
    static member create
        (formatInput: 'input -> string)
        (parseOutput: 'input -> string -> 'output)
        (agent: ChatAgent)
        : TypedAgent<'input, 'output> =
        { Agent = agent; FormatInput = formatInput; ParseOutput = parseOutput }

/// Module functions for TypedAgent
module TypedAgent =
    /// Invokes the typed agent with structured input/output
    let invoke (input: 'input) (agent: TypedAgent<'input, 'output>) : Task<'output> =
        task {
            let prompt = agent.FormatInput input
            let! response = agent.Agent.Chat prompt
            return agent.ParseOutput input response
        }
