# CLAUDE.md — AI Assistant Context

## What this repo is

A provider-agnostic agentic framework in **F# on .NET 10**. Two runnable CLI apps (`agent-chat`, `research-agent`) share a core library (`AgentCore`). The design is functional throughout: no mutable state in domain code, no exceptions escaping the domain boundary, Railway-Oriented Programming via `AsyncResult<'a>`.

## Build and test commands

```bash
dotnet build src/core/agent-workflow-core.fsproj
dotnet build src/providers/providers.fsproj
dotnet build src/apps/agent-chat/agent-chat.fsproj
dotnet build src/apps/research-agent/research-agent.fsproj
dotnet test tests/AgentCoreTests/AgentCoreTests.fsproj
```

Run a specific app:
```bash
cd src/apps/agent-chat && dotnet run -- chat "hello"
cd src/apps/research-agent && dotnet run -- research "What is F#?"
```

## Critical conventions

### Error handling — never use exceptions in domain code
All fallible operations return `Result<'a, AppError>` or `AsyncResult<'a>` (`Async<Result<'a, AppError>>`).
Use `asyncResult { ... }` CE for async chains; `result { ... }` for pure chains.
New failure modes → add to `AppError` DU in `src/core/Error.fs`.

### No mutable bindings in domain code
`AgentState` and pipeline types are immutable records. Use `{ state with Field = newValue }` for updates.

### F# file compilation order is strict
Files in `.fsproj` must be listed before their consumers. When adding a file, check the order.

### No reflection in core library
`src/core` uses manual `System.Text.Json` mapping only — no `[<JsonPropertyName>]` attributes, no source generators.

## Key types (src/core/Types.fs)

```fsharp
type Message =
    | UserMessage | AssistantMessage | SystemMessage
    | ToolResultMessage of toolCallId: string * content: string
    | AssistantToolCall of calls: ToolCall list

type AgentState = { History: Message list; Turns: int; MaxTurns: int }

type InferenceRequest  = { Model: string; Messages: Message list; Tools: ToolDefinition list option; MaxTokens: int }
type InferenceResponse = { Content: ResponseContent; TokensUsed: TokenUsage; StopReason: StopReason }

type ToolSupport = Native | Emulated | NoTools
```

## Provider interface (src/core/Providers/IInferenceProvider.fs)

```fsharp
type IInferenceProvider =
    abstract Name        : string
    abstract ToolSupport : ToolSupport
    abstract Complete    : InferenceRequest -> AsyncResult<InferenceResponse>
```

Concrete implementations: `AnthropicProvider`, `OllamaProvider`, `OpenAICompatibleProvider`.
Factory: `ProviderFactory.fromConfig : ProviderConfig -> Result<IInferenceProvider, AppError>`.

## Agentic loop (src/core/Core.fs)

`Agent.Core.run config systemPrompt userMessage` — tail-recursive async loop.
Terminates on `EndTurn`, `MaxTokens`, or `MaxTurns` exceeded.
Tool calls go through `Tools.executeAll` which dispatches by name (no reflection).

## Research agent pipeline (src/apps/research-agent/)

Phase 1 — `Planner.run`: one LLM call → `ResearchPlan` (list of sub-questions as JSON).
Phase 2 — `Executor.runAll`: runs `Agent.Core.run` once per `PlanTask`; failures collected, not fatal.
Phase 3 — `Synthesizer.run`: one LLM call → markdown report written to `output/report.md`.

## Config (src/core/Config/)

`providers.json` holds provider list + `activeProvider`. `apiKey`/`baseUrl` support `${ENV_VAR}` placeholders resolved from `.env` or the environment at load time. `Config.load` returns `Result<AppConfig, AppError>` — no exceptions.

## Testing (tests/AgentCoreTests/)

Use `MockProvider` (implements `IInferenceProvider`) to replay canned responses. No network needed.
```fsharp
let mock = MockProvider([Ok (Responses.text "Hello"); Ok (Responses.toolCall "calculator" [("expression","1+1")])])
```

## What to do when adding features

- **New provider**: implement `IInferenceProvider` in `src/providers/`, add to `ProviderFactory.fromConfig`, add to `.fsproj` before `ProviderFactory.fs`.
- **New tool**: add `ToolDefinition` + handler + dispatch arm in `src/core/Tools.fs`, add to `allTools`.
- **New error case**: add to `AppError` in `src/core/Error.fs`, add convenience lift, handle at CLI boundary.
- **New app**: reference `agent-workflow-core.fsproj` and `providers.fsproj`; use `Config.load` + `ProviderFactory.fromConfig` + `Agent.Core.run`.
