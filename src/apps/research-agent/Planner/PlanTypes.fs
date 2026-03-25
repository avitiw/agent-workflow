/// Domain types for the three-phase research pipeline.
/// Immutable records flow from Planner → Executor → Synthesizer — no shared mutable state.
module AgentResearch.Planner.PlanTypes

open AgentCore.Types

// ---------------------------------------------------------------------------
// Phase 1 output — Research Plan
// ---------------------------------------------------------------------------

type TaskStatus =
    | Pending
    | InProgress
    | Completed
    | Failed of reason: string

type PlanTask = {
    Id       : string
    Question : string
    Status   : TaskStatus
}

type ResearchPlan = {
    OriginalQuestion : string
    Tasks            : PlanTask list
}

// ---------------------------------------------------------------------------
// Phase 2 output — Task Results
// ---------------------------------------------------------------------------

type TaskResult = {
    TaskId   : string
    Question : string
    Answer   : string
    Tokens   : TokenUsage
}

// ---------------------------------------------------------------------------
// Phase 3 output — Final Report
// ---------------------------------------------------------------------------

type FinalReport = {
    Question    : string
    Content     : string       // full markdown
    OutputPath  : string       // path where file was written
    TotalTokens : TokenUsage   // sum across all phases
}
