namespace AgentNet

open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.Agents.AI
open Microsoft.Extensions.AI

/// Integration with Microsoft Agent Framework
[<RequireQualifiedAccess>]
module MAF =

    /// Converts an AgentNet Tool to a Microsoft.Extensions.AI AIFunction
    let private toolToAIFunction (tool: Tool) : AIFunction =
        // Create a function that will be called by the agent
        let invokeFunc (args: obj[]) : obj = tool.Invoke(args)

        // Create AIFunction using the factory
        AIFunctionFactory.Create(
            Func<obj[], obj>(invokeFunc),
            name = tool.Name,
            description = tool.Description)

    /// Creates a ChatClientAgent from an AgentNet Agent config
    let createAgent (chatClient: IChatClient) (config: AgentConfig) : AIAgent =
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

    /// Builds a fully functional AgentNet.Agent from config and chat client
    let build (chatClient: IChatClient) (config: AgentConfig) : AgentNet.Agent =
        let mafAgent = createAgent chatClient config
        let thread = mafAgent.GetNewThread()

        {
            Config = config
            Chat = fun message -> async {
                let! response = mafAgent.RunAsync(message, thread) |> Async.AwaitTask
                return response.Text
            }
        }

/// Extension methods for AgentConfig
[<AutoOpen>]
module AgentExtensions =

    type AgentConfig with
        /// Builds the agent with the provided chat client
        member this.Build(chatClient: IChatClient) : AgentNet.Agent =
            MAF.build chatClient this
