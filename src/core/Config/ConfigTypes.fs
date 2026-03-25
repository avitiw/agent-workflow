/// Configuration record types.
/// Kept separate from loading logic so they can be opened by any module
/// without pulling in IO or JSON dependencies.
module AgentCore.Config.ConfigTypes

// ---------------------------------------------------------------------------
// Raw JSON shape (providers.json)
// ---------------------------------------------------------------------------

/// One entry in the "providers" array of providers.json.
/// apiKey / baseUrl may contain "${ENV_VAR}" placeholders resolved at load-time.
/// Separating raw shape from resolved shape keeps the resolution logic explicit
/// and makes untrusted JSON easy to validate before use.
type ProviderConfig = {
    Name         : string
    Type         : string         // "Anthropic" | "Ollama" | "OpenAICompatible"
    BaseUrl      : string option
    ApiKey       : string option  // resolved at load time from environment
    DefaultModel : string
}

/// Top-level providers.json shape.
type ProvidersFile = {
    Providers      : ProviderConfig list
    ActiveProvider : string
}

// ---------------------------------------------------------------------------
// Resolved runtime config
// ---------------------------------------------------------------------------

type AppConfig = {
    Providers      : ProviderConfig list
    ActiveProvider : string
    MaxTurns       : int
}
