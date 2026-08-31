namespace Context.Application.Settings;

// Persisted verbatim (as JSON) into Project.ConfigJson - the whole field is this DTO's
// serialization, nothing else uses ConfigJson today. Provider fields are informational/
// override-on-next-restart only: Context.Providers wires concrete IEmbeddingProvider/
// ILlmProvider instances once at Context.Host startup via DI (see CONTRACTS.md SS5), there is
// no per-request/per-project provider resolution, so changing these fields does NOT hot-swap
// the running provider - the Settings UI must say so next to the provider fields.
// ReindexEnabled/ReindexIntervalMinutes and SessionSummaryIntervalMinutes DO take effect live
// (ReindexSchedulerService/SessionSummaryScheduler poll this DTO), no restart needed.
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
    int MaxMemoryNotesPerSection = 20);
