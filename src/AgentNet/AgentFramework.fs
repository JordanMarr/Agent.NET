namespace AgentNet

open System.Collections.Generic
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

    /// Builds a fully functional ChatAgent from config and chat client
    let build (chatClient: IChatClient) (config: ChatAgentConfig) : ChatAgent =
        let mafAgent = createAgent chatClient config
        let thread = mafAgent.GetNewThread()

        {
            Config = config
            Chat = fun message -> task {
                let! response = mafAgent.RunAsync(message, thread)
                return response.Text
            }
        }

/// Extends ChatAgent type with the build function (requires MAF)
[<AutoOpen>]
module ChatAgentExtensions =

    type ChatAgent with
        /// Builds an agent from config using the specified chat client
        static member build (chatClient: IChatClient) (config: ChatAgentConfig) : ChatAgent =
            MAF.build chatClient config
