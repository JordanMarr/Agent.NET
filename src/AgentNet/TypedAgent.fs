namespace AgentNet

open System.Threading.Tasks

/// A typed wrapper around ChatAgent for structured input/output in workflows
type TypedAgent<'input, 'output> = {
    /// The underlying ChatAgent
    ChatAgent: ChatAgent
    /// Formats structured input into an LLM prompt string
    FormatInput: 'input -> string
    /// Parses the AI's response into structured output
    ParseOutput: 'input -> string -> 'output
    /// Invokes the typed agent with structured input/output
    Invoke: 'input -> TypedAgent<'input, 'output> -> Task<'output>
}

/// Module functions for TypedAgent
module TypedAgent =
    /// Invokes the typed agent with structured input/output
    let invoke (input: 'input) (agent: TypedAgent<'input, 'output>) : Task<'output> =
        task {
            let prompt = agent.FormatInput input
            let! response = agent.ChatAgent.Chat prompt
            return agent.ParseOutput input response
        }

    /// <summary>
    /// Creates a typed agent by wrapping a ChatAgent with format/parse functions.
    /// </summary>
    /// <param name="formatInput">A function to format structured input into an LLM prompt string.</param>
    /// <param name="parseOutput">A function to parse the AI's response into a structured output.</param>
    /// <param name="agent">A ChatAgent instance for interacting with the AI model.</param>
    let create
        (formatInput: 'input -> string)
        (parseOutput: 'input -> string -> 'output)
        (agent: ChatAgent) : TypedAgent<'input, 'output> =
        { 
            ChatAgent = agent
            FormatInput = formatInput
            ParseOutput = parseOutput
            Invoke = invoke
        }

