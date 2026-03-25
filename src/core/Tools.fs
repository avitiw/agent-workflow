/// Tool definitions and handlers.
/// Each tool is a pure function or an AsyncResult-returning function.
/// Dispatch is via pattern-matching on tool name — no reflection, no
/// dictionary of delegates, fully exhaustive via compiler checking.
module AgentCore.Agent.Tools

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Collections.Generic
open AgentCore.Core.Error
open AgentCore.Types

// Shared HttpClient — reused across all web_search calls (recommended pattern).
let private httpClient = new HttpClient()

// ---------------------------------------------------------------------------
// Tool schema definitions (sent to providers)
// ---------------------------------------------------------------------------

let webSearchTool : ToolDefinition = {
    Name        = "web_search"
    Description = "Search the web for current information. Returns a list of result snippets."
    Parameters  = Map [
        "query", { Type = "string"; Description = "The search query"; Required = true; EnumValues = None }
    ]
    Required    = ["query"]
}

let readFileTool : ToolDefinition = {
    Name        = "read_file"
    Description = "Read the contents of a file at the given path."
    Parameters  = Map [
        "path", { Type = "string"; Description = "Absolute or relative path to the file"; Required = true; EnumValues = None }
    ]
    Required    = ["path"]
}

let writeFileTool : ToolDefinition = {
    Name        = "write_file"
    Description = "Write content to a file, creating it if necessary."
    Parameters  = Map [
        "path",    { Type = "string"; Description = "Path of the file to write"; Required = true; EnumValues = None }
        "content", { Type = "string"; Description = "Content to write";          Required = true; EnumValues = None }
    ]
    Required    = ["path"; "content"]
}

let calculatorTool : ToolDefinition = {
    Name        = "calculator"
    Description = "Evaluate a mathematical expression and return the result."
    Parameters  = Map [
        "expression", { Type = "string"; Description = "A mathematical expression e.g. '2 + 3 * 4'"; Required = true; EnumValues = None }
    ]
    Required    = ["expression"]
}

/// All available tools — the agent core passes this list to providers.
let allTools : ToolDefinition list = [webSearchTool; readFileTool; writeFileTool; calculatorTool]

// ---------------------------------------------------------------------------
// Argument extraction helpers (safe, Result-returning)
// ---------------------------------------------------------------------------

let private getArg (name: string) (input: Dictionary<string, obj>) : Result<string, AppError> =
    match input.TryGetValue(name) with
    | true, v when v <> null -> Ok (v.ToString())
    | _ -> Error (ToolErr (InvalidArguments ("unknown", $"Missing required argument '{name}'")))

// ---------------------------------------------------------------------------
// Calculator — expression parser using operator precedence
// ---------------------------------------------------------------------------

// A small recursive descent parser for +, -, *, /, ^ and parentheses.
// Using FParsec would be cleaner but adds a dependency; this parser is
// ~50 lines and covers the calculator use case precisely.

module private Calc =

    exception ParseError of string

    let private skipWs (s: string) (i: int ref) =
        while !i < s.Length && Char.IsWhiteSpace(s.[!i]) do incr i

    let rec private parseExpr (s: string) (i: int ref) : float =
        let mutable result = parseTerm s i
        skipWs s i
        while !i < s.Length && (s.[!i] = '+' || s.[!i] = '-') do
            let op = s.[!i]
            incr i
            let term = parseTerm s i
            result <- if op = '+' then result + term else result - term
            skipWs s i
        result

    and private parseTerm (s: string) (i: int ref) : float =
        let mutable result = parsePower s i
        skipWs s i
        while !i < s.Length && (s.[!i] = '*' || s.[!i] = '/') do
            let op = s.[!i]
            incr i
            let pow = parsePower s i
            result <- if op = '*' then result * pow else result / pow
            skipWs s i
        result

    and private parsePower (s: string) (i: int ref) : float =
        let b = parseUnary s i
        skipWs s i
        if !i < s.Length && s.[!i] = '^' then
            incr i
            Math.Pow(b, parsePower s i)
        else b

    and private parseUnary (s: string) (i: int ref) : float =
        skipWs s i
        if !i < s.Length && s.[!i] = '-' then
            incr i; -(parseAtom s i)
        else parseAtom s i

    and private parseAtom (s: string) (i: int ref) : float =
        skipWs s i
        if !i >= s.Length then raise (ParseError "Unexpected end of expression")
        if s.[!i] = '(' then
            incr i
            let v = parseExpr s i
            skipWs s i
            if !i >= s.Length || s.[!i] <> ')' then raise (ParseError "Missing closing ')'")
            incr i; v
        else
            let start = !i
            while !i < s.Length && (Char.IsDigit(s.[!i]) || s.[!i] = '.') do incr i
            if !i = start then raise (ParseError $"Unexpected character '{s.[!i]}'")
            Double.Parse(s.[start .. !i - 1], Globalization.CultureInfo.InvariantCulture)

    let evaluate (expr: string) : Result<float, string> =
        try
            let i = ref 0
            let v = parseExpr expr i
            if !i < expr.Length then Error $"Unexpected token at position {!i}: '{expr.[!i]}'"
            else Ok v
        with
        | ParseError msg -> Error msg
        | ex             -> Error ex.Message

// ---------------------------------------------------------------------------
// Tool handlers
// ---------------------------------------------------------------------------

// Strip HTML tags from Wikipedia search snippets (e.g. <span class="searchmatch">).
let private stripHtml (s: string) =
    System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "")

let private searchWikipedia (query: string) : AsyncResult<string list> =
    asyncResult {
        let encoded = Uri.EscapeDataString(query)
        let url     = $"https://en.wikipedia.org/w/api.php?action=query&list=search&srsearch={encoded}&format=json&srlimit=3&utf8=1"

        let! json =
            AsyncResult.protect
                (fun () -> async {
                    use req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url)
                    req.Headers.Add("User-Agent", "fsharp-agent-core/1.0 (https://github.com/local/fsharp-agent-core)")
                    let! resp = httpClient.SendAsync(req) |> Async.AwaitTask
                    return! resp.Content.ReadAsStringAsync() |> Async.AwaitTask })
                (fun msg -> ToolErr (ExecutionFailed ("web_search", $"Wikipedia unreachable: {msg}")))
                ()

        try
            use doc     = JsonDocument.Parse(json)
            let results =
                doc.RootElement
                   .GetProperty("query")
                   .GetProperty("search")
                   .EnumerateArray()
                |> Seq.map (fun r ->
                    let title   = r.GetProperty("title").GetString()
                    let snippet = r.GetProperty("snippet").GetString() |> stripHtml
                    $"[Wikipedia] {title} — {snippet}")
                |> Seq.toList
            return results
        with ex ->
            return! AsyncResult.err (ToolErr (ExecutionFailed ("web_search", $"Failed to parse Wikipedia response: {ex.Message}")))
    }

let private handleWebSearch (input: Dictionary<string, obj>) : AsyncResult<ToolResult> =
    asyncResult {
        let! query   = getArg "query" input
        let  encoded = Uri.EscapeDataString(query)
        let  ddgUrl  = $"https://api.duckduckgo.com/?q={encoded}&format=json&no_html=1&skip_disambig=1"

        let! ddgJson =
            AsyncResult.protect
                (fun () -> httpClient.GetStringAsync(ddgUrl) |> Async.AwaitTask)
                (fun msg -> ToolErr (ExecutionFailed ("web_search", $"DuckDuckGo unreachable: {msg}")))
                ()

        let parts = ResizeArray<string>()

        try
            use doc  = JsonDocument.Parse(ddgJson)
            let root = doc.RootElement

            match root.TryGetProperty("Answer") with
            | true, v when v.ValueKind = JsonValueKind.String ->
                let s = v.GetString()
                if not (String.IsNullOrWhiteSpace(s)) then parts.Add($"Answer: {s}")
            | _ -> ()

            match root.TryGetProperty("AbstractText") with
            | true, v when v.ValueKind = JsonValueKind.String ->
                let text = v.GetString()
                if not (String.IsNullOrWhiteSpace(text)) then
                    let source =
                        match root.TryGetProperty("AbstractSource") with
                        | true, s -> s.GetString()
                        | _       -> "Source"
                    let srcUrl =
                        match root.TryGetProperty("AbstractURL") with
                        | true, u -> $" ({u.GetString()})"
                        | _       -> ""
                    parts.Add($"[{source}]{srcUrl} {text}")
            | _ -> ()

            match root.TryGetProperty("RelatedTopics") with
            | true, arr when arr.ValueKind = JsonValueKind.Array ->
                arr.EnumerateArray()
                |> Seq.filter (fun t -> t.TryGetProperty("Text") |> fst)
                |> Seq.truncate 3
                |> Seq.iter (fun t ->
                    let text   = t.GetProperty("Text").GetString()
                    let srcUrl =
                        match t.TryGetProperty("FirstURL") with
                        | true, u -> $" — {u.GetString()}"
                        | _       -> ""
                    if not (String.IsNullOrWhiteSpace(text)) then
                        parts.Add($"• {text}{srcUrl}"))
            | _ -> ()
        with _ -> ()   // DDG parse failure → fall through to Wikipedia

        // Fall back to Wikipedia search when DDG returns nothing.
        if parts.Count = 0 then
            let! wikiResults = searchWikipedia query
            wikiResults |> List.iter parts.Add

        return
            if parts.Count = 0 then TextResult $"No results found for \"{query}\"."
            else TextResult (String.concat "\n\n" parts)
    }

let private handleReadFile (input: Dictionary<string, obj>) : AsyncResult<ToolResult> =
    asyncResult {
        let! path = getArg "path" input
        let! content =
            AsyncResult.protect
                (fun () -> async { return File.ReadAllText(path) })
                (fun msg -> ToolErr (ExecutionFailed ("read_file", msg)))
                ()
        return TextResult content
    }

let private handleWriteFile (input: Dictionary<string, obj>) : AsyncResult<ToolResult> =
    asyncResult {
        let! path    = getArg "path"    input
        let! content = getArg "content" input
        do! AsyncResult.protect
                (fun () -> async {
                    let dir = Path.GetDirectoryName(path)
                    if not (String.IsNullOrEmpty(dir)) then
                        Directory.CreateDirectory(dir) |> ignore
                    File.WriteAllText(path, content) })
                (fun msg -> ToolErr (ExecutionFailed ("write_file", msg)))
                ()
        return TextResult $"Successfully wrote {content.Length} characters to '{path}'."
    }

let private handleCalculator (input: Dictionary<string, obj>) : AsyncResult<ToolResult> =
    asyncResult {
        let! expr = getArg "expression" input
        let! result =
            Calc.evaluate expr
            |> Result.mapError (fun msg -> ToolErr (ExecutionFailed ("calculator", msg)))
            |> AsyncResult.ofResult
        return TextResult $"{result}"
    }

// ---------------------------------------------------------------------------
// Dispatcher — pattern match over tool name
// ---------------------------------------------------------------------------

/// Dispatch a ToolCall to the appropriate handler.
/// Returns Error (ToolNotFound) for unknown tool names.
let dispatch (call: ToolCall) : AsyncResult<ToolResult> =
    match call.Name with
    | "web_search"  -> handleWebSearch  call.Input
    | "read_file"   -> handleReadFile   call.Input
    | "write_file"  -> handleWriteFile  call.Input
    | "calculator"  -> handleCalculator call.Input
    | unknown       -> AsyncResult.err (ToolErr (ToolNotFound unknown))

/// Execute all tool calls in a list, returning results paired with their call IDs.
/// Tool calls are executed sequentially (order may matter for file ops).
let executeAll (calls: ToolCall list) : AsyncResult<(ToolCall * ToolResult) list> =
    AsyncResult.traverse (fun call ->
        asyncResult {
            let! result = dispatch call
            return (call, result)
        }) calls
