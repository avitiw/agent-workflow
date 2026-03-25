/// Phase 1 — Planner.
/// Calls the LLM once to break the user's question into a structured list of
/// sub-questions (a ResearchPlan).  The response must be a JSON array; any
/// parse failure surfaces as PlannerErr on the error rail.
module AgentResearch.Planner.Planner

open System.Text.Json
open AgentCore.Core.Error
open AgentCore.Types
open AgentCore.Providers.IInferenceProvider
open AgentResearch.Planner.PlanTypes

// ---------------------------------------------------------------------------
// System prompt injected into the planner call
// ---------------------------------------------------------------------------

let private systemPrompt = """
You are a research planning assistant. Given a complex question, break it down
into 3-5 focused sub-questions that together will answer the original question.

Respond with ONLY a valid JSON array — no markdown fences, no explanation:
[
  {"question": "..."},
  {"question": "..."}
]"""

// ---------------------------------------------------------------------------
// JSON parsing — extract plan tasks from LLM response text
// ---------------------------------------------------------------------------

let private parsePlan (question: string) (text: string) : Result<ResearchPlan, AppError> =
    try
        let startIdx = text.IndexOf('[')
        let endIdx   = text.LastIndexOf(']')
        if startIdx = -1 || endIdx = -1 then
            Error (PlannerErr $"No JSON array found in planner response:\n{text}")
        else
            let json = text.[startIdx..endIdx]
            use doc  = JsonDocument.Parse(json)
            let tasks =
                doc.RootElement.EnumerateArray()
                |> Seq.mapi (fun i el ->
                    { Id       = $"task-{i + 1}"
                      Question = el.GetProperty("question").GetString()
                      Status   = Pending })
                |> Seq.toList
            if tasks.IsEmpty then
                Error (PlannerErr "Planner returned an empty task list")
            else
                Ok { OriginalQuestion = question; Tasks = tasks }
    with ex ->
        Error (PlannerErr $"Failed to parse research plan JSON: {ex.Message}")

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/// Call the provider once and return a ResearchPlan on the Ok rail.
let run (provider: IInferenceProvider) (model: string) (question: string) : AsyncResult<ResearchPlan> =
    asyncResult {
        let request = {
            Model     = model
            Messages  = [ SystemMessage systemPrompt; UserMessage $"Question: {question}" ]
            Tools     = None
            MaxTokens = 1024
        }

        let! response = provider.Complete(request)

        let text =
            match response.Content with
            | TextContent t | Mixed (t, _) -> t
            | ToolCallContent _            -> ""

        return! parsePlan question text |> AsyncResult.ofResult
    }
