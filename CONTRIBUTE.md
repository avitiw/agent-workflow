# Contributing

## Project Conventions

### Language and runtime
- F# on .NET 10. All source files are `.fs`; project files are `.fsproj`. No `.cs` files.
- Target framework: `net10.0`.

### Error handling
- **No exceptions in domain code.** All fallible operations return `Result<'a, AppError>` or `AsyncResult<'a>`.
- Use the `asyncResult { ... }` computation expression for async chains, `result { ... }` for pure chains.
- New failure modes belong in the `AppError` discriminated union in `src/core/Error.fs`. Add a sub-union if the new category warrants it (see `ConfigError`, `ProviderError`, `ToolError`).
- Provide a convenience lift at the bottom of `Error.fs` (e.g. `let myErr m = Error (MyErr m)`).

### Immutability
- No `mutable` bindings outside of tight performance-critical loops or interop with imperative .NET APIs.
- `AgentState` and all pipeline types (`ResearchPlan`, `TaskResult`, `FinalReport`) are immutable records. State transitions return new records.

### Module and file order
F# compilation is order-dependent. Files in `.fsproj` must be listed before any file that references them. When adding a file, place it in the `<ItemGroup>` before its first consumer.

### Naming
- Modules: `PascalCase`. Files: `PascalCase.fs` matching the module name.
- Functions: `camelCase`. Types: `PascalCase`. Constants/values: `camelCase`.
- Private helpers are marked `let private`.

### No reflection / source generators in the core library
`src/core` uses only `System.Text.Json` with manual mapping. This keeps the library trimmer-friendly and makes the data path explicit.

---

## Setting Up for Development

```bash
# Clone
git clone <repo-url>
cd agent-workflow

# Build everything
dotnet build src/core/agent-workflow-core.fsproj
dotnet build src/providers/providers.fsproj
dotnet build src/apps/agent-chat/agent-chat.fsproj
dotnet build src/apps/research-agent/research-agent.fsproj

# Run tests
dotnet test tests/AgentCoreTests/AgentCoreTests.fsproj
```

---

## Testing

Tests live in `tests/AgentCoreTests/` and use xUnit.

### MockProvider

`MockProvider.fs` contains a deterministic `IInferenceProvider` that replays a pre-loaded list of `Result<InferenceResponse, AppError>` values. Use it for all agent loop tests — no network required.

```fsharp
let mock = MockProvider([
    Ok (Responses.toolCall "calculator" [("expression", "6 * 7")])
    Ok (Responses.text "The answer is 42.")
])
```

### What to test
- Agent loop behaviour: `EndTurn`, `ToolUse`, `MaxTurns` exceeded, system prompt forwarding.
- Tool handlers: pure input/output where possible; use temp files for `read_file` / `write_file`.
- Config loading: valid JSON, missing fields, unresolved env vars.
- Provider response parsing: happy path and error cases.

### What not to test with mocks
- Actual HTTP calls to Anthropic/Ollama/OpenAI. These belong in integration tests (not currently in this repo) that require live credentials.

---

## Adding a Provider

1. Create `src/providers/MyProvider.fs`:

```fsharp
module AgentCore.Providers.MyProvider

open AgentCore.Core.Error
open AgentCore.Types
open AgentCore.Providers.IInferenceProvider

type MyProvider(apiKey: string, ?httpClient: HttpClient) =
    let client = defaultArg httpClient (new HttpClient())

    interface IInferenceProvider with
        member _.Name        = "MyProvider"
        member _.ToolSupport = Native   // or Emulated or NoTools

        member _.Complete(req: InferenceRequest) : AsyncResult<InferenceResponse> =
            asyncResult {
                // ... build request, call API, parse response
            }

let create (apiKey: string) : IInferenceProvider =
    MyProvider(apiKey) :> IInferenceProvider
```

2. Add to `src/providers/providers.fsproj` **before** `ProviderFactory.fs`:

```xml
<Compile Include="MyProvider.fs" />
```

3. Add a match arm in `ProviderFactory.fromConfig`:

```fsharp
| "MyProvider" ->
    cfg.ApiKey
    |> Result.ofOption (ConfigErr (ParseFailure $"Provider '{cfg.Name}' requires apiKey"))
    |> Result.map MyProvider.create
```

4. Add a provider entry in `providers.json`.

---

## Adding a Tool

1. Define the schema in `src/core/Tools.fs`:

```fsharp
let myTool : ToolDefinition = {
    Name        = "my_tool"
    Description = "What this tool does."
    Parameters  = Map [
        "param1", { Type = "string"; Description = "..."; Required = true; EnumValues = None }
    ]
    Required    = ["param1"]
}
```

2. Add the handler:

```fsharp
let private handleMyTool (input: Dictionary<string, obj>) : AsyncResult<ToolResult> =
    asyncResult {
        let! param1 = getArg "param1" input
        // ...
        return TextResult "result"
    }
```

3. Add a dispatch arm:

```fsharp
| "my_tool" -> handleMyTool call.Input
```

4. Add to `allTools`:

```fsharp
let allTools : ToolDefinition list = [...; myTool]
```

No changes to `AgentRunConfig` or the CLI are needed. The tool is available to any agent that passes `allTools`.

---

## Pull Request Checklist

- [ ] `dotnet build` succeeds with no warnings.
- [ ] `dotnet test` passes.
- [ ] New failure modes are typed errors on the `Error` rail, not exceptions or string messages.
- [ ] No `mutable` bindings added without justification.
- [ ] New files are added to the `.fsproj` in the correct order.
- [ ] Secrets are never hardcoded; `${ENV_VAR}` placeholders are used in `providers.json`.
- [ ] Public API surface is minimal — keep helpers `private`.

---

## File Reference

| Path | Purpose |
|---|---|
| `src/core/Types.fs` | All shared domain types |
| `src/core/Error.fs` | `AppError` DU, `AsyncResult` CE, `Result` CE |
| `src/core/Memory.fs` | Sliding-window history management |
| `src/core/Tools.fs` | Tool schemas and handlers |
| `src/core/Providers/IInferenceProvider.fs` | Provider interface |
| `src/core/Config/ConfigTypes.fs` | `ProviderConfig`, `AppConfig` |
| `src/core/Config/Config.fs` | Config loading (`.env` + `providers.json`) |
| `src/core/Core.fs` | Agentic loop (`run`, `step`, `loop`) |
| `src/providers/AnthropicProvider.fs` | Anthropic API implementation |
| `src/providers/OllamaProvider.fs` | Ollama implementation |
| `src/providers/OpenAICompatibleProvider.fs` | Generic OpenAI-compat implementation |
| `src/providers/ProviderFactory.fs` | `fromConfig` factory function |
| `tests/AgentCoreTests/MockProvider.fs` | Test double for `IInferenceProvider` |
