# F# Agent Workflow

A provider-agnostic agentic framework written in F# on .NET 10. Ships two CLI applications — an interactive chat agent and a multi-phase research agent — built on a shared core library that implements Railway-Oriented Programming throughout.

## Key Design Decisions

- **Railway-Oriented Programming** — every effectful operation returns `AsyncResult<'a>` (`Async<Result<'a, AppError>>`). No exceptions escape the domain boundary.
- **Provider abstraction** — the agent core never sees HTTP. All LLM backends implement `IInferenceProvider` with a single `Complete` method.
- **Tool support levels** — `Native` (structured protocol), `Emulated` (schema injected into system prompt, JSON parsed from reply), `NoTools`.
- **Immutable state** — `AgentState` is a plain record. Each loop iteration produces a new state via `with` updates.
- **Config separation** — secrets live in `.env`; structured config (provider list, active provider) lives in `providers.json`. `${ENV_VAR}` placeholders in JSON are resolved at load time.

## Repository Layout

```
agent-workflow/
├── src/
│   ├── core/                        # AgentCore library (no executables)
│   │   ├── Types.fs                 # Message, ToolCall, AgentState, InferenceRequest/Response
│   │   ├── Error.fs                 # AppError DU + AsyncResult CE + Result CE
│   │   ├── Memory.fs                # Pure sliding-window history management
│   │   ├── Tools.fs                 # Tool schemas + handlers (web_search, read_file, write_file, calculator)
│   │   ├── Providers/
│   │   │   └── IInferenceProvider.fs  # Provider interface
│   │   └── Config/
│   │       ├── ConfigTypes.fs       # ProviderConfig, AppConfig records
│   │       └── Config.fs            # .env + providers.json loading on the Ok rail
│   │
│   ├── providers/                   # Concrete provider implementations
│   │   ├── AnthropicProvider.fs     # POST /v1/messages — native tool use
│   │   ├── OllamaProvider.fs        # POST /v1/chat/completions — native tool use
│   │   ├── OpenAICompatibleProvider.fs  # Generic OpenAI-compat (OpenAI, Groq, Mistral, etc.)
│   │   └── ProviderFactory.fs       # fromConfig: ProviderConfig → IInferenceProvider
│   │
│   └── apps/
│       ├── agent-chat/              # Interactive chat CLI
│       │   ├── Display.fs           # Spectre.Console rendering
│       │   ├── Commands.fs          # chat / providers / models commands
│       │   └── Program.fs           # Entry point
│       │
│       └── research-agent/          # Three-phase research CLI
│           ├── Planner/
│           │   ├── PlanTypes.fs     # ResearchPlan, TaskResult, FinalReport
│           │   └── Planner.fs       # Phase 1: LLM call → JSON sub-question list
│           ├── Executor/
│           │   └── Executor.fs      # Phase 2: run agent loop per task
│           ├── Synthesizer/
│           │   └── Synthesizer.fs   # Phase 3: LLM call → markdown report
│           ├── Display.fs
│           ├── Commands.fs
│           └── Program.fs
│
└── tests/
    └── AgentCoreTests/
        ├── MockProvider.fs          # Deterministic IInferenceProvider for tests
        └── AgentLoopTests.fs        # xUnit tests for the agentic loop
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An API key for at least one provider **or** a local [Ollama](https://ollama.ai) instance

## Quick Start

### 1. Configure providers

Copy the sample config into each app directory and edit it:

```bash
cp src/apps/agent-chat/providers.json .env.example   # template only
```

`providers.json` (per-app, already present in `src/apps/*/providers.json`):

```json
{
  "providers": [
    {
      "name": "anthropic",
      "type": "Anthropic",
      "apiKey": "${ANTHROPIC_API_KEY}",
      "defaultModel": "claude-haiku-4-5"
    },
    {
      "name": "ollama-local",
      "type": "Ollama",
      "baseUrl": "http://localhost:11434",
      "defaultModel": "qwen3:8b"
    },
    {
      "name": "groq",
      "type": "OpenAICompatible",
      "baseUrl": "https://api.groq.com/openai",
      "apiKey": "${GROQ_API_KEY}",
      "defaultModel": "llama-3.3-70b-versatile"
    }
  ],
  "activeProvider": "ollama-local"
}
```

Create `.env` in the app's working directory with your secrets:

```
ANTHROPIC_API_KEY=sk-ant-...
GROQ_API_KEY=gsk_...
```

### 2. Build

```bash
dotnet build src/core/agent-workflow-core.fsproj
dotnet build src/providers/providers.fsproj
dotnet build src/apps/agent-chat/agent-chat.fsproj
dotnet build src/apps/research-agent/research-agent.fsproj
```

### 3. Run the chat agent

```bash
# Single-turn
cd src/apps/agent-chat
dotnet run -- chat "What is the capital of France?"

# Interactive REPL
dotnet run -- chat

# Use a specific provider / model
dotnet run -- chat --provider anthropic --model claude-opus-4-6 "Explain monads"

# Disable tools
dotnet run -- chat --no-tools "Tell me a joke"

# List configured providers
dotnet run -- providers

# List Ollama models
dotnet run -- models --provider ollama-local
```

### 4. Run the research agent

```bash
cd src/apps/research-agent
dotnet run -- research "What are the key differences between F# and Haskell?"

# Use different providers for different phases
dotnet run -- research "History of functional programming" \
  --planner-provider anthropic \
  --provider ollama-local \
  --synthesizer-provider groq
```

Output is written to `output/report.md`.

### 5. Run tests

```bash
dotnet test tests/AgentCoreTests/AgentCoreTests.fsproj
```

## Providers

| Type | `providers.json` `"type"` | Auth | Tool Support |
|---|---|---|---|
| Anthropic | `"Anthropic"` | `apiKey` | Native |
| Ollama (local) | `"Ollama"` | None | Native |
| OpenAI | `"OpenAICompatible"` | `apiKey` | Native |
| Groq | `"OpenAICompatible"` | `apiKey` | Native |
| Mistral | `"OpenAICompatible"` | `apiKey` | Native |
| Together AI | `"OpenAICompatible"` | `apiKey` | Native |
| Any OpenAI-compat | `"OpenAICompatible"` | optional | Native |

## Built-in Tools

| Tool | Description |
|---|---|
| `web_search` | DuckDuckGo instant answers + Wikipedia fallback |
| `read_file` | Read a file from the local filesystem |
| `write_file` | Write content to a file (creates directories as needed) |
| `calculator` | Evaluate arithmetic expressions (`+`, `-`, `*`, `/`, `^`, parentheses) |

## Architecture Overview

```
CLI (Commands.fs)
  │
  ├── Config.load → AppConfig
  ├── ProviderFactory.fromConfig → IInferenceProvider
  │
  └── Agent.Core.run
        │
        └── loop (tail-recursive AsyncResult)
              │
              ├── step → provider.Complete(InferenceRequest)
              │           └── parses InferenceResponse
              │
              ├── EndTurn / MaxTokens / StopSequence → return final text
              │
              └── ToolUse → Tools.executeAll → append results → loop
```

The research agent wraps `Agent.Core.run` in a three-phase pipeline:

```
Planner.run  →  ResearchPlan  (list of sub-questions)
    │
    ▼
Executor.runAll  →  TaskResult list  (one agent run per sub-question)
    │
    ▼
Synthesizer.run  →  FinalReport  (markdown written to output/report.md)
```

## Adding a New Provider

1. Create `src/providers/MyProvider.fs` implementing `IInferenceProvider`.
2. Add `<Compile Include="MyProvider.fs" />` to `src/providers/providers.fsproj` (before `ProviderFactory.fs`).
3. Add a match arm in `ProviderFactory.fromConfig` for the new `"type"` string.
4. Add an entry to `providers.json`.

No changes to the agent core or CLI are needed.

## Adding a New Tool

1. Define a `ToolDefinition` value in `src/core/Tools.fs`.
2. Add a handler function (`handleMyTool`).
3. Add a match arm in `dispatch`.
4. Add the tool to `allTools`.

The tool is automatically available to all agents that pass `allTools` in `AgentRunConfig.Tools`.

## Error Handling

All errors are typed `AppError` discriminated unions:

```fsharp
type AppError =
    | ConfigErr   of ConfigError
    | ProviderErr of ProviderError
    | ToolErr     of ToolError
    | PlannerErr  of message: string
    | ExecutorErr of message: string
    | SynthesizerErr of message: string
    | MaxTurnsExceeded
    | Unexpected  of message: string
```

Errors flow on the `Error` rail of `AsyncResult<'a>` and are pattern-matched exhaustively at the CLI boundary.

## License

See [LICENSE](LICENSE).
