/// CLI command definitions using Spectre.Console.Cli.
/// Each command parses its settings, loads config on the Ok rail, then
/// delegates to the agent core or display module.
module AgentCore.CLI.Commands

open System
open System.ComponentModel
open System.Threading
open Spectre.Console.Cli
open AgentCore.Core.Error
open AgentCore.Config.Config
open AgentCore.Config.ConfigTypes
open AgentCore.Agent.Core
open AgentCore.Agent.Tools
open Agent.Chat.CLI.Display
open AgentCore.Providers.ProviderFactory
open AgentCore.Providers.OllamaProvider

// ---------------------------------------------------------------------------
// Shared settings base
// ---------------------------------------------------------------------------

type BaseSettings() =
    inherit CommandSettings()

    [<CommandOption("--providers-file")>]
    [<Description("Path to providers.json (default: ./providers.json)")>]
    [<DefaultValue("providers.json")>]
    member val ProvidersFile = "providers.json" with get, set

    [<CommandOption("--provider|-p")>]
    [<Description("Override the active provider")>]
    member val ProviderOverride : string = null with get, set

    [<CommandOption("--model|-m")>]
    [<Description("Override the model for the selected provider")>]
    member val ModelOverride : string = null with get, set

// ---------------------------------------------------------------------------
// chat command
// ---------------------------------------------------------------------------

type ChatSettings() =
    inherit BaseSettings()

    [<CommandArgument(0, "[prompt]")>]
    [<Description("Initial user message (omit to enter interactive REPL)")>]
    member val Prompt : string = null with get, set

    [<CommandOption("--system")>]
    [<Description("System prompt text")>]
    member val SystemPrompt : string = null with get, set

    [<CommandOption("--no-tools")>]
    [<Description("Disable tool use for this session")>]
    member val NoTools = false with get, set

type ChatCommand() =
    inherit Command<ChatSettings>()

    override _.Execute(ctx: CommandContext, settings: ChatSettings, _ct: CancellationToken) =
        let providerOverride = settings.ProviderOverride |> Option.ofObj
        let modelOverride    = settings.ModelOverride    |> Option.ofObj
        let systemPrompt     = settings.SystemPrompt     |> Option.ofObj

        let result =
            asyncResult {
                let! cfg      = load settings.ProvidersFile
                let! provCfg  = resolveActive providerOverride cfg
                let  model    = modelOverride |> Option.defaultValue provCfg.DefaultModel
                let! provider = fromConfigWithModel modelOverride provCfg

                showStartupBanner provCfg model

                let tools = if settings.NoTools then [] else allTools
                let runConfig : AgentRunConfig = {
                    Provider = provider
                    Model    = model
                    Tools    = tools
                    MaxTurns = cfg.MaxTurns
                    OnStep   = handleAgentEvent
                }

                if settings.Prompt <> null then
                    let! _ = run runConfig systemPrompt settings.Prompt
                    ()
                else
                    // Interactive REPL — recursive so asyncResult CE stays clean
                    let rec repl () : AsyncResult<unit> =
                        async {
                            printf "> "
                            let userInput = Console.ReadLine()
                            if userInput = null || userInput.Trim().ToLower() = "/exit" then
                                return Ok ()
                            else
                                let! runResult = run runConfig systemPrompt userInput
                                match runResult with
                                | Error e -> showError e
                                | Ok _    -> ()
                                return! repl ()
                        }
                    return! repl ()
            }
            |> Async.RunSynchronously

        match result with
        | Ok _    -> 0
        | Error e -> showError e; 1

// ---------------------------------------------------------------------------
// providers command
// ---------------------------------------------------------------------------

type ProvidersSettings() =
    inherit BaseSettings()

type ProvidersCommand() =
    inherit Command<ProvidersSettings>()

    override _.Execute(_ctx: CommandContext, settings: ProvidersSettings, _ct: CancellationToken) =
        match load settings.ProvidersFile with
        | Error e   -> showError e; 1
        | Ok cfg    -> showProvidersTable cfg.Providers; 0

// ---------------------------------------------------------------------------
// models command
// ---------------------------------------------------------------------------

type ModelsSettings() =
    inherit BaseSettings()

type ModelsCommand() =
    inherit Command<ModelsSettings>()

    override _.Execute(_ctx: CommandContext, settings: ModelsSettings, _ct: CancellationToken) =
        let providerOverride = settings.ProviderOverride |> Option.ofObj

        let result =
            asyncResult {
                let! cfg     = load settings.ProvidersFile
                let! provCfg = resolveActive providerOverride cfg

                if provCfg.Type <> "Ollama" then
                    return! AsyncResult.err
                        (ConfigErr (ParseFailure $"--models only works with Ollama providers (got '{provCfg.Type}')"))
                else
                    let baseUrl = provCfg.BaseUrl |> Option.defaultValue "http://localhost:11434"
                    use client  = new System.Net.Http.HttpClient()
                    let! models = listModels baseUrl client
                    showModelList provCfg.Name models
                    return ()
            }
            |> Async.RunSynchronously

        match result with
        | Ok _    -> 0
        | Error e -> showError e; 1
