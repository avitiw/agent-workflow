/// Constructs IInferenceProvider instances from ProviderConfig.
/// Provider selection lives entirely in config — no hardcoded provider
/// logic in the agent core.  Adding a new provider type is a config
/// entry (type = "OpenAICompatible") plus a ProviderConfig row.
module AgentCore.Providers.ProviderFactory

open AgentCore.Core.Error
open AgentCore.Config.ConfigTypes
open AgentCore.Providers.IInferenceProvider
open AgentCore.Providers.AnthropicProvider
open AgentCore.Providers.OllamaProvider
open AgentCore.Providers.OpenAICompatibleProvider

// ---------------------------------------------------------------------------
// Factory — pure function, no side effects
// ---------------------------------------------------------------------------

/// Build an IInferenceProvider from a resolved ProviderConfig.
/// Returns Error on the rail if required fields are missing.
let fromConfig (cfg: ProviderConfig) : Result<IInferenceProvider, AppError> =
    match cfg.Type with

    | "Anthropic" ->
        cfg.ApiKey
        |> Result.ofOption (ConfigErr (ParseFailure $"Provider '{cfg.Name}' (Anthropic) requires apiKey"))
        |> Result.map (fun key -> AnthropicProvider.create key)

    | "Ollama" ->
        let baseUrl = cfg.BaseUrl |> Option.defaultValue "http://localhost:11434"
        Ok (OllamaProvider.create baseUrl cfg.DefaultModel)

    | "OpenAICompatible" ->
        let baseUrl =
            cfg.BaseUrl
            |> Result.ofOption (ConfigErr (ParseFailure $"Provider '{cfg.Name}' (OpenAICompatible) requires baseUrl"))
        baseUrl
        |> Result.map (fun url ->
            OpenAICompatibleProvider.create cfg.Name url cfg.DefaultModel cfg.ApiKey)

    | unknown ->
        Error (ConfigErr (ParseFailure $"Unknown provider type '{unknown}' for provider '{cfg.Name}'"))

/// Build a provider and override the model if one was supplied via CLI.
let fromConfigWithModel (modelOverride: string option) (cfg: ProviderConfig) : Result<IInferenceProvider, AppError> =
    let effectiveCfg =
        match modelOverride with
        | Some m -> { cfg with DefaultModel = m }
        | None   -> cfg
    fromConfig effectiveCfg

/// Build all providers from config, collecting both successes and failures.
/// Failures are returned alongside successes so the CLI can display which
/// providers are misconfigured without aborting startup.
let buildAll (cfgs: ProviderConfig list) : (IInferenceProvider list * (string * AppError) list) =
    cfgs
    |> List.map (fun cfg ->
        match fromConfig cfg with
        | Ok p    -> Choice1Of2 p
        | Error e -> Choice2Of2 (cfg.Name, e))
    |> List.fold
        (fun (ok, err) choice ->
            match choice with
            | Choice1Of2 p     -> (p :: ok, err)
            | Choice2Of2 (n,e) -> (ok, (n, e) :: err))
        ([], [])
    |> fun (ok, err) -> (List.rev ok, List.rev err)
