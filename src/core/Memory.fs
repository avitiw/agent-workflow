/// Conversation history management with configurable sliding-window pruning.
/// Pure functions only — no mutable state.  The agent core owns the AgentState
/// value and threads it through each loop iteration via functional update.
module AgentCore.Agent.Memory

open AgentCore.Types

// ---------------------------------------------------------------------------
// Sliding-window pruning
// ---------------------------------------------------------------------------

/// Keep all SystemMessages anchored at the front of history, then keep
/// the last `windowSize` non-system messages.  This prevents the context
/// window from growing unboundedly while preserving the system prompt.
let pruneHistory (windowSize: int) (history: Message list) : Message list =
    let systemMsgs = history |> List.filter (function SystemMessage _ -> true | _ -> false)
    let otherMsgs  = history |> List.filter (function SystemMessage _ -> false | _ -> true)
    let kept = otherMsgs |> List.rev |> List.truncate windowSize |> List.rev
    systemMsgs @ kept

// ---------------------------------------------------------------------------
// AgentState transitions  (all pure — return new state)
// ---------------------------------------------------------------------------

let addMessage (msg: Message) (state: AgentState) : AgentState =
    { state with History = state.History @ [msg] }

let addUserMessage (content: string) (state: AgentState) : AgentState =
    { state with History = state.History @ [UserMessage content] }

let addAssistantMessage (content: string) (state: AgentState) : AgentState =
    { state with History = state.History @ [AssistantMessage content] }

let addToolCall (calls: ToolCall list) (state: AgentState) : AgentState =
    { state with History = state.History @ [AssistantToolCall calls] }

let addToolResult (toolCallId: string) (result: string) (state: AgentState) : AgentState =
    { state with History = state.History @ [ToolResultMessage (toolCallId, result)] }

let incrementTurn (state: AgentState) : AgentState =
    { state with Turns = state.Turns + 1 }

/// Apply pruning to the current history if needed.
let prune (state: AgentState) : AgentState =
    { state with
        History = pruneHistory (state.MaxTurns * 2) state.History }

// ---------------------------------------------------------------------------
// Queries (pure)
// ---------------------------------------------------------------------------

let isExhausted (state: AgentState) : bool =
    state.Turns >= state.MaxTurns

/// Extract only the messages needed for the next inference request
/// (system prompt + pruned history, no implementation detail messages).
let toMessages (state: AgentState) : Message list =
    pruneHistory (state.MaxTurns * 4) state.History
