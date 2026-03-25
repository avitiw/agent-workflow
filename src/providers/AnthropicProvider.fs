/// Anthropic provider — POST https://api.anthropic.com/v1/messages
/// Auth: x-api-key + anthropic-version headers.
/// Supports native tool use (tools + tool_choice fields).
/// All mapping steps return Result; errors stay on the Error rail.
module AgentCore.Providers.AnthropicProvider

open System.Net.Http
open System.Text
open System.Text.Json
open System.Collections.Generic
open AgentCore.Core.Error
open AgentCore.Types
open AgentCore.Providers.IInferenceProvider

// ---------------------------------------------------------------------------
// Serialisation helpers
// ---------------------------------------------------------------------------

let private snakeCase = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)

/// Map domain Message → Anthropic API message object(s).
/// Returns a list because some message types expand to multiple API messages.
let private toApiMessages (messages: Message list) : obj list =
    messages |> List.collect (function
        | SystemMessage _       -> []   // system extracted separately below
        | UserMessage text      -> [ {| role = "user";      content = text |} :> obj ]
        | AssistantMessage text -> [ {| role = "assistant"; content = text |} :> obj ]
        | AssistantToolCall calls ->
            let blocks =
                calls |> List.map (fun c ->
                    {| ``type`` = "tool_use"; id = c.Id; name = c.Name; input = c.Input |} :> obj)
            [ {| role = "assistant"; content = blocks |} :> obj ]
        | ToolResultMessage (id, content) ->
            let block = {| ``type`` = "tool_result"; tool_use_id = id; content = content |}
            [ {| role = "user"; content = [| block |] |} :> obj ])

let private toApiTool (t: ToolDefinition) : obj =
    let props =
        t.Parameters
        |> Map.map (fun _ p ->
            let d = Dictionary<string, obj>()
            d["type"]        <- p.Type
            d["description"] <- p.Description
            p.EnumValues |> Option.iter (fun evs -> d["enum"] <- evs |> List.toArray :> obj)
            d :> obj)
        |> Map.toSeq |> dict
    {| name         = t.Name
       description  = t.Description
       input_schema = {| ``type`` = "object"; properties = props; required = t.Required |} |}
    :> obj

let private buildBody (req: InferenceRequest) : string =
    let systemMsg =
        req.Messages
        |> List.tryPick (function SystemMessage s -> Some s | _ -> None)

    let body = Dictionary<string, obj>()
    body["model"]      <- req.Model
    body["max_tokens"] <- req.MaxTokens
    body["messages"]   <- toApiMessages req.Messages

    systemMsg |> Option.iter (fun s -> body["system"] <- s)

    req.Tools
    |> Option.filter (fun ts -> ts <> [])
    |> Option.iter (fun ts ->
        body["tools"]       <- ts |> List.map toApiTool |> List.toArray :> obj
        body["tool_choice"] <- {| ``type`` = "auto" |} :> obj)

    JsonSerializer.Serialize(body, snakeCase)

// ---------------------------------------------------------------------------
// Response parsing — Result-returning, no exceptions
// ---------------------------------------------------------------------------

let private parseStopReason = function
    | "end_turn"      -> EndTurn
    | "tool_use"      -> ToolUse
    | "max_tokens"    -> MaxTokens
    | "stop_sequence" -> StopSequence
    | other           -> Other other

let private parseToolCall (el: JsonElement) : Result<ToolCall, AppError> =
    try
        let input =
            JsonSerializer.Deserialize<Dictionary<string, obj>>(
                el.GetProperty("input").GetRawText())
        Ok {
            Id    = el.GetProperty("id").GetString()
            Name  = el.GetProperty("name").GetString()
            Input = input
        }
    with ex ->
        Error (ProviderErr (ResponseParseError $"Tool call parse error: {ex.Message}"))

let private parseContent (root: JsonElement) : Result<ResponseContent, AppError> =
    let blocks =
        root.GetProperty("content").EnumerateArray() |> Seq.toList

    let textParts =
        blocks
        |> List.choose (fun b ->
            if b.GetProperty("type").GetString() = "text"
            then Some (b.GetProperty("text").GetString())
            else None)

    let toolBlockEls =
        blocks
        |> List.filter (fun b -> b.GetProperty("type").GetString() = "tool_use")

    result {
        let! calls = Result.traverse parseToolCall toolBlockEls
        return
            match textParts, calls with
            | [],  []    -> TextContent ""
            | ts,  []    -> TextContent (String.concat "\n" ts)
            | [],  cs    -> ToolCallContent cs
            | ts,  cs    -> Mixed (String.concat "\n" ts, cs)
    }

let private parseResponse (json: string) : Result<InferenceResponse, AppError> =
    try
        use doc  = JsonDocument.Parse(json)
        let root = doc.RootElement

        // Surface API-level errors onto the Error rail
        match root.TryGetProperty("error") with
        | true, errEl ->
            let msg = errEl.GetProperty("message").GetString()
            let ``type`` = errEl.GetProperty("type").GetString()
            match ``type`` with
            | "authentication_error" -> providerErr AuthenticationError
            | _ -> providerErr (ResponseParseError $"Anthropic API error: {msg}")
        | _ ->

        let usage = root.GetProperty("usage")
        let tokens = {
            InputTokens  = usage.GetProperty("input_tokens").GetInt32()
            OutputTokens = usage.GetProperty("output_tokens").GetInt32()
        }
        let stopReason = parseStopReason (root.GetProperty("stop_reason").GetString())

        result {
            let! content = parseContent root
            return { Content = content; TokensUsed = tokens; StopReason = stopReason }
        }
    with ex ->
        providerErr (ResponseParseError $"Failed to parse Anthropic response: {ex.Message}")

// ---------------------------------------------------------------------------
// Provider implementation
// ---------------------------------------------------------------------------

/// Each AnthropicProvider instance is self-contained — no shared state,
/// no statics, so two instances with different keys coexist safely.
type AnthropicProvider(apiKey: string, ?httpClient: HttpClient) =
    let client  = defaultArg httpClient (new HttpClient())
    let baseUrl = "https://api.anthropic.com/v1/messages"

    interface IInferenceProvider with
        member _.Name        = "Anthropic"
        member _.ToolSupport = Native

        member _.Complete(req: InferenceRequest) : AsyncResult<InferenceResponse> =
            asyncResult {
                let  body    = buildBody req
                let  content = new StringContent(body, Encoding.UTF8, "application/json")
                use  request = new HttpRequestMessage(HttpMethod.Post, baseUrl, Content = content)

                request.Headers.Add("x-api-key",         apiKey)
                request.Headers.Add("anthropic-version", "2023-06-01")

                let! response =
                    AsyncResult.protect
                        (fun () -> client.SendAsync(request) |> Async.AwaitTask)
                        (fun msg -> ProviderErr (NetworkFailure msg))
                        ()

                let! bodyText =
                    AsyncResult.protect
                        (fun () -> response.Content.ReadAsStringAsync() |> Async.AwaitTask)
                        (fun msg -> ProviderErr (NetworkFailure msg))
                        ()

                return! parseResponse bodyText |> AsyncResult.ofResult
            }

let create (apiKey: string) : IInferenceProvider =
    AnthropicProvider(apiKey) :> IInferenceProvider
