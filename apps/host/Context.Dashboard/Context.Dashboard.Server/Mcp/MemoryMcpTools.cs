using System.ComponentModel;
using Context.Application;
using Context.Application.Settings;
using Context.Dashboard.Server.Security;
using Context.Domain.Enums;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Context.Dashboard.Server.Mcp;

/// <summary>
/// MCP tools for semantic memory search, lookup, and agent-initiated creation or deletion.
/// </summary>
[McpServerToolType]
public sealed class MemoryMcpTools(
    IContextEngine engine,
    CurrentProjectAccessor currentProject,
    IProjectSettingsService projectSettingsService,
    ILogger<MemoryMcpTools>? logger = null)
{
    private const int DefaultMemorySearchLimit = 20;
    private const int MaxMemorySearchLimit = 100;

    [McpServerTool(Name = "memory_search")]
    [Description("Semantic search over the project's knowledge base: durable memories of how the system works " +
        "(architecture, component behavior, decisions and their reasons, constraints, configuration, gotchas, procedures, " +
        "open issues), ranked by meaning rather than keywords. Ask in natural language, the way you would ask a colleague " +
        "(\"how are failed jobs retried?\"); a paraphrase finds a memory that shares none of its words, and exact names " +
        "of classes, files or tickets also match directly. Each memory carries a short reusable Topic tag (e.g. " +
        "\"Auth middleware\") naming the feature/subsystem it's about, so memories on the same subject from different " +
        "sessions can be found together via the topic filter.")]
    public async Task<IReadOnlyList<Context.Domain.Entities.Memory>> MemorySearchAsync(
        [Description("What you want to know, in natural language. Ranked by semantic similarity; files, symbols and tickets it names boost exact matches.")] string query,
        [Description("Include superseded and obsolete memories.")] bool includeSuperseded = false,
        [Description("Optional exact-match filter on the memory's Topic tag, e.g. \"Auth middleware\".")] string? topic = null,
        [Description("Maximum memories to return.")] int limit = DefaultMemorySearchLimit,
        CancellationToken ct = default)
    {
        var memories = await engine.SearchMemoryAsync(currentProject.ProjectId, query, includeSuperseded, ct, topic).ConfigureAwait(false);
        return [.. memories.Take(Math.Clamp(limit, 1, MaxMemorySearchLimit))];
    }

    [McpServerTool(Name = "memory_get")]
    [Description("Fetches one memory by id with its evidence (why it exists or changed) and the entities it mentions, or null if it doesn't exist.")]
    public async Task<MemoryDetail?> MemoryGetAsync(
        [Description("Memory id.")] Guid memoryId,
        CancellationToken ct = default)
        => await engine.GetMemoryAsync(currentProject.ProjectId, memoryId, ct).ConfigureAwait(false);

    [McpServerTool(Name = "memory_remember")]
    [Description("Stores durable project knowledge, whether the user asked for it explicitly or you found it yourself. " +
        "Call it the moment a user says 'remember that...', 'note for later...', or gives a standing instruction — don't " +
        "wait for enough events to accumulate. Just as important: call it proactively, without being asked, whenever you " +
        "surface something durable about THIS project that isn't already captured — a root cause found while " +
        "investigating a bug, an architectural fact learned from reading unfamiliar files, a subagent's final report " +
        "handing back a concrete finding (file:line pointers, a mechanism, a gotcha, a decision and its reasoning), or a " +
        "conclusion from cross-file analysis. A subagent's own report is not itself persisted anywhere — if it contains " +
        "something worth knowing next session, this is the only way it survives past this conversation. Don't call it " +
        "for things that are cheap to re-derive from the code (file locations, current contents) or that are purely " +
        "about this session (in-progress plans, what you intend to do next) — see memory_search's Topic guidance for " +
        "what makes a memory durable. You already have the full context, so fill in facts/topic/concepts/files yourself " +
        "rather than leaving this an unstructured narrative dump — the same structured shape the automatic " +
        "session-summary extraction produces, so this memory is just as useful when it resurfaces later. " +
        "Saving is asynchronous: the call returns status \"saving\" with a jobId as soon as the request is durably queued, and the " +
        "memory is stored (and retried if the embedding provider is down) in the background. No memory id is returned — the " +
        "jobId is not a memory id, so don't pass it to memory_get or memory_forget. The memory shows up in memory_search after " +
        "a short delay, longer while the provider is unavailable. Don't call it again just because it isn't searchable yet.")]
    public async Task<MemoryWriteAccepted> MemoryRememberAsync(
        [Description("The memory's main content: 2-5 self-contained sentences naming the SPECIFIC files (by path), " +
            "classes/functions/symbols (by name), and config keys actually involved — never a generic stand-in " +
            "('a service', 'some files'). State the actual mechanism/reasoning, not just that something happened.")] string content,
        [Description("One of Fact, Decision, Constraint, Architecture, Behavior, Configuration, Discovery, Procedure, Issue. Unknown or omitted means Fact.")] string? type = null,
        [Description("Short one-line title (under 100 chars) stating what is true or was learned, in the present tense — not what was changed or what you did to find out. " +
            "Omit to auto-derive one from the first sentence of content.")] string? title = null,
        [Description("Short 2-5 word phrase naming the feature/subsystem/component this memory is about (e.g. \"Auth middleware\", " +
            "\"Job dispatcher retry logic\") — reuse an existing name for that area rather than inventing a one-off phrase, so memories " +
            "about the same subject from different sessions share it and can be found together via memory_search's topic filter.")] string? topic = null,
        [Description("2-5 short, atomic, standalone facts pulled out of content — each must make sense read on its own with no " +
            "surrounding narrative (shown when a search result needs to say something useful in very few tokens). Reuse concrete " +
            "names from content (a file path, a symbol, a number, a config key) rather than a vaguer paraphrase.")] string[]? facts = null,
        [Description("1-3 tags naming what KIND of knowledge this is, chosen ONLY from this fixed list: how-it-works, why-it-exists, " +
            "problem-solution, gotcha, pattern, trade-off. Omit if none clearly apply.")] string[]? concepts = null,
        [Description("Specific files this memory is about that were read/examined but not changed. Omit if not applicable.")] string[]? filesRead = null,
        [Description("Specific files this memory is about that were actually edited because of it. Omit if not applicable.")] string[]? filesModified = null,
        CancellationToken ct = default)
    {
        await EnsureExplicitMemoryWritesEnabledAsync("memory_remember", ct).ConfigureAwait(false);

        var jobId = await engine.RequestRememberAsync(
            currentProject.ProjectId, content, ParseMemoryType(type), ct,
            title, topic, facts, concepts, filesRead, filesModified).ConfigureAwait(false);
        return new MemoryWriteAccepted(MemoryWriteStatus.Saving, jobId);
    }

    [McpServerTool(Name = "memory_forget")]
    [Description("Permanently deletes one memory by id (from memory_search/memory_get) together with its evidence, entities " +
        "and embedding — there is no undo. Use when the user " +
        "explicitly asks to forget/remove/delete something previously remembered, or when you find a memory that is now flatly " +
        "wrong or obsolete (e.g. it describes an approach that has since been replaced) rather than leaving stale/misleading " +
        "guidance for a future session. Prefer memory_remember for a memory that's merely outdated/incomplete — a fresh call " +
        "about the same topic is deduped/reinforced/supersedes the old one automatically, which preserves history; reach for " +
        "memory_forget only when the memory should not exist at all. The delete is asynchronous: status \"deleting\" with a jobId " +
        "means it was queued and will be applied (and retried) in the background; \"not_found\" means no such memory exists in this project.")]
    public async Task<MemoryWriteAccepted> MemoryForgetAsync(
        [Description("Id of the memory to delete.")] Guid memoryId,
        CancellationToken ct = default)
    {
        await EnsureExplicitMemoryWritesEnabledAsync("memory_forget", ct).ConfigureAwait(false);
        var jobId = await engine.RequestForgetAsync(currentProject.ProjectId, memoryId, ct).ConfigureAwait(false);
        return jobId is { } id
            ? new MemoryWriteAccepted(MemoryWriteStatus.Deleting, id)
            : new MemoryWriteAccepted(MemoryWriteStatus.NotFound, null);
    }

    private async Task EnsureExplicitMemoryWritesEnabledAsync(string toolName, CancellationToken ct)
    {
        var settings = await projectSettingsService.GetAsync(currentProject.ProjectId, ct).ConfigureAwait(false);
        if (!settings.ExplicitRememberEnabled)
        {
            logger?.LogInformation(
                "{ToolName} was called for project {ProjectId} but ExplicitRememberEnabled is false; rejecting.",
                toolName, currentProject.ProjectId);
            throw new InvalidOperationException(
                $"{toolName} is disabled for this project (ExplicitRememberEnabled is false in project settings).");
        }
    }

    private MemoryType ParseMemoryType(string? type)
    {
        if (!MemoryTypeExtensions.TryParseName(type, out var parsed) && !string.IsNullOrWhiteSpace(type))
        {
            logger?.LogInformation("memory_remember received an unrecognized MemoryType {Type}; defaulting to Fact.", type);
        }

        return parsed;
    }
}
