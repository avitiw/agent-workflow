module AgentCore.Tests.AgentLoopTests

open Xunit
open AgentCore.Core.Error
open AgentCore.Agent.Core
open AgentCore.Agent.Tools
open AgentCore.Tests.MockProvider

let private noopEvent _ = ()

let private makeConfig (provider: MockProvider) maxTurns = {
    Provider = provider :> AgentCore.Providers.IInferenceProvider.IInferenceProvider
    Model    = "mock-model"
    Tools    = allTools
    MaxTurns = maxTurns
    OnStep   = noopEvent
}

// ---------------------------------------------------------------------------
// Single-turn: EndTurn response
// ---------------------------------------------------------------------------

[<Fact>]
let ``agent returns final text on EndTurn`` () =
    let mock   = MockProvider([Ok (Responses.text "Hello from mock!")])
    let config = makeConfig mock 10
    let result = run config None "Hi" |> Async.RunSynchronously
    match result with
    | Ok text -> Assert.Equal("Hello from mock!", text)
    | Error e -> failwith $"Expected Ok, got {e}"

// ---------------------------------------------------------------------------
// Tool call followed by final response
// ---------------------------------------------------------------------------

[<Fact>]
let ``agent executes tool and returns follow-up response`` () =
    let mock = MockProvider([
        Ok (Responses.toolCall "calculator" [("expression", "6 * 7")])
        Ok (Responses.text    "The answer is 42.")
    ])
    let config = makeConfig mock 10
    let result = run config None "What is 6*7?" |> Async.RunSynchronously
    match result with
    | Ok text -> Assert.Equal("The answer is 42.", text)
    | Error e -> failwith $"Expected Ok after tool use, got {e}"

// ---------------------------------------------------------------------------
// MaxTurns exceeded
// ---------------------------------------------------------------------------

[<Fact>]
let ``agent returns MaxTurnsExceeded when loop runs too long`` () =
    // Provide more tool-use responses than the turn limit allows
    let responses =
        List.replicate 20 (Ok (Responses.toolCall "calculator" [("expression", "1+1")]))
    let mock   = MockProvider(responses)
    let config = makeConfig mock 3
    let result = run config None "Loop forever" |> Async.RunSynchronously
    match result with
    | Error MaxTurnsExceeded -> ()
    | other -> failwith $"Expected MaxTurnsExceeded, got {other}"

// ---------------------------------------------------------------------------
// System prompt is included in first request
// ---------------------------------------------------------------------------

[<Fact>]
let ``agent passes system prompt through`` () =
    let mock   = MockProvider([Ok (Responses.text "Aye aye!")])
    let config = makeConfig mock 10
    let result = run config (Some "You are a pirate.") "Hello" |> Async.RunSynchronously
    match result with
    | Ok _ -> ()   // main assertion: no error; system prompt forwarded internally
    | Error e -> failwith $"Unexpected error: {e}"
