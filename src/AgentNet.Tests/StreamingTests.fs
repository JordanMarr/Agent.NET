/// Tests for ChatAgent streaming support
module AgentNet.Tests.StreamingTests

open NUnit.Framework
open Swensen.Unquote
open AgentNet
open AgentNet.Tests.Stubs
open Microsoft.Extensions.AI
open System.Collections.Generic
open System.Threading.Tasks

/// Helper to collect all events from an IAsyncEnumerable
let collectEvents (stream: IAsyncEnumerable<ChatStreamEvent>) = task {
    let events = ResizeArray<ChatStreamEvent>()
    let enumerator = stream.GetAsyncEnumerator()
    try
        let mutable hasMore = true
        while hasMore do
            let! next = enumerator.MoveNextAsync()
            if next then
                events.Add(enumerator.Current)
            else
                hasMore <- false
        return events |> Seq.toList
    finally
        enumerator.DisposeAsync().AsTask().Wait()
}

[<Test>]
let ``StreamAsync emits TextDelta events for text content``() = task {
    // Arrange
    let client = new StubChatClient()
    let update1 = ChatResponseUpdate()
    update1.Contents.Add(TextContent("Hello"))
    let update2 = ChatResponseUpdate()
    update2.Contents.Add(TextContent(" world"))
    client.SetStreamingUpdates([update1; update2])

    let agent =
        ChatAgent.create "You are a test agent"
        |> ChatAgent.build client

    // Act
    let! events = collectEvents (agent.ChatStream "test")

    // Assert
    test <@ events.Length = 3 @> // 2 text deltas + 1 completed
    test <@ events.[0] = TextDelta "Hello" @>
    test <@ events.[1] = TextDelta " world" @>
    match events.[2] with
    | Completed resp ->
        test <@ resp.Text = "Hello world" @>
        test <@ resp.Messages.Length = 2 @>
        test <@ resp.Messages.[0].Role = AgentNet.ChatRole.User @>
        test <@ resp.Messages.[0].Content = "test" @>
        test <@ resp.Messages.[1].Role = AgentNet.ChatRole.Assistant @>
        test <@ resp.Messages.[1].Content = "Hello world" @>
    | _ -> Assert.Fail("Expected Completed event")
}

[<Test>]
let ``StreamAsync emits ReasoningDelta events``() = task {
    // Arrange
    let client = new StubChatClient()
    let update1 = ChatResponseUpdate()
    update1.Contents.Add(TextReasoningContent("Let me think"))
    let update2 = ChatResponseUpdate()
    update2.Contents.Add(TextContent("The answer is 42"))
    client.SetStreamingUpdates([update1; update2])

    let agent =
        ChatAgent.create "You are a test agent"
        |> ChatAgent.build client

    // Act
    let! events = collectEvents (agent.ChatStream "question")

    // Assert
    test <@ events.Length = 3 @>
    test <@ events.[0] = ReasoningDelta "Let me think" @>
    test <@ events.[1] = TextDelta "The answer is 42" @>
    match events.[2] with
    | Completed resp -> test <@ resp.Text = "The answer is 42" @>
    | _ -> Assert.Fail("Expected Completed event")
}

[<Test>]
let ``StreamAsync emits ToolCallDelta for function calls``() = task {
    // Arrange
    let client = new StubChatClient()
    let update = ChatResponseUpdate()
    let args = dict ["query", box "weather" :> obj] :> IDictionary<string, obj>
    update.Contents.Add(FunctionCallContent("call-1", "search", args))
    client.SetStreamingUpdates([update])

    let agent =
        ChatAgent.create "You are a test agent"
        |> ChatAgent.build client

    // Act
    let! events = collectEvents (agent.ChatStream "search for weather")

    // Assert: find the first ToolCallDelta
    let toolEvents = events |> List.choose (fun e -> match e with ToolCallDelta tc -> Some tc | _ -> None)
    test <@ toolEvents.Length >= 1 @>
    let first = toolEvents.[0]
    test <@ first.Id = "call-1" @>
    test <@ first.Name = Some "search" @>
    test <@ first.IsStart = true @>
    test <@ first.IsEnd = true @>
    test <@ first.ArgumentsJsonDelta.IsSome @>
    // Stream ends with Completed
    match events |> List.last with
    | Completed _ -> ()
    | _ -> Assert.Fail("Expected Completed as last event")
}

[<Test>]
let ``StreamAsync always ends with exactly one Completed event``() = task {
    // Arrange: empty stream
    let client = new StubChatClient()
    client.SetStreamingUpdates([])

    let agent =
        ChatAgent.create "You are a test agent"
        |> ChatAgent.build client

    // Act
    let! events = collectEvents (agent.ChatStream "hello")

    // Assert
    test <@ events.Length = 1 @>
    match events.[0] with
    | Completed resp ->
        test <@ resp.Text = "" @>
        test <@ resp.Messages.Length = 2 @>
    | _ -> Assert.Fail("Expected Completed event")
}

[<Test>]
let ``StreamAsync handles interleaved text and tool calls``() = task {
    // Arrange
    let client = new StubChatClient()
    let u1 = ChatResponseUpdate()
    u1.Contents.Add(TextContent("I'll search for that. "))
    let u2 = ChatResponseUpdate()
    let args = dict ["q", box "test"]
    u2.Contents.Add(FunctionCallContent("call-1", "search", args))
    let u3 = ChatResponseUpdate()
    u3.Contents.Add(TextContent("Here are the results."))
    client.SetStreamingUpdates([u1; u2; u3])

    let agent =
        ChatAgent.create "You are a test agent"
        |> ChatAgent.build client

    // Act
    let! events = collectEvents (agent.ChatStream "search")

    // Assert: verify ordering of event types
    let textEvents = events |> List.choose (fun e -> match e with TextDelta t -> Some t | _ -> None)
    let toolEvents = events |> List.choose (fun e -> match e with ToolCallDelta tc -> Some tc | _ -> None)
    test <@ textEvents |> List.exists (fun t -> t.Contains("search for that")) @>
    test <@ textEvents |> List.exists (fun t -> t.Contains("results")) @>
    test <@ toolEvents |> List.exists (fun tc -> tc.Id = "call-1") @>
    // Completed is last and contains accumulated text
    match events |> List.last with
    | Completed resp -> test <@ resp.Text.Contains("search for that") && resp.Text.Contains("results") @>
    | _ -> Assert.Fail("Expected Completed")
}

[<Test>]
let ``StreamAsync Completed event contains accumulated text``() = task {
    // Arrange
    let client = new StubChatClient()
    let u1 = ChatResponseUpdate()
    u1.Contents.Add(TextContent("Part 1"))
    let u2 = ChatResponseUpdate()
    u2.Contents.Add(TextContent(" Part 2"))
    let u3 = ChatResponseUpdate()
    u3.Contents.Add(TextContent(" Part 3"))
    client.SetStreamingUpdates([u1; u2; u3])

    let agent =
        ChatAgent.create "You are a test agent"
        |> ChatAgent.build client

    // Act
    let! events = collectEvents (agent.ChatStream "input")

    // Assert
    let completedEvents = events |> List.choose (fun e -> match e with Completed r -> Some r | _ -> None)
    test <@ completedEvents.Length = 1 @>
    test <@ completedEvents.[0].Text = "Part 1 Part 2 Part 3" @>
}

[<Test>]
let ``StreamAsync no events after Completed``() = task {
    // Arrange
    let client = new StubChatClient()
    let u1 = ChatResponseUpdate()
    u1.Contents.Add(TextContent("Hello"))
    client.SetStreamingUpdates([u1])

    let agent =
        ChatAgent.create "You are a test agent"
        |> ChatAgent.build client

    // Act
    let! events = collectEvents (agent.ChatStream "hi")

    // Assert: Completed is last
    let lastEvent = events |> List.last
    match lastEvent with
    | Completed _ -> ()
    | _ -> Assert.Fail("Last event should be Completed")

    // No Completed in non-last positions
    let nonLastEvents = events |> List.take (events.Length - 1)
    let completedInMiddle = nonLastEvents |> List.exists (fun e -> match e with Completed _ -> true | _ -> false)
    test <@ not completedInMiddle @>
}

[<Test>]
let ``StreamAsync multiple contents in single update``() = task {
    // Arrange: single update with both reasoning and text
    let client = new StubChatClient()
    let update = ChatResponseUpdate()
    update.Contents.Add(TextReasoningContent("thinking..."))
    update.Contents.Add(TextContent("answer"))
    client.SetStreamingUpdates([update])

    let agent =
        ChatAgent.create "You are a test agent"
        |> ChatAgent.build client

    // Act
    let! events = collectEvents (agent.ChatStream "q")

    // Assert
    test <@ events.Length = 3 @>
    test <@ events.[0] = ReasoningDelta "thinking..." @>
    test <@ events.[1] = TextDelta "answer" @>
    match events.[2] with
    | Completed resp -> test <@ resp.Text = "answer" @>
    | _ -> Assert.Fail("Expected Completed")
}
