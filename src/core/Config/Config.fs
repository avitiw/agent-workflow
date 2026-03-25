/// Configuration loading via Railway Oriented Programming.
/// Every step returns Result so failures compose without exceptions.
///
/// Design note — why JSON config, not .env for structured data:
///   .env is the right home for secrets (flat key=value, excluded from VCS).
///   providers.json holds structured, non-secret config (lists, typed fields,
///   nested objects).  Mixing them would require ugly key conventions and
///   duplicate schema knowledge.  Placeholders like ${ANTHROPIC_API_KEY} in
///   providers.json let the two files cooperate without leaking secrets.
module AgentCore.Config.Config

open System
open System.IO
open System.Text.Json
open dotenv.net
open AgentCore.Core.Error
open AgentCore.Config.ConfigTypes

// ---------------------------------------------------------------------------
// Environment-variable placeholder resolution  (pure, no IO)
// ---------------------------------------------------------------------------

/// "${VAR}" → resolved value, or Error if the variable is unset.
let private resolveEnvPlaceholder (value: string) : Result<string, AppError> =
    if value.StartsWith("${") && value.EndsWith("}") then
        let varName = value[2 .. value.Length - 2]
        match Environment.GetEnvironmentVariable(varName) with
        | null | "" -> Error (ConfigErr (UnresolvedEnvVar varName))
        | resolved  -> Ok resolved
    else
        Ok value   // literal string, pass through

let private resolveOptional (opt: string option) : Result<string option, AppError> =
    match opt with
    | None   -> Ok None
    | Some v -> resolveEnvPlaceholder v |> Result.map Some

// ---------------------------------------------------------------------------
// JSON parsing — manual mapping avoids reflection / source generators
// ---------------------------------------------------------------------------

let private str (name: string) (el: JsonElement) : Result<string, AppError> =
    match el.TryGetProperty(name) with
    | true, v when v.ValueKind <> JsonValueKind.Null -> Ok (v.GetString())
    | _ -> Error (ConfigErr (ParseFailure $"Missing required field '{name}'"))

let private optStr (name: string) (el: JsonElement) : Result<string option, AppError> =
    match el.TryGetProperty(name) with
    | true, v when v.ValueKind <> JsonValueKind.Null -> Ok (Some (v.GetString()))
    | _ -> Ok None

let private parseProviderConfig (el: JsonElement) : Result<ProviderConfig, AppError> =
    result {
        let! name    = str "name"         el
        let! ptype   = str "type"         el
        let! baseUrl = optStr "baseUrl"   el
        let! apiKey  = optStr "apiKey"    el
        let! model   = str "defaultModel" el
        return {
            Name         = name
            Type         = ptype
            BaseUrl      = baseUrl
            ApiKey       = apiKey
            DefaultModel = model
        }
    }

let private parseProvidersFile (json: string) : Result<ProvidersFile, AppError> =
    try
        use doc = JsonDocument.Parse(json)
        let root = doc.RootElement

        let providerResults =
            root.GetProperty("providers").EnumerateArray()
            |> Seq.map parseProviderConfig
            |> Seq.toList

        let activeResult =
            match root.TryGetProperty("activeProvider") with
            | true, v -> Ok (v.GetString())
            | _       -> Error (ConfigErr (ParseFailure "Missing 'activeProvider' field"))

        result {
            let! providers = Result.sequence providerResults
            let! active    = activeResult
            return { Providers = providers; ActiveProvider = active }
        }
    with ex ->
        Error (ConfigErr (ParseFailure $"JSON parse error: {ex.Message}"))

// ---------------------------------------------------------------------------
// Env-var resolution over all provider configs
// ---------------------------------------------------------------------------

let private resolveProvider (p: ProviderConfig) : Result<ProviderConfig, AppError> =
    result {
        let! apiKey  = resolveOptional p.ApiKey
        let! baseUrl = resolveOptional p.BaseUrl
        return { p with ApiKey = apiKey; BaseUrl = baseUrl }
    }

// ---------------------------------------------------------------------------
// Public API  (Result-returning, no exceptions)
// ---------------------------------------------------------------------------

/// Load config from .env (optional) and providers.json (required).
/// Returns a fully resolved AppConfig on the Ok rail, or a typed ConfigError.
let load (providersJsonPath: string) : Result<AppConfig, AppError> =
    // .env is optional — CI/CD envs set variables directly
    DotEnv.Load(DotEnvOptions(envFilePaths = [| ".env" |], ignoreExceptions = true))

    result {
        do! if File.Exists(providersJsonPath) then Ok ()
            else Error (ConfigErr (FileNotFound providersJsonPath))

        let  json   = File.ReadAllText(providersJsonPath)
        let! parsed = parseProvidersFile json

        return {
            Providers      = parsed.Providers
            ActiveProvider = parsed.ActiveProvider
            MaxTurns       = 20
        }
    }

/// Look up a provider by name, returning MissingProvider on miss.
let findProvider (name: string) (cfg: AppConfig) : Result<ProviderConfig, AppError> =
    cfg.Providers
    |> List.tryFind (fun p -> p.Name = name)
    |> Result.ofOption (ConfigErr (MissingProvider name))

/// Resolve the active provider (or the override from --provider flag).
/// Env-var placeholders are expanded here — only for the selected provider,
/// so missing keys for other providers don't cause failures.
let resolveActive (overrideName: string option) (cfg: AppConfig) : Result<ProviderConfig, AppError> =
    let name = overrideName |> Option.defaultValue cfg.ActiveProvider
    result {
        let! raw = findProvider name cfg
        return! resolveProvider raw
    }
