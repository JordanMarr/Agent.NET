namespace AgentNet

open System.Threading.Tasks

/// A typed wrapper around ChatAgent for structured input/output in workflows
type TypedAgent<'input, 'output> = {
    ChatAgent: ChatAgent
    FormatInput: 'input -> string
    ParseOutput: 'input -> string -> 'output
}

/// Module functions for TypedAgent
module TypedAgent =
    /// Creates a typed agent by wrapping a ChatAgent with format/parse functions
    let create
        (formatInput: 'input -> string)
        (parseOutput: 'input -> string -> 'output)
        (agent: ChatAgent)
        : TypedAgent<'input, 'output> =
        { ChatAgent = agent; FormatInput = formatInput; ParseOutput = parseOutput }

    /// Invokes the typed agent with structured input/output
    let invoke (input: 'input) (agent: TypedAgent<'input, 'output>) : Task<'output> =
        task {
            let prompt = agent.FormatInput input
            let! response = agent.ChatAgent.Chat prompt
            return agent.ParseOutput input response
        }
