/// CLI command definitions using Spectre.Console.Cli.
/// Single `research` command orchestrates the three-phase pipeline.
module AgentResearch.CLI.Commands

open System.ComponentModel
open System.Threading
open Spectre.Console.Cli

open AgentCore.Types
open AgentCore.Core.Error
open AgentCore.Config.Config 
open AgentCore.Agent.Core
open AgentCore.Agent.Tools
open AgentResearch.CLI.Display
open AgentCore.Providers.ProviderFactory
open AgentCore.Providers.OllamaProvider
open AgentResearch.Planner.PlanTypes
// open AgentResearch.Core.Error
// 
// open AgentResearch.Config.Config
// open AgentResearch.Providers.ProviderFactory

// open AgentResearch.CLI.Display

// Module aliases avoid ambiguity between namespace and module of the same name
module PlannerApi    = AgentResearch.Planner.Planner
module ExecutorApi   = AgentResearch.Executor.Executor
module SynthesizerApi = AgentResearch.Synthesizer.Synthesizer

// ---------------------------------------------------------------------------
// Settings
// ---------------------------------------------------------------------------

type ResearchSettings() =
    inherit CommandSettings()

    [<CommandArgument(0, "<question>")>]
    [<Description("The research question to investigate")>]
    member val Question : string = null with get, set

    [<CommandOption("--providers-file")>]
    [<Description("Path to providers.json (default: ./providers.json)")>]
    [<DefaultValue("providers.json")>]
    member val ProvidersFile = "providers.json" with get, set

    [<CommandOption("--provider|-p")>]
    [<Description("Provider for all phases")>]
    member val ProviderOverride : string = null with get, set

    [<CommandOption("--planner-provider")>]
    [<Description("Override provider for Phase 1 (Planner)")>]
    member val PlannerProvider : string = null with get, set

    [<CommandOption("--synthesizer-provider")>]
    [<Description("Override provider for Phase 3 (Synthesizer)")>]
    member val SynthesizerProvider : string = null with get, set

    [<CommandOption("--model|-m")>]
    [<Description("Override the model for all phases")>]
    member val ModelOverride : string = null with get, set

    [<CommandOption("--max-turns")>]
    [<Description("Max agent turns per task (default: 10)")>]
    [<DefaultValue(10)>]
    member val MaxTurns = 10 with get, set

// ---------------------------------------------------------------------------
// research command
// ---------------------------------------------------------------------------

type ResearchCommand() =
    inherit Command<ResearchSettings>()

    override _.Execute(_ctx: CommandContext, settings: ResearchSettings, _ct: CancellationToken) =
        let providerOverride    = settings.ProviderOverride    |> Option.ofObj
        let plannerOverride     = settings.PlannerProvider     |> Option.ofObj
        let synthOverride       = settings.SynthesizerProvider |> Option.ofObj
        let modelOverride       = settings.ModelOverride       |> Option.ofObj

        let result =
            asyncResult {
                let! cfg = load settings.ProvidersFile

                // Resolve providers for each phase (fall back to global --provider, then activeProvider)
                let! mainProvCfg  = resolveActive providerOverride cfg
                let! planProvCfg  = resolveActive (plannerOverride  |> Option.orElse providerOverride) cfg
                let! synthProvCfg = resolveActive (synthOverride    |> Option.orElse providerOverride) cfg

                let model = modelOverride |> Option.defaultValue mainProvCfg.DefaultModel

                let! mainProvider  = fromConfigWithModel modelOverride mainProvCfg
                let! planProvider  = fromConfigWithModel modelOverride planProvCfg
                let! synthProvider = fromConfigWithModel modelOverride synthProvCfg

                showStartupBanner mainProvCfg model

                // ── Phase 1: Plan ─────────────────────────────────────────
                showPhase 1 "Planning"
                let! (plan : ResearchPlan) = PlannerApi.run planProvider model settings.Question
                showPlan plan

                // ── Phase 2: Execute ──────────────────────────────────────
                showPhase 2 "Executing"
                let execConfig : ExecutorApi.ExecutorConfig = {
                    Provider   = mainProvider
                    Model      = model
                    MaxTurns   = settings.MaxTurns
                    OnProgress = showProgress
                }
                let! (results : TaskResult list) = ExecutorApi.runAll execConfig plan

                // Accumulate tokens from all task results
                let executorTokens =
                    results |> List.fold (fun acc r ->
                        { InputTokens  = acc.InputTokens  + r.Tokens.InputTokens
                          OutputTokens = acc.OutputTokens + r.Tokens.OutputTokens }
                    ) { InputTokens = 0; OutputTokens = 0 }

                // ── Phase 3: Synthesize ───────────────────────────────────
                showPhase 3 "Synthesizing"
                let! (report : FinalReport) = SynthesizerApi.run synthProvider model plan results executorTokens

                showReportSaved report.OutputPath
                showTokenSummary report.TotalTokens

                return ()
            }
            |> Async.RunSynchronously

        match result with
        | Ok _    -> 0
        | Error e -> showError e; 1
