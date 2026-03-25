/// Generic OpenAI-compatible provider.
/// Covers OpenAI, Groq, Together AI, Mistral, and any API that implements
/// the /v1/chat/completions endpoint.  Adding a new provider requires only
/// a config entry — no new code.
///
/// Tradeoffs of normalising all providers vs using native SDKs:
///   Native SDKs offer richer typing and keep pace with provider changes,
///   but each SDK pulls in its own dependencies, sets its own idioms, and
///   creates provider-specific coupling in the agent core.  Raw HTTP + a thin
///   normalisation layer means we control the entire data path, can test
///   everything with HttpClient fakes, and add any provider in minutes.
///   The cost is manual serialisation — acceptable given the stable OpenAI
///   chat-completions format most providers have converged on.
module AgentCore.Providers.OpenAICompatibleProvider

open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Collections.Generic
open AgentCore.Core.Error
open AgentCore.Types
open AgentCore.Providers.IInferenceProvider

// ---------------------------------------------------------------------------
// Serialisation — OpenAI chat completions format
// ---------------------------------------------------------------------------

let private snakeCase = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)

let private toApiMessage (msg: Message) : obj option =
    match msg with
    | SystemMessage content     -> Some ({| role = "system";    content = content |} :> obj)
    | UserMessage content       -> Some ({| role = "user";      content = content |} :> obj)
    | AssistantMessage content  -> Some ({| role = "assistant"; content = content |} :> obj)
    | AssistantToolCall calls   ->
        let toolCalls =
            calls |> List.map (fun c ->
                {| ``type``      = "function"
                   id            = c.Id
                   ``function``  = {| name = c.Name; arguments = JsonSerializer.Serialize(c.Input) |} |})
        Some ({| role = "assistant"; content = null; tool_calls = toolCalls |} :> obj)
    | ToolResultMessage (id, content) ->
        Some ({| role = "tool"; content = content; tool_call_id = id |} :> obj)

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

let private buildBody (req: InferenceRequest) : string =
    let messages = req.Messages |> List.choose toApiMessage
    let body = Dictionary<string, obj>()
    body["model"]      <- req.Model
    body["messages"]   <- messages
    body["max_tokens"] <- req.MaxTokens

    req.Tools
    |> Option.filter (fun ts -> ts <> [])
    |> Option.iter (fun ts ->
        body["tools"]       <- ts |> List.map toApiTool |> List.toArray :> obj
        body["tool_choice"] <- "auto" :> obj)

    JsonSerializer.Serialize(body, snakeCase)

// ---------------------------------------------------------------------------
// Response parsing — full Result-chained pipeline
// ---------------------------------------------------------------------------

let private parseStopReason = function
    | "stop"         -> EndTurn
    | "tool_calls"   -> ToolUse
    | "length"       -> MaxTokens
    | other          -> Other other

let private parseToolCall (el: JsonElement) : Result<ToolCall, AppError> =
    try
        let fn    = el.GetProperty("function")
        let args  = fn.GetProperty("arguments").GetString()
        let input = JsonSerializer.Deserialize<Dictionary<string, obj>>(args)
        Ok {
            Id    = el.GetProperty("id").GetString()
            Name  = fn.GetProperty("name").GetString()
            Input = input
        }
    with ex ->
        Error (ProviderErr (ResponseParseError $"OpenAI tool_call parse error: {ex.Message}"))

let private extractContent
    (textOpt: string option)
    (toolEls: JsonElement list)
    : Result<ResponseContent, AppError> =

    result {
        let! calls = Result.traverse parseToolCall toolEls
        return
            match textOpt, calls with
            | None,    []  -> TextContent ""
            | Some t,  []  -> TextContent t
            | None,    cs  -> ToolCallContent cs
            | Some t,  cs  -> Mixed (t, cs)
    }

let private parseResponse (json: string) : Result<InferenceResponse, AppError> =
    try
        use doc  = JsonDocument.Parse(json)
        let root = doc.RootElement

        // Surface provider errors onto the Error rail
        match root.TryGetProperty("error") with
        | true, errEl ->
            let msg    = errEl.GetProperty("message").GetString()
            let code   = match errEl.TryGetProperty("code") with true, v -> v.GetString() | _ -> ""
            match code with
            | "invalid_api_key"   | "authentication_error" -> providerErr AuthenticationError
            | "model_not_found"                            -> providerErr (ModelNotFound msg)
            | _                                            -> providerErr (ResponseParseError $"API error: {msg}")
        | _ ->

        let choice  = root.GetProperty("choices").[0]
        let message = choice.GetProperty("message")
        let stop    = parseStopReason (choice.GetProperty("finish_reason").GetString())

        let usage = root.GetProperty("usage")
        let tokens = {
            InputTokens  = usage.GetProperty("prompt_tokens").GetInt32()
            OutputTokens = usage.GetProperty("completion_tokens").GetInt32()
        }

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
            let! content = extractContent textOpt toolEls
            return { Content = content; TokensUsed = tokens; StopReason = stop }
        }
    with ex ->
        providerErr (ResponseParseError $"Failed to parse OpenAI-compatible response: {ex.Message}")

// ---------------------------------------------------------------------------
// Provider implementation
// ---------------------------------------------------------------------------

type OpenAICompatibleProvider(name: string, baseUrl: string, model: string, ?apiKey: string, ?httpClient: HttpClient) =
    let client   = defaultArg httpClient (new HttpClient())
    let endpoint = $"{baseUrl.TrimEnd('/')}/v1/chat/completions"

    interface IInferenceProvider with
        member _.Name        = name
        member _.ToolSupport = Native

        member _.Complete(req: InferenceRequest) : AsyncResult<InferenceResponse> =
            asyncResult {
                let  body    = buildBody req
                let  content = new StringContent(body, Encoding.UTF8, "application/json")
                use  request = new HttpRequestMessage(HttpMethod.Post, endpoint, Content = content)

                apiKey |> Option.iter (fun key ->
                    request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", key))

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

let create (name: string) (baseUrl: string) (model: string) (apiKey: string option) : IInferenceProvider =
    OpenAICompatibleProvider(name, baseUrl, model, ?apiKey = apiKey) :> IInferenceProvider
