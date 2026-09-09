namespace Context.Application.Settings;

// Persisted verbatim (as JSON) into Project.ConfigJson - the whole field is this DTO's
// serialization, nothing else uses ConfigJson today. EmbeddingProvider/LlmProvider/Ollama* DO
// take effect live, no restart needed: Context.Application.Providers.ProjectProviderResolver
// (IProviderResolver) re-reads this DTO fresh on every embedding/chat call, resolving each
// project's own provider independently -- OpenAI/Anthropic's API key/model/base-URL stay
// process-wide (this DTO carries none of those), only the provider *choice* and Ollama's own
// endpoint/models are genuinely per-project. ReindexEnabled/ReindexIntervalMinutes and
// SessionSummaryIntervalMinutes also take effect live (ReindexSchedulerService/
// SessionSummaryScheduler poll this DTO).
public sealed record ProjectSettingsDto(
    string EmbeddingProvider,
    string LlmProvider,
    string? OllamaBaseUrl,
    string? OllamaEmbeddingModel,
    string? OllamaChatModel,
    bool ReindexEnabled,
    int ReindexIntervalMinutes,
    DateTimeOffset? LastReindexScheduledAt,
    // <= 0 means "only summarize at session end" (today's only behavior) - a periodic
    // mid-session summary is opt-in per project, same shape as the reindex schedule above.
    int SessionSummaryIntervalMinutes = 0,
    // The interval is a MAXIMUM wait, not a guarantee: SessionSummaryScheduler also requires at
    // least this many new SessionEvents since the last summary before firing one, so an idle or
    // barely-active session doesn't burn an LLM call (and produce a low-value memory) just because
    // the clock ran out. Default chosen to represent a real batch of activity, not a single action.
    int MinEventsForSessionSummary = 15,
    // Maximum number of ranked memories included in each context section (historical notes/open
    // issues). Runtime-tunable so projects can trade retrieval breadth vs prompt size.
    int MaxMemoryNotesPerSection = 20,
    // Gates SummarizeSessionJobHandler's chain into ExtractMemory: a periodic mid-session chunk
    // only triggers memory extraction (LLM candidate generation + embedding + dedup - real work,
    // unlike a summary pass) once it covers at least this many events. Bypassed entirely when the
    // session is actually ending, since every chunk must be processed then regardless of size.
    int MinEventsForMemoryExtraction = 20,
    // Lets a project opt out of the memory_remember/memory_forget MCP tools entirely if its owner
    // doesn't want agent-initiated memory writes (creation or deletion) outside the normal
    // extraction pipeline. Defaults to enabled since both tools only ever fire on an explicit agent
    // tool call, never automatically.
    bool ExplicitRememberEnabled = true,
    // StaleSessionScheduler ends an Active session (same path as a real SessionEnd hook, reason
    // "stale-timeout") once this many minutes have passed with no new SessionEvent. <= 0 disables
    // cleanup for the project (today's only behavior: a killed CLI process leaves its Session row
    // Active forever). Default 4h - long enough that a normal thinking pause never trips it.
    int StaleSessionTimeoutMinutes = 240);
