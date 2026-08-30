namespace Context.Application.Settings;

// Persisted verbatim (as JSON) into Project.ConfigJson - the whole field is this DTO's
// serialization, nothing else uses ConfigJson today. Provider fields are informational/
// override-on-next-restart only: Context.Providers wires concrete IEmbeddingProvider/
// ILlmProvider instances once at Context.Host startup via DI (see CONTRACTS.md SS5), there is
// no per-request/per-project provider resolution, so changing these fields does NOT hot-swap
// the running provider - the Settings UI must say so next to the provider fields.
// ReindexEnabled/ReindexIntervalMinutes DO take effect live (ReindexSchedulerService polls
// this DTO), no restart needed.
public sealed record ProjectSettingsDto(
    string EmbeddingProvider,
    string LlmProvider,
    string? OllamaBaseUrl,
    string? OllamaEmbeddingModel,
    string? OllamaChatModel,
    bool ReindexEnabled,
    int ReindexIntervalMinutes,
    DateTimeOffset? LastReindexScheduledAt);
