module AgentCore.Types

open System.Collections.Generic

type Message =
    | UserMessage       of content: string
    | AssistantMessage  of content: string
    | SystemMessage     of content: string
    /// A tool result appended to history after execution; pairs with a prior ToolCall.
    | ToolResultMessage of toolCallId: string * content: string
    /// The assistant's decision to invoke one or more tools.
    | AssistantToolCall of calls: ToolCall list

and ToolCall = {
    Id    : string
    Name  : string
    /// Raw JSON-decoded arguments from the provider.  Using Dictionary<string,obj>
    /// matches what System.Text.Json deserializes to without schema baking.
    Input : Dictionary<string, obj>
}


type ParameterSchema = {
    Type        : string
    Description : string
    Required    : bool
    EnumValues  : string list option
}

type ToolDefinition = {
    Name        : string
    Description : string
    Parameters  : Map<string, ParameterSchema>
    Required    : string list
}

type ToolResult =
    | TextResult of string
    | JsonResult of string   // pre-serialised JSON string

let toolResultContent = function
    | TextResult s -> s
    | JsonResult s -> s

// ---------------------------------------------------------------------------
// Agent state
// ---------------------------------------------------------------------------

/// Tracks the full conversation + iteration count.
/// Kept as a plain record — no mutability.  Each agentic loop iteration
/// produces a *new* AgentState via `with` updates.
type AgentState = {
    History  : Message list
    Turns    : int
    MaxTurns : int
}
module AgentState =
    let create maxTurns = { History = []; Turns = 0; MaxTurns = maxTurns }

    let addMessage  msg   state = { state with History = state.History @ [msg] }
    let addMessages msgs  state = { state with History = state.History @ msgs  }
    let incrementTurn     state = { state with Turns = state.Turns + 1 }

    /// Sliding-window: keep SystemMessage(s) + the last N non-system messages.
    let prune windowSize (state: AgentState) =
        let systemMsgs = state.History |> List.filter (function SystemMessage _ -> true | _ -> false)
        let otherMsgs  = state.History |> List.filter (function SystemMessage _ -> false | _ -> true)
        let kept = otherMsgs |> List.rev |> List.truncate windowSize |> List.rev
        { state with History = systemMsgs @ kept }
// ---------------------------------------------------------------------------
// Inference request / response (provider-agnostic)
// ---------------------------------------------------------------------------

type InferenceRequest = {
    Model     : string
    Messages  : Message list
    Tools     : ToolDefinition list option
    MaxTokens : int
}

type TokenUsage = {
    InputTokens  : int
    OutputTokens : int
}

type StopReason =
    | EndTurn
    | ToolUse
    | MaxTokens
    | StopSequence
    | Other of string

type ResponseContent =
    | TextContent     of string
    | ToolCallContent of ToolCall list
    | Mixed           of text: string * calls: ToolCall list

type InferenceResponse = {
    Content    : ResponseContent
    TokensUsed : TokenUsage
    StopReason : StopReason
}
// ---------------------------------------------------------------------------
// Tool support levels
// ---------------------------------------------------------------------------

type ToolSupport =
    | Native   // Structured tool protocol (Anthropic, OpenAI)
    | Emulated // Schema injected into system prompt; JSON parsed from reply
    | NoTools  // Provider cannot use tools; agent runs as plain assistant