using System.Security.Cryptography;
using System.Text;
using Context.Application;
using Context.Claude.Hooks;
using Context.Domain.Enums;
using Context.Retrieval.Abstractions;

namespace Context.Host.Hooks;

/// <summary>
/// Local-only Claude Code lifecycle hook endpoints (CONTRACTS.md §8's <c>Context.Claude.Hooks</c>
/// DTOs, wired here per the Context.Host task brief §5). Every route is guarded by
/// <see cref="RequireBearerToken"/>, comparing the request's <c>Authorization: Bearer</c> header
/// against <see cref="HostProjectContext.BearerToken"/> (loaded once at startup from
/// <c>.claude/context-platform.local.json</c> — see <c>Program.cs</c>) with a fixed-time comparison.
/// Every handler fails open on its own internal errors where the generated hook scripts expect it
/// (additional-context building never lets an exception escape) — but never on the auth check
/// itself, which must reject unambiguously.
/// </summary>
public static class HookEndpoints
{
    private const int MaxRecentMemoriesForSessionStart = 5;
    private const int MaxContextPackHitsForPrompt = 5;
    private const int MaxContextPackNotesForPrompt = 3;
    private const int PromptContextTokenBudget = 1200;
    private const int SnippetPreviewChars = 200;

    public static void MapHookEndpoints(this WebApplication app)
    {
        var hooks = app.MapGroup("/hooks").AddEndpointFilter(RequireBearerToken);

        hooks.MapPost("/session-start", SessionStartAsync);
        hooks.MapPost("/user-prompt-submit", UserPromptSubmitAsync);
        hooks.MapPost("/post-tool-use", PostToolUseAsync);
        hooks.MapPost("/session-end", SessionEndAsync);
    }

    private static async ValueTask<object?> RequireBearerToken(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var project = ctx.HttpContext.RequestServices.GetRequiredService<HostProjectContext>();
        if (!IsAuthorized(ctx.HttpContext, project))
        {
            return Results.Unauthorized();
        }

        return await next(ctx).ConfigureAwait(false);
    }

    private static bool IsAuthorized(HttpContext http, HostProjectContext project)
    {
        // No local config was ever installed (or it couldn't be parsed) for this project - no
        // token can ever legitimately match, so every hook call is rejected.
        if (string.IsNullOrEmpty(project.BearerToken))
        {
            return false;
        }

        const string prefix = "Bearer ";
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(project.BearerToken);
        var presented = Encoding.UTF8.GetBytes(header[prefix.Length..]);

        // CryptographicOperations.FixedTimeEquals requires equal-length spans; a length mismatch
        // is itself decided in the same fixed time as any other same-length comparison would take
        // (this is the standard shape for a fixed-time bearer-token check with a variable-length
        // secret - see e.g. ASP.NET Core's own API key auth samples).
        return expected.Length == presented.Length && CryptographicOperations.FixedTimeEquals(expected, presented);
    }

    private static async Task<IResult> SessionStartAsync(
        SessionStartInput input,
        IContextEngine engine,
        HostProjectContext project,
        ClaudeSessionMap sessionMap,
        CancellationToken ct)
    {
        var sessionId = await engine.StartSessionAsync(project.ProjectId, input.SessionId, ct).ConfigureAwait(false);
        sessionMap.Set(input.SessionId, sessionId);

        var additionalContext = await TryBuildSessionStartContextAsync(engine, project.ProjectId, ct).ConfigureAwait(false);
        return Results.Ok(new HookOutput(additionalContext));
    }

    private static async Task<IResult> UserPromptSubmitAsync(
        UserPromptSubmitInput input,
        IContextEngine engine,
        HostProjectContext project,
        ClaudeSessionMap sessionMap,
        CancellationToken ct)
    {
        var sessionId = await GetOrStartSessionAsync(engine, project, sessionMap, input.SessionId, ct).ConfigureAwait(false);
        await engine.RecordEventAsync(sessionId, SessionEventType.Prompt, new { prompt = input.Prompt }, ct).ConfigureAwait(false);

        var additionalContext = await TryBuildPromptContextAsync(engine, project.ProjectId, sessionId, input.Prompt, ct).ConfigureAwait(false);
        return Results.Ok(new HookOutput(additionalContext));
    }

    private static async Task<IResult> PostToolUseAsync(
        PostToolUseInput input,
        IContextEngine engine,
        HostProjectContext project,
        ClaudeSessionMap sessionMap,
        CancellationToken ct)
    {
        var sessionId = await GetOrStartSessionAsync(engine, project, sessionMap, input.SessionId, ct).ConfigureAwait(false);
        var type = ClassifyTool(input.ToolName);

        await engine.RecordEventAsync(sessionId, type, new
        {
            toolName = input.ToolName,
            toolInput = input.ToolInputJson,
            toolResponse = input.ToolResponseJson
        }, ct).ConfigureAwait(false);

        // File-level reindexing on edit is intentionally NOT triggered here: the
        // FileWatcherHostedService already picks up any on-disk change a tool makes (Edit/Write/
        // MultiEdit and friends all write through the real filesystem), so doing it again here
        // would just be a redundant, harder-to-debounce second trigger.
        return Results.Ok(new HookOutput(AdditionalContext: null));
    }

    private static async Task<IResult> SessionEndAsync(
        SessionEndInput input,
        IContextEngine engine,
        HostProjectContext project,
        ClaudeSessionMap sessionMap,
        CancellationToken ct)
    {
        var sessionId = await GetOrStartSessionAsync(engine, project, sessionMap, input.SessionId, ct).ConfigureAwait(false);
        await engine.EndSessionAsync(sessionId, ct).ConfigureAwait(false);
        sessionMap.Remove(input.SessionId);
        return Results.Ok(new HookOutput(AdditionalContext: null));
    }

    /// <summary>See <see cref="ClaudeSessionMap"/>'s remarks for why a miss here starts a new session.</summary>
    private static async Task<Guid> GetOrStartSessionAsync(
        IContextEngine engine, HostProjectContext project, ClaudeSessionMap sessionMap, string claudeSessionId, CancellationToken ct)
    {
        if (sessionMap.TryGet(claudeSessionId, out var existing))
        {
            return existing;
        }

        var sessionId = await engine.StartSessionAsync(project.ProjectId, claudeSessionId, ct).ConfigureAwait(false);
        sessionMap.Set(claudeSessionId, sessionId);
        return sessionId;
    }

    private static SessionEventType ClassifyTool(string toolName) => toolName switch
    {
        "Read" or "NotebookRead" => SessionEventType.FileRead,
        "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => SessionEventType.FileEdit,
        "Bash" or "BashOutput" or "KillShell" => SessionEventType.Command,
        _ => SessionEventType.ToolCall
    };

    /// <summary>
    /// SessionStart's "retrieves relevant previous context" (spec §9.13): a short project snapshot
    /// plus the project's most relevant memories. Any failure here is swallowed - Context Platform
    /// being briefly unhealthy must never block Claude Code's own session start.
    /// </summary>
    private static async Task<string?> TryBuildSessionStartContextAsync(IContextEngine engine, Guid projectId, CancellationToken ct)
    {
        try
        {
            var memories = await engine.SearchMemoryAsync(projectId, string.Empty, includeSuperseded: false, ct).ConfigureAwait(false);
            if (memories.Count == 0)
            {
                return null;
            }

            var infoObj = await engine.ProjectInfoAsync(projectId, ct).ConfigureAwait(false);
            var sb = new StringBuilder("Context Platform - project memory:\n");
            if (infoObj is ProjectInfo info)
            {
                sb.Append($"{info.FilesIndexed} files / {info.SymbolsIndexed} symbols indexed, {info.MemoriesCount} memories on record.\n");
            }

            foreach (var memory in memories.Take(MaxRecentMemoriesForSessionStart))
            {
                sb.Append($"- [{memory.Type}] {memory.Title}\n");
            }

            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// UserPromptSubmit's "may perform contextual retrieval" (spec §9.13): a budgeted
    /// <see cref="ContextPack"/> for the submitted prompt, formatted as plain text. Swallows every
    /// failure for the same fail-open reason as <see cref="TryBuildSessionStartContextAsync"/>.
    /// </summary>
    private static async Task<string?> TryBuildPromptContextAsync(
        IContextEngine engine, Guid projectId, Guid sessionId, string prompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        try
        {
            var query = new SearchQuery(projectId, prompt, Scope: null, TokenBudget: PromptContextTokenBudget, SessionId: sessionId);
            var pack = await engine.SearchContextAsync(query, ct).ConfigureAwait(false);
            return FormatContextPack(pack);
        }
        catch
        {
            return null;
        }
    }

    private static string? FormatContextPack(ContextPack pack)
    {
        if (pack.PrimaryHits.Count == 0 && pack.HistoricalNotes.Count == 0 && pack.OpenIssues.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(pack.ArchitectureSummary))
        {
            sb.Append(pack.ArchitectureSummary).Append('\n');
        }

        foreach (var hit in pack.PrimaryHits.Take(MaxContextPackHitsForPrompt))
        {
            sb.Append($"- [{hit.Kind}] {hit.Label}");
            if (!string.IsNullOrWhiteSpace(hit.Snippet))
            {
                sb.Append(": ").Append(Truncate(hit.Snippet, SnippetPreviewChars));
            }

            sb.Append('\n');
        }

        foreach (var note in pack.HistoricalNotes.Take(MaxContextPackNotesForPrompt))
        {
            sb.Append("- ").Append(note).Append('\n');
        }

        foreach (var issue in pack.OpenIssues.Take(MaxContextPackNotesForPrompt))
        {
            sb.Append("! ").Append(issue).Append('\n');
        }

        return sb.ToString();
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : string.Concat(text.AsSpan(0, maxChars), "…");
}
