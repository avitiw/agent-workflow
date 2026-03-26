/// Ollama provider — POST {baseUrl}/v1/chat/completions  (OpenAI-compatible format).
/// No auth required.  Tool support depends on the loaded model; we attempt
/// native tool use and fall back to Emulated when the model signals it cannot.
/// baseUrl is configurable so remote Ollama instances work identically.
module AgentCore.Providers.OllamaProvider

open System.Net.Http
open System.Text
open System.Text.Json
open System.Collections.Generic
open AgentCore.Core.Error
open AgentCore.Types
open AgentCore.Providers.IInferenceProvider

// ---------------------------------------------------------------------------
// Serialisation — OpenAI-compatible chat completions format
// ---------------------------------------------------------------------------

let private snakeCase = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)

let private toApiRole = function
    | UserMessage _      -> "user"
    | AssistantMessage _ -> "assistant"
    | SystemMessage _    -> "system"
    | AssistantToolCall _-> "assistant"
    | ToolResultMessage _-> "tool"

let private toApiMessage (msg: Message) : obj option =
    match msg with
    | UserMessage content       -> Some {| role = "user";      content = content |}
    | AssistantMessage content  -> Some {| role = "assistant"; content = content |}
    | SystemMessage content     -> Some {| role = "system";    content = content |}
    | AssistantToolCall calls   ->
        let toolCalls =
            calls |> List.map (fun c ->
                {| ``type`` = "function"
                   id       = c.Id
                   ``function`` = {| name = c.Name; arguments = JsonSerializer.Serialize(c.Input) |} |})
        Some {| role = "assistant"; content = null; tool_calls = toolCalls |}
    | ToolResultMessage (id, content) ->
        Some {| role = "tool"; content = content; tool_call_id = id |}

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
    {| ``type`` = "function"
       ``function`` = {|
           name        = t.Name
           description = t.Description
           parameters  = {| ``type`` = "object"; properties = props; required = t.Required |}
       |} |}
    :> obj

let private buildBody (model: string) (req: InferenceRequest) : string =
    let messages = req.Messages |> List.choose toApiMessage
    let body = Dictionary<string, obj>()
    body["model"]    <- model
    body["messages"] <- messages
    body["stream"]   <- false

    req.Tools
    |> Option.filter (fun ts -> ts <> [])
    |> Option.iter (fun ts ->
        body["tools"] <- ts |> List.map toApiTool |> List.toArray :> obj)

    JsonSerializer.Serialize(body, snakeCase)

// ---------------------------------------------------------------------------
// Response parsing
// ---------------------------------------------------------------------------

let private parseStopReason = function
    | "stop"         -> EndTurn
    | "tool_calls"   -> ToolUse
    | "length"       -> MaxTokens
    | other          -> Other other

let private parseToolCall (el: JsonElement) : Result<ToolCall, AppError> =
    try
        let fn   = el.GetProperty("function")
        let args = fn.GetProperty("arguments").GetString()
        let input = JsonSerializer.Deserialize<Dictionary<string, obj>>(args)
        Ok {
            Id    = el.GetProperty("id").GetString()
            Name  = fn.GetProperty("name").GetString()
            Input = input
        }
    with ex ->
        Error (ProviderErr (ResponseParseError $"Ollama tool_call parse error: {ex.Message}"))

let private parseResponse (json: string) : Result<InferenceResponse, AppError> =
    try
        use doc  = JsonDocument.Parse(json)
        let root = doc.RootElement

        match root.TryGetProperty("error") with
        | true, errEl -> providerErr (ResponseParseError (errEl.GetString()))
        | _ ->

        let choice    = root.GetProperty("choices").[0]
        let message   = choice.GetProperty("message")
        let stopStr   = choice.GetProperty("finish_reason").GetString()
        let stopReason = parseStopReason stopStr

        let usage = root.GetProperty("usage")
        let tokens = {
            InputTokens  = usage.GetProperty("prompt_tokens").GetInt32()
            OutputTokens = usage.GetProperty("completion_tokens").GetInt32()
        }

        // Determine content: text vs tool_calls vs both
        let textOpt =
            match message.TryGetProperty("content") with
            | true, v when v.ValueKind = JsonValueKind.String ->
                let s = v.GetString()
                if System.String.IsNullOrWhiteSpace(s) then None else Some s
            | _ -> None

        let toolEls =
            match message.TryGetProperty("tool_calls") with
            | true, v when v.ValueKind = JsonValueKind.Array ->
                v.EnumerateArray() |> Seq.toList
            | _ -> []

        result {
            let! calls = Result.traverse parseToolCall toolEls
            let content =
                match textOpt, calls with
                | None,    []    -> TextContent ""
                | Some t,  []    -> TextContent t
                | None,    cs    -> ToolCallContent cs
                | Some t,  cs    -> Mixed (t, cs)
            return { Content = content; TokensUsed = tokens; StopReason = stopReason }
        }
    with ex ->
        providerErr (ResponseParseError $"Failed to parse Ollama response: {ex.Message}")

// ---------------------------------------------------------------------------
// Provider implementation
// ---------------------------------------------------------------------------

type OllamaProvider(baseUrl: string, model: string, ?httpClient: HttpClient) =
    let client   = defaultArg httpClient (new HttpClient(Timeout = System.Threading.Timeout.InfiniteTimeSpan))
    let endpoint = $"{baseUrl.TrimEnd('/')}/v1/chat/completions"

    interface IInferenceProvider with
        member _.Name        = "Ollama"
        member _.ToolSupport = Native   // most modern Ollama models support tools

        member _.Complete(req: InferenceRequest) : AsyncResult<InferenceResponse> =
            asyncResult {
                let  body    = buildBody model req
                let  content = new StringContent(body, Encoding.UTF8, "application/json")
                use  request = new HttpRequestMessage(HttpMethod.Post, endpoint, Content = content)

                let! response =
                    AsyncResult.protect
                        (fun () -> client.SendAsync(request) |> Async.AwaitTask)
                        (fun msg -> ProviderErr (NetworkFailure $"Ollama unreachable: {msg}"))
                        ()

                let! bodyText =
                    AsyncResult.protect
                        (fun () -> response.Content.ReadAsStringAsync() |> Async.AwaitTask)
                        (fun msg -> ProviderErr (NetworkFailure msg))
                        ()

                return! parseResponse bodyText |> AsyncResult.ofResult
            }

/// Query available models via /api/tags.  Returns Error if Ollama is unreachable.
let listModels (baseUrl: string) (httpClient: HttpClient) : AsyncResult<string list> =
    asyncResult {
        let url = $"{baseUrl.TrimEnd('/')}/api/tags"
        let! response =
            AsyncResult.protect
                (fun () -> httpClient.GetStringAsync(url) |> Async.AwaitTask)
                (fun msg -> ProviderErr (NetworkFailure $"Cannot reach Ollama at {url}: {msg}"))
                ()

        try
            use doc    = JsonDocument.Parse(response)
            let models =
                doc.RootElement.GetProperty("models").EnumerateArray()
                |> Seq.map (fun m -> m.GetProperty("name").GetString())
                |> Seq.toList
            return models
        with ex ->
            return! AsyncResult.err (ProviderErr (ResponseParseError $"Failed to parse Ollama model list: {ex.Message}"))
    }

let create (baseUrl: string) (model: string) : IInferenceProvider =
    OllamaProvider(baseUrl, model) :> IInferenceProvider
