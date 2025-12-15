# AgentNet F# Computation Expression Library Spec

## Overview
An F# computation expression library wrapping Microsoft.SemanticKernel to provide a clean, idiomatic way to create AI agents.

## Goals
- Beautiful, clean F# syntax for agent creation
- Leverage computation expressions for fluent configuration
- Wrap SemanticKernel's Agent framework

## API Vision

### Agent Creation
```fsharp
// Example of desired syntax (to be refined)
let myAgent = agent {
    name "Assistant"
    instructions "You are a helpful assistant..."
    // ... more configuration
}
```

## User Requirements
<!-- Add your requirements, preferences, and examples here -->


## SemanticKernel Concepts to Wrap
- ChatCompletionAgent
- AgentThread / ChatHistoryAgentThread
- AgentGroupChat
- Plugins and Functions
- Selection Strategies
- Termination Strategies

## Design Decisions
<!-- Document key design decisions here -->


## Open Questions
<!-- List any open questions or areas needing clarification -->

