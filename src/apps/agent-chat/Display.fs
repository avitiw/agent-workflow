/// All Spectre.Console rendering logic lives here.
/// The agent core fires AgentEvents; this module translates them to Spectre
/// output.  Keeping rendering isolated means the agent core remains testable
/// without any console dependency.
module Agent.Chat.CLI.Display

open Spectre.Console
open AgentCore.Core.Error
open AgentCore.Types
open AgentCore.Agent.Core
open AgentCore.Config.ConfigTypes

// ---------------------------------------------------------------------------
// Startup panel
// ---------------------------------------------------------------------------

let showStartupBanner (provider: ProviderConfig) (model: string) =
    let panel =
        Panel($"[bold green]{provider.Name}[/]  •  model: [yellow]{model}[/]  •  type: [cyan]{provider.Type}[/]")
            .Header("[bold]fsharp-agent-core[/]")
    panel.Border <- BoxBorder.Rounded
    AnsiConsole.Write(panel)

// ---------------------------------------------------------------------------
// Provider table (--providers flag)
// ---------------------------------------------------------------------------

let showProvidersTable (providers: ProviderConfig list) =
    let table = Table()
    table.AddColumn("[bold]Name[/]")         |> ignore
    table.AddColumn("[bold]Type[/]")         |> ignore
    table.AddColumn("[bold]Model[/]")        |> ignore
    table.AddColumn("[bold]Auth[/]")         |> ignore

    for p in providers do
        let authStatus =
            match p.ApiKey with
            | Some k when not (k.StartsWith("${")) -> "[green]key set[/]"
            | Some _                               -> "[red]unresolved[/]"
            | None                                 -> "[dim]none[/]"
        table.AddRow(p.Name, p.Type, p.DefaultModel, authStatus) |> ignore

    table.Title  <- TableTitle("[bold]Configured Providers[/]")
    table.Border <- TableBorder.Rounded
    AnsiConsole.Write(table)

// ---------------------------------------------------------------------------
// Ollama model list (--models flag)
// ---------------------------------------------------------------------------

let showModelList (providerName: string) (models: string list) =
    let table = Table()
    table.AddColumn("[bold]Model[/]") |> ignore
    for m in models do table.AddRow(m) |> ignore
    table.Title  <- TableTitle($"[bold]Models — {providerName}[/]")
    table.Border <- TableBorder.Rounded
    AnsiConsole.Write(table)

// ---------------------------------------------------------------------------
// Token usage progress bar
// ---------------------------------------------------------------------------

let showTokenUsage (used: TokenUsage) (contextWindow: int) =
    let total = used.InputTokens + used.OutputTokens
    let pct   = float total / float contextWindow * 100.0
    let bar   = BreakdownChart()
    bar.AddItem("Input",  float used.InputTokens,          Color.SteelBlue) |> ignore
    bar.AddItem("Output", float used.OutputTokens,         Color.Gold1)    |> ignore
    bar.AddItem("Free",   float (contextWindow - total),   Color.Grey)     |> ignore
    AnsiConsole.MarkupLine($"[dim]Context: {total:N0}/{contextWindow:N0} tokens ({pct:F1}%%)[/]")
    AnsiConsole.Write(bar)

// ---------------------------------------------------------------------------
// Tool call display
// ---------------------------------------------------------------------------

let showToolCall (call: ToolCall) =
    AnsiConsole.Write(Rule($"[bold yellow]Tool Call:[/] [cyan]{call.Name}[/]"))
    let table = Table()
    table.AddColumn("[dim]Argument[/]")
    table.AddColumn("[dim]Value[/]")
    for kvp in call.Input do
        table.AddRow($"[green]{kvp.Key}[/]", $"[white]{kvp.Value}[/]") |> ignore
    table.Border <- TableBorder.Simple
    AnsiConsole.Write(table)

let showToolResult (call: ToolCall) (result: AgentCore.Types.ToolResult) =
    let content = toolResultContent result
    let preview = if content.Length > 300 then content.[..297] + "..." else content
    AnsiConsole.MarkupLine($"[dim]  ↳ {call.Name} result:[/] [white]{Markup.Escape(preview)}[/]")

// ---------------------------------------------------------------------------
// Final response
// ---------------------------------------------------------------------------

let showFinalResponse (text: string) =
    let panel = Panel(Markup(Markup.Escape(text)))
    panel.Header <- PanelHeader("[bold green]Response[/]")
    panel.Border <- BoxBorder.Rounded
    AnsiConsole.Write(panel)

// ---------------------------------------------------------------------------
// Error panel
// ---------------------------------------------------------------------------

let private errorDescription = function
    | ConfigErr  (FileNotFound p)       -> $"Config file not found: {p}"
    | ConfigErr  (ParseFailure m)       -> $"Config parse error: {m}"
    | ConfigErr  (MissingProvider n)    -> $"No provider named '{n}'"
    | ConfigErr  (UnresolvedEnvVar v)   -> $"Environment variable '${v}' is not set"
    | ProviderErr AuthenticationError   -> "Authentication failed — check your API key"
    | ProviderErr (NetworkFailure m)    -> $"Network error: {m}"
    | ProviderErr (ResponseParseError m)-> $"Response parse error: {m}"
    | ProviderErr (HttpError (s, b))    -> $"HTTP {s}: {b}"
    | ProviderErr (ModelNotFound m)     -> $"Model not found: {m}"
    | ProviderErr (ToolsNotSupported p) -> $"Provider '{p}' does not support tools"
    | ToolErr    (ToolNotFound n)       -> $"Unknown tool: {n}"
    | ToolErr    (InvalidArguments(t,m))-> $"Invalid arguments for tool '{t}': {m}"
    | ToolErr    (ExecutionFailed(t,m)) -> $"Tool '{t}' failed: {m}"
    | MaxTurnsExceeded                  -> "Maximum turn limit reached"
    | Unexpected m                      -> $"Unexpected error: {m}"

let showError (err: AppError) =
    let desc = errorDescription err
    let panel = Panel($"[red]{Markup.Escape(desc)}[/]\n\n[dim]Check providers.json and your .env file, then retry.[/]")
    panel.Header <- PanelHeader("[bold red]Error[/]")
    panel.Border <- BoxBorder.Heavy
    AnsiConsole.Write(panel)

// ---------------------------------------------------------------------------
// Live status spinner during inference
// ---------------------------------------------------------------------------

/// Run `work` while showing a spinner with `label`.
let withSpinner (label: string) (work: unit -> Async<'a>) : Async<'a> =
    async {
        let mutable result = Unchecked.defaultof<'a>
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync(label, (fun ctx ->
                task {
                    let! r = work () |> Async.StartAsTask
                    result <- r
                })) |> Async.AwaitTask |> Async.RunSynchronously
        return result
    }

// ---------------------------------------------------------------------------
// AgentEvent handler (wires agent core events to Spectre output)
// ---------------------------------------------------------------------------

let handleAgentEvent (event: AgentEvent) : unit =
    match event with
    | Thinking               -> ()   // spinner is driven by withSpinner wrapper
    | ReceivedResponse resp  ->
        showTokenUsage resp.TokensUsed 200_000  // default context window
    | CallingTool call       -> showToolCall call
    | AgentEvent.ToolResult (call, result) -> showToolResult call result
    | FinalResponse text     -> showFinalResponse text
    | ErrorOccurred err      -> showError err
