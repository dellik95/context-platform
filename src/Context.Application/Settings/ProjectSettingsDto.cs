namespace Context.Application.Settings;

// Persisted verbatim (as JSON) into Project.ConfigJson - the whole field is this DTO's
// serialization, nothing else uses ConfigJson today. EmbeddingProvider/LlmProvider/Ollama*/Foundry* DO
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
    // Batch size for memory extraction: once this many SessionEvents are waiting, ContextEngine
    // claims them as one batch and enqueues a single ExtractMemory job (one LLM call over the raw
    // events). Session end flushes whatever is left, however few.
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
    int StaleSessionTimeoutMinutes = 240,
    // Once SummarizeSessionJobHandler folds a batch of SessionEvents into a SessionSummaryChunk,
    // those events are fully covered by the chunk and never read again by ExtractMemory - but the
    // dashboard's session detail view and the memory_recall MCP tool both still show raw events for
    // history. Defaults to off so a project keeps that raw view until it explicitly opts into
    // trading it away for a smaller SessionEvents table.
    bool DeleteSessionEventsAfterSummary = false,
    // HookInvocationRetentionScheduler deletes HookInvocation rows older than this many days for
    // the project. Unlike SessionEvents, hook invocations are never folded into anything else (no
    // summary/chunk covers them) - they exist purely for the dashboard's live activity feed and
    // "hook invocations" counter, so the table grows forever with no natural cleanup point. <= 0
    // disables cleanup (today's only behavior). Default 30d - long enough to cover the dashboard's
    // recent-activity views, short enough that the table doesn't grow unbounded.
    int HookInvocationRetentionDays = 30,
    // MemoryCompactionScheduler periodically clusters and merges near-duplicate memories via
    // JobType.CompactMemory. <= 0 disables periodic compaction (today's only behavior) - same
    // shape as SessionSummaryIntervalMinutes above, one on/off+period knob rather than a separate
    // enable flag.
    int MemoryCompactionIntervalMinutes = 0,
    DateTimeOffset? LastMemoryCompactionScheduledAt = null,
    // Cosine similarity (embedding of "{Title}\n{Body}") above which two memories are clustered
    // together for a single compaction merge.
    double MemoryCompactionSimilarityThreshold = 0.90,
    // The four MemoryConsolidator knobs below govern real-time dedup on extraction (every
    // memory_remember call and every ExtractMemory job), separate from the periodic
    // MemoryCompaction* pass above. Defaults match MemoryConsolidator's former hardcoded consts,
    // so an unconfigured project behaves exactly as before this became configurable.
    //
    // Best-match cosine similarity strictly above this reinforces the existing memory (or, if the
    // LLM judges it contradictory, supersedes it) instead of inserting a near-duplicate.
    double MemoryConsolidationSimilarityThreshold = 0.92,
    // Best-match similarity at or above this (but at/below MemoryConsolidationSimilarityThreshold)
    // triggers a restatement check against the nearest neighbors before inserting as new.
    double MemoryConsolidationLowSimilarityThreshold = 0.80,
    // Reinforcements a memory needs (via the paths above) before being promoted from Working to
    // LongTerm tier.
    int MemoryConsolidationPromotionReinforcementThreshold = 2,
    // How many nearest existing memories are considered as merge/restatement candidates for one
    // new candidate.
    int MemoryConsolidationTopKForDedup = 3,
    // Foundry Local endpoint/models, used when EmbeddingProvider/LlmProvider is "foundry". Null
    // falls back to CONTEXT_FOUNDRY_* / FoundryLocalOptions' defaults.
    string? FoundryBaseUrl = null,
    string? FoundryEmbeddingModel = null,
    string? FoundryChatModel = null);
