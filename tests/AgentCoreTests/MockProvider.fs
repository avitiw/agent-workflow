module AgentCore.Tests.MockProvider

open System.Collections.Generic
open AgentCore.Core.Error
open AgentCore.Types
open AgentCore.Providers.IInferenceProvider

/// A pure mock provider that returns pre-canned responses in sequence.
/// Used to test the agentic loop without any network calls.
type MockProvider(responses: Result<InferenceResponse, AppError> list) =
    let mutable remaining = responses

    interface IInferenceProvider with
        member _.Name        = "Mock"
        member _.ToolSupport = Native

        member _.Complete(_req) =
            async {
                match remaining with
                | []     -> return Error (Unexpected "MockProvider exhausted — no more responses")
                | r :: rest ->
                    remaining <- rest
                    return r
            }

module Responses =
    let text (content: string) : InferenceResponse = {
        Content    = TextContent content
        TokensUsed = { InputTokens = 10; OutputTokens = 20 }
        StopReason = EndTurn
    }

    let toolCall (name: string) (args: (string * string) list) : InferenceResponse =
        let input = Dictionary<string, obj>()
        args |> List.iter (fun (k, v) -> input[k] <- v :> obj)
        let call = { Id = "mock-call-id"; Name = name; Input = input }
        {
            Content    = ToolCallContent [call]
            TokensUsed = { InputTokens = 15; OutputTokens = 30 }
            StopReason = ToolUse
        }
