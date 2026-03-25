/// Provider abstraction — the single contract all LLM backends implement.
///
/// Why abstract at request/response level rather than HTTP level?
///   HTTP-level abstraction still leaks provider concerns: URL shape, auth header
///   names, body serialisation format.  Abstracting at InferenceRequest/Response
///   level means the agent core never sees HTTP.  It works with provider-neutral
///   types and can be unit-tested with a pure mock that returns canned responses
///   without any network.  Caching, retry, and rate-limiting can be layered as
///   decorators without touching provider code.
module AgentCore.Providers.IInferenceProvider

open AgentCore.Core.Error
open AgentCore.Types

// ---------------------------------------------------------------------------
// Provider interface
// ---------------------------------------------------------------------------

type IInferenceProvider =
    /// Human-readable name (matches providers.json "name" field).
    abstract member Name        : string
    /// Declares how this provider handles tool calling.
    abstract member ToolSupport : ToolSupport
    /// Send a request; returns Ok InferenceResponse or an Error on the rail.
    abstract member Complete    : InferenceRequest -> AsyncResult<InferenceResponse>
