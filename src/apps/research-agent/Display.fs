/// All Spectre.Console rendering logic.
/// Adapted from fsharp-agent-core — extended with phase progress and token summary.
module AgentResearch.CLI.Display

open Spectre.Console
open AgentCore.Core.Error
open AgentCore.Types
open AgentCore.Agent.Core
open AgentCore.Config.ConfigTypes
open AgentResearch.Planner.PlanTypes

// ---------------------------------------------------------------------------
// Startup banner
// ---------------------------------------------------------------------------

let showStartupBanner (provider: ProviderConfig) (model: string) =
    let panel =
        Panel($"[bold green]{provider.Name}[/]  •  model: [yellow]{model}[/]  •  type: [cyan]{provider.Type}[/]")
            .Header("[bold]fsharp-research-agent[/]")
    panel.Border <- BoxBorder.Rounded
    AnsiConsole.Write(panel)

// ---------------------------------------------------------------------------
// Phase headers
// ---------------------------------------------------------------------------

let showPhase (n: int) (label: string) =
    AnsiConsole.WriteLine()
    AnsiConsole.Write(Rule($"[bold cyan]Phase {n} — {label}[/]"))

// ---------------------------------------------------------------------------
// Research plan display
// ---------------------------------------------------------------------------

let showPlan (plan: ResearchPlan) =
    let table = Table()
    table.AddColumn("[bold]#[/]")       |> ignore
    table.AddColumn("[bold]Sub-question[/]") |> ignore
    for t in plan.Tasks do
        table.AddRow($"[dim]{t.Id}[/]", t.Question) |> ignore
    table.Title  <- TableTitle($"[bold]Research Plan[/] — {Markup.Escape(plan.OriginalQuestion)}")
    table.Border <- TableBorder.Rounded
    AnsiConsole.Write(table)

// ---------------------------------------------------------------------------
// Progress line
// ---------------------------------------------------------------------------

let showProgress (msg: string) =
    AnsiConsole.MarkupLine($"[dim]{Markup.Escape(msg)}[/]")

// ---------------------------------------------------------------------------
// Token usage bar
// ---------------------------------------------------------------------------

let showTokenUsage (used: TokenUsage) (contextWindow: int) =
    let total = used.InputTokens + used.OutputTokens
    let pct   = float total / float contextWindow * 100.0
    let bar   = BreakdownChart()
    bar.AddItem("Input",  float used.InputTokens,        Color.SteelBlue) |> ignore
    bar.AddItem("Output", float used.OutputTokens,       Color.Gold1)     |> ignore
    bar.AddItem("Free",   float (contextWindow - total), Color.Grey)      |> ignore
    AnsiConsole.MarkupLine($"[dim]Context: {total:N0}/{contextWindow:N0} tokens ({pct:F1}%%)[/]")
    AnsiConsole.Write(bar)

// ---------------------------------------------------------------------------
// Token summary table (printed at end)
// ---------------------------------------------------------------------------

let showTokenSummary (total: TokenUsage) =
    AnsiConsole.WriteLine()
    AnsiConsole.Write(Rule("[bold]Token Usage Summary[/]"))
    let table = Table()
    table.AddColumn("[bold]Phase[/]")  |> ignore
    table.AddColumn("[bold]Tokens[/]") |> ignore
    table.AddRow("Input tokens",  $"{total.InputTokens:N0}")  |> ignore
    table.AddRow("Output tokens", $"{total.OutputTokens:N0}") |> ignore
    table.AddRow("[bold]Total[/]", $"[bold]{total.InputTokens + total.OutputTokens:N0}[/]") |> ignore
    table.Border <- TableBorder.Rounded
    AnsiConsole.Write(table)

// ---------------------------------------------------------------------------
// Final report path
// ---------------------------------------------------------------------------

let showReportSaved (path: string) =
    AnsiConsole.MarkupLine($"\n[bold green]Report saved →[/] [yellow]{Markup.Escape(path)}[/]")

// ---------------------------------------------------------------------------
// Error panel
// ---------------------------------------------------------------------------

let private errorDescription = function
    | ConfigErr  (FileNotFound p)        -> $"Config file not found: {p}"
    | ConfigErr  (ParseFailure m)        -> $"Config parse error: {m}"
    | ConfigErr  (MissingProvider n)     -> $"No provider named '{n}'"
    | ConfigErr  (UnresolvedEnvVar v)    -> $"Environment variable '${v}' is not set"
    | ProviderErr AuthenticationError    -> "Authentication failed — check your API key"
    | ProviderErr (NetworkFailure m)     -> $"Network error: {m}"
    | ProviderErr (ResponseParseError m) -> $"Response parse error: {m}"
    | ProviderErr (HttpError (s, b))     -> $"HTTP {s}: {b}"
    | ProviderErr (ModelNotFound m)      -> $"Model not found: {m}"
    | ProviderErr (ToolsNotSupported p)  -> $"Provider '{p}' does not support tools"
    | ToolErr    (ToolNotFound n)        -> $"Unknown tool: {n}"
    | ToolErr    (InvalidArguments(t,m)) -> $"Invalid arguments for tool '{t}': {m}"
    | ToolErr    (ExecutionFailed(t,m))  -> $"Tool '{t}' failed: {m}"
    | PlannerErr     m                   -> $"Planner failed: {m}"
    | ExecutorErr    m                   -> $"Executor failed: {m}"
    | SynthesizerErr m                   -> $"Synthesizer failed: {m}"
    | MaxTurnsExceeded                   -> "Maximum turn limit reached"
    | Unexpected m                       -> $"Unexpected error: {m}"

let showError (err: AppError) =
    let desc = errorDescription err
    let panel = Panel($"[red]{Markup.Escape(desc)}[/]\n\n[dim]Check providers.json and your .env file, then retry.[/]")
    panel.Header <- PanelHeader("[bold red]Error[/]")
    panel.Border <- BoxBorder.Heavy
    AnsiConsole.Write(panel)

// ---------------------------------------------------------------------------
// AgentEvent handler (used by Executor's per-task agent loop)
// ---------------------------------------------------------------------------

let handleAgentEvent (event: AgentEvent) : unit =
    match event with
    | Thinking                           -> ()
    | ReceivedResponse resp              -> showTokenUsage resp.TokensUsed 200_000
    | CallingTool call                   -> AnsiConsole.MarkupLine($"[dim]    ↳ tool: [cyan]{call.Name}[/][/]")
    | AgentEvent.ToolResult (call, _)    -> AnsiConsole.MarkupLine($"[dim]    ✓ {call.Name} returned[/]")
    | FinalResponse _                    -> ()
    | ErrorOccurred err                  -> showError err
