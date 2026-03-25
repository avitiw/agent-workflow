/// Provider-agnostic agentic loop.
/// The loop is a tail-recursive async function driven by pattern-matching on
/// StopReason.  Tool execution and message accumulation compose on the Ok rail;
/// any error short-circuits the loop and propagates up to the CLI layer.
module AgentCore.Agent.Core

open AgentCore.Core.Error
open AgentCore.Types
open AgentCore.Agent.Memory
open AgentCore.Agent.Tools
open AgentCore.Providers.IInferenceProvider

// ---------------------------------------------------------------------------
// Emulated tool support — schema injection + response parsing
// ---------------------------------------------------------------------------

module private Emulation =
    open System.Text.Json
    open System.Text.RegularExpressions

    /// Render tool definitions as JSON schema block injected into system prompt.
    let buildSystemPromptAddendum (tools: ToolDefinition list) : string =
        let schemas =
            tools
            |> List.map (fun t ->
                let props =
                    t.Parameters
                    |> Map.map (fun _ p ->
                        {| ``type`` = p.Type; description = p.Description |} :> obj)
                    |> Map.toSeq |> dict
                {| name        = t.Name
                   description = t.Description
                   parameters  = {| ``type`` = "object"; properties = props; required = t.Required |} |})

        let json = JsonSerializer.Serialize(schemas, JsonSerializerOptions(WriteIndented = true))
        $"""
You have access to the following tools. To use a tool, respond with ONLY a JSON object in this exact format:
{{"tool": "<tool_name>", "input": {{<arguments>}}}}

Available tools:
{json}

If you do not need a tool, respond normally in plain text."""

    /// Parse a tool call JSON from a model's text response.
    let tryParseToolCall (text: string) : ToolCall option =
        let pattern = """(?s)\{.*?"tool"\s*:\s*"([^"]+)".*?"input"\s*:\s*(\{.*?\}).*?\}"""
        let m = Regex.Match(text, pattern)
        if not m.Success then None
        else
            try
                let name  = m.Groups.[1].Value
                let input = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, obj>>(m.Groups.[2].Value)
                Some { Id = System.Guid.NewGuid().ToString(); Name = name; Input = input }
            with _ -> None

    /// Inject emulated tool schema into messages before sending.
    let augmentRequest (tools: ToolDefinition list) (req: InferenceRequest) : InferenceRequest =
        let addendum = buildSystemPromptAddendum tools
        let messages =
            match req.Messages |> List.tryFindIndex (function SystemMessage _ -> true | _ -> false) with
            | Some idx ->
                req.Messages |> List.mapi (fun i m ->
                    if i = idx
                    then match m with SystemMessage s -> SystemMessage (s + "\n\n" + addendum) | _ -> m
                    else m)
            | None ->
                SystemMessage addendum :: req.Messages
        { req with Messages = messages; Tools = None }

// ---------------------------------------------------------------------------
// Agentic loop
// ---------------------------------------------------------------------------

type AgentRunConfig = {
    Provider : IInferenceProvider
    Model    : string
    Tools    : ToolDefinition list
    MaxTurns : int
    OnStep   : AgentEvent -> unit   // side-effecting callback for UI updates
}

and AgentEvent =
    | Thinking
    | ReceivedResponse  of InferenceResponse
    | CallingTool       of ToolCall
    | ToolResult        of ToolCall * AgentCore.Types.ToolResult
    | FinalResponse     of string
    | ErrorOccurred     of AppError

/// One iteration of the loop.  Returns the next AgentState on the Ok rail,
/// or an error that terminates the loop.
let private step
    (config: AgentRunConfig)
    (state: AgentState)
    : AsyncResult<AgentState * bool> =    // (newState, isDone)

    asyncResult {
        config.OnStep Thinking

        // Build inference request — adapt for emulated tool support
        let tools = if config.Tools = [] then None else Some config.Tools
        let baseReq = {
            Model     = config.Model
            Messages  = toMessages state
            Tools     = tools
            MaxTokens = 4096
        }

        let inferenceReq =
            match config.Provider.ToolSupport, tools with
            | Emulated, Some ts -> Emulation.augmentRequest ts baseReq
            | NoTools,  _       -> { baseReq with Tools = None }
            | _                 -> baseReq

        let! response = config.Provider.Complete(inferenceReq)
        config.OnStep (ReceivedResponse response)

        match response.StopReason with
        | MaxTokens ->
            // Surface as a graceful partial response rather than an error
            let text =
                match response.Content with
                | TextContent t | Mixed (t, _) -> t
                | ToolCallContent _             -> "(max tokens reached during tool call)"
            config.OnStep (FinalResponse text)
            let nextState = state |> addAssistantMessage text |> incrementTurn
            return (nextState, true)

        | EndTurn ->
            let text =
                match response.Content with
                | TextContent t | Mixed (t, _) -> t
                | ToolCallContent _             -> ""
            config.OnStep (FinalResponse text)
            let nextState = state |> addAssistantMessage text |> incrementTurn
            return (nextState, true)

        | ToolUse ->
            let calls =
                match response.Content with
                | ToolCallContent cs | Mixed (_, cs) -> cs
                | TextContent text ->
                    // Emulated tool mode: parse JSON from response text
                    match Emulation.tryParseToolCall text with
                    | Some call -> [call]
                    | None      -> []

            if calls = [] then
                // No parseable tool call — treat as a final response
                let text = match response.Content with TextContent t -> t | _ -> ""
                config.OnStep (FinalResponse text)
                return (state |> addAssistantMessage text |> incrementTurn, true)
            else
                // Record tool calls in history then execute each
                let stateWithCalls = state |> addToolCall calls |> incrementTurn

                let! results = Tools.executeAll calls

                // Append tool results to history
                let stateWithResults =
                    results
                    |> List.fold (fun s (call, result) ->
                        config.OnStep (AgentEvent.ToolResult (call, result))
                        addToolResult call.Id (toolResultContent result) s
                    ) stateWithCalls

                return (prune stateWithResults, false)

        | StopSequence | Other _ ->
            let text =
                match response.Content with
                | TextContent t | Mixed (t, _) -> t
                | _ -> ""
            config.OnStep (FinalResponse text)
            return (state |> addAssistantMessage text |> incrementTurn, true)
    }

/// The full agentic loop — tail-recursive over AgentState.
/// Terminates on EndTurn, MaxTokens, or MaxTurns exceeded.
let rec private loop (config: AgentRunConfig) (state: AgentState) : AsyncResult<AgentState> =
    if isExhausted state then
        AsyncResult.err MaxTurnsExceeded
    else
        asyncResult {
            let! (nextState, isDone) = step config state
            if isDone then return nextState
            else return! loop config nextState
        }

let private prependSystem sp state = Memory.addMessage (SystemMessage sp) state

/// Public entry point.  Run the agent with a given user message.
let run (config: AgentRunConfig) (systemPrompt: string option) (userMessage: string) : AsyncResult<string> =
    asyncResult {
        let initialState =
            AgentState.create config.MaxTurns
            |> fun s ->
                match systemPrompt with
                | Some sp -> prependSystem sp s
                | None    -> s
            |> Memory.addUserMessage userMessage

        let! finalState = loop config initialState

        // Extract the last assistant text from history
        let lastText =
            finalState.History
            |> List.rev
            |> List.tryPick (function
                | AssistantMessage text -> Some text
                | _ -> None)
            |> Option.defaultValue "(No response generated)"

        return lastText
    }

