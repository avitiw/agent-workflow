/// Phase 2 — Executor.
/// Runs the existing agentic loop (Agent.Core.run) once per PlanTask.
/// Each task is independent — failures are collected as ExecutorErr rather than
/// aborting the whole pipeline.
module AgentResearch.Executor.Executor

open AgentCore.Core.Error
open AgentCore.Types
open AgentCore.Agent.Tools
open AgentCore.Agent.Core
open AgentCore.Providers.IInferenceProvider
open AgentResearch.Planner.PlanTypes

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

type ExecutorConfig = {
    Provider   : IInferenceProvider
    Model      : string
    MaxTurns   : int
    OnProgress : string -> unit   // side-effecting progress callback for CLI
}

// ---------------------------------------------------------------------------
// Single-task runner
// ---------------------------------------------------------------------------

let private taskSystemPrompt =
    "You are a focused research assistant. Answer the given sub-question thoroughly \
     using the tools available (web_search, read_url). Be concise but complete."

/// Run one PlanTask through the agent loop and return a TaskResult.
let runTask (config: ExecutorConfig) (task: PlanTask) : AsyncResult<TaskResult> =
    asyncResult {
        config.OnProgress $"  Running [{task.Id}]: {task.Question}"

        let mutable tokensAccumulated = { InputTokens = 0; OutputTokens = 0 }

        let runConfig : AgentRunConfig = {
            Provider = config.Provider
            Model    = config.Model
            Tools    = allTools
            MaxTurns = config.MaxTurns
            OnStep   = fun event ->
                match event with
                | ReceivedResponse resp ->
                    tokensAccumulated <- {
                        InputTokens  = tokensAccumulated.InputTokens  + resp.TokensUsed.InputTokens
                        OutputTokens = tokensAccumulated.OutputTokens + resp.TokensUsed.OutputTokens
                    }
                | CallingTool call ->
                    config.OnProgress $"    ↳ tool: {call.Name}"
                | _ -> ()
        }

        let! answer = run runConfig (Some taskSystemPrompt) task.Question

        return {
            TaskId   = task.Id
            Question = task.Question
            Answer   = answer
            Tokens   = tokensAccumulated
        }
    }

// ---------------------------------------------------------------------------
// Run all tasks — collects results, wraps individual failures as ExecutorErr
// ---------------------------------------------------------------------------

/// Execute all tasks in the plan sequentially.
/// A failing task surfaces as ExecutorErr but does not abort remaining tasks —
/// it is recorded with an empty answer so the Synthesizer can still proceed.
let runAll (config: ExecutorConfig) (plan: ResearchPlan) : AsyncResult<TaskResult list> =
    asyncResult {
        let! results =
            plan.Tasks
            |> AsyncResult.traverse (fun task ->
                async {
                    let! r = runTask config task
                    return
                        match r with
                        | Ok result -> Ok result
                        | Error e   ->
                            let msg = $"[{task.Id}] failed: {e}"
                            config.OnProgress $"  ⚠ {msg}"
                            Ok { TaskId   = task.Id
                                 Question = task.Question
                                 Answer   = $"(Task failed: {msg})"
                                 Tokens   = { InputTokens = 0; OutputTokens = 0 } }
                })
        return results
    }
