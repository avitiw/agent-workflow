/// Phase 3 — Synthesizer.
/// Takes all TaskResults and calls the LLM once more to produce a coherent
/// markdown research report.  The full question + each task answer are injected
/// into the user message so the model has complete context.
module AgentResearch.Synthesizer.Synthesizer

open System.IO
open AgentCore.Core.Error
open AgentCore.Types
open AgentCore.Providers.IInferenceProvider
open AgentResearch.Planner.PlanTypes

// ---------------------------------------------------------------------------
// System prompt
// ---------------------------------------------------------------------------

let private systemPrompt = """
You are a research synthesis assistant. You will be given a research question and
the results of several focused sub-investigations. Write a comprehensive, well-structured
markdown report that:
- Starts with an executive summary
- Covers each sub-question with its findings
- Ends with a conclusion and key takeaways
- Uses markdown headers, bullet points, and emphasis appropriately
Respond with ONLY the markdown content."""

// ---------------------------------------------------------------------------
// Prompt assembly
// ---------------------------------------------------------------------------

let private buildUserMessage (plan: ResearchPlan) (results: TaskResult list) : string =
    let sb = System.Text.StringBuilder()
    sb.AppendLine($"# Research Question\n{plan.OriginalQuestion}\n") |> ignore
    sb.AppendLine("# Sub-Question Results\n") |> ignore
    for r in results do
        sb.AppendLine($"## {r.Question}\n{r.Answer}\n") |> ignore
    sb.ToString()

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/// Synthesize all task results into a FinalReport on the Ok rail.
let run
    (provider  : IInferenceProvider)
    (model     : string)
    (plan      : ResearchPlan)
    (results   : TaskResult list)
    (totalTokens : TokenUsage)
    : AsyncResult<FinalReport> =

    asyncResult {
        let userMsg = buildUserMessage plan results

        let request = {
            Model     = model
            Messages  = [ SystemMessage systemPrompt; UserMessage userMsg ]
            Tools     = None
            MaxTokens = 4096
        }

        let! response = provider.Complete(request)

        let content =
            match response.Content with
            | TextContent t | Mixed (t, _) -> t
            | ToolCallContent _            -> ""

        if System.String.IsNullOrWhiteSpace(content) then
            return! AsyncResult.err (SynthesizerErr "Synthesizer returned empty content")

        // Write report to output/
        let filename  = "report.md"
        let outputDir = "output"
        Directory.CreateDirectory(outputDir) |> ignore
        let outputPath = Path.Combine(outputDir, filename)
        File.WriteAllText(outputPath, content)

        let finalTokens = {
            InputTokens  = totalTokens.InputTokens  + response.TokensUsed.InputTokens
            OutputTokens = totalTokens.OutputTokens + response.TokensUsed.OutputTokens
        }

        return {
            Question    = plan.OriginalQuestion
            Content     = content
            OutputPath  = outputPath
            TotalTokens = finalTokens
        }
    }
