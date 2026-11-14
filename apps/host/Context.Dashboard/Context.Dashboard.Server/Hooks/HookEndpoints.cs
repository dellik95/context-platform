using System.Text.Json;
using Context.Application;
using Context.Application.Jobs;
using Context.Claude.Hooks;
using Context.Domain.Enums;
using Context.Dashboard.Server.Security;
using Context.Retrieval.Abstractions;

namespace Context.Dashboard.Server.Hooks;

/// <summary>
/// Local-only Claude Code lifecycle hook endpoints for the <c>Context.Claude.Hooks</c> DTOs. Every
/// route is guarded by <see cref="BearerTokenAuth.RequireBearerTokenFilter"/>, which resolves the
/// request's <c>Authorization: Bearer</c> header to whichever <see cref="Context.Domain.Entities.Project"/>
/// it belongs to and stashes it for <see cref="CurrentProjectAccessor"/>.
/// <para/>
/// A hook's only durable work on the request path is one <see cref="JobType.ProcessHook"/> insert so
/// the hook script's 2 s curl timeout is easy to meet; context-carrying hooks build read-only payloads
/// fail-open via <see cref="HookContextBuilder"/>.
/// </summary>
public static class HookEndpoints
{
    public static void MapHookEndpoints(this WebApplication app)
    {
        var hooks = app.MapGroup("/hooks").RequireBearerToken();

        hooks.MapPost("/session-start", SessionStartAsync);
        hooks.MapPost("/user-prompt-submit", UserPromptSubmitAsync);
        hooks.MapPost("/post-tool-use", PostToolUseAsync);
        hooks.MapPost("/session-end", SessionEndAsync);
        hooks.MapPost("/post-compact", PostCompactAsync);
        hooks.MapPost("/stop", StopAsync);
    }

    internal static async Task<IResult> SessionStartAsync(
        SessionStartInput input, IContextEngine engine, IJobQueue jobQueue, CurrentProjectAccessor project, CancellationToken ct)
    {
        await QueueHookAsync(jobQueue, project.ProjectId, HookType.SessionStart, input, ct).ConfigureAwait(false);

        var additionalContext = await HookContextBuilder.TryBuildSessionStartContextAsync(engine, project.ProjectId, ct).ConfigureAwait(false);
        return Results.Ok(new HookOutput(additionalContext));
    }

    internal static async Task<IResult> UserPromptSubmitAsync(
        UserPromptSubmitInput input, IContextEngine engine, IJobQueue jobQueue, CurrentProjectAccessor project, CancellationToken ct)
    {
        await QueueHookAsync(jobQueue, project.ProjectId, HookType.UserPromptSubmit, input, ct).ConfigureAwait(false);

        var additionalContext = await HookContextBuilder.TryBuildPromptContextAsync(
            engine, project.ProjectId, input.SessionId, input.Prompt, ct).ConfigureAwait(false);
        return Results.Ok(new HookOutput(additionalContext));
    }

    internal static async Task<IResult> PostToolUseAsync(
        PostToolUseInput input, IJobQueue jobQueue, CurrentProjectAccessor project, CancellationToken ct)
    {
        // File-level reindexing on edit is intentionally NOT triggered here: the
        // ProjectFileWatcherManager's per-project watcher already picks up any on-disk change.
        await QueueHookAsync(jobQueue, project.ProjectId, HookType.PostToolUse, input, ct).ConfigureAwait(false);
        return Results.Ok(new HookOutput(AdditionalContext: null));
    }

    internal static async Task<IResult> SessionEndAsync(
        SessionEndInput input, IJobQueue jobQueue, CurrentProjectAccessor project, CancellationToken ct)
    {
        await QueueHookAsync(jobQueue, project.ProjectId, HookType.SessionEnd, input, ct).ConfigureAwait(false);
        return Results.Ok(new HookOutput(AdditionalContext: null));
    }

    /// <summary>Records the assistant's reply for the turn. Must never return additionalContext on Stop.</summary>
    internal static async Task<IResult> StopAsync(
        StopInput input, IJobQueue jobQueue, CurrentProjectAccessor project, CancellationToken ct)
    {
        await QueueHookAsync(jobQueue, project.ProjectId, HookType.Stop, input, ct).ConfigureAwait(false);
        return Results.Ok(new HookOutput(AdditionalContext: null));
    }

    /// <summary>Captures a mid-session <c>/compact</c> summary checkpoint from stdin or the transcript tail.</summary>
    internal static async Task<IResult> PostCompactAsync(
        PostCompactInput input, IJobQueue jobQueue, CurrentProjectAccessor project, CancellationToken ct)
    {
        var compactSummary = string.IsNullOrWhiteSpace(input.CompactSummary)
            ? await HookContextBuilder.TryExtractCompactSummaryAsync(input.TranscriptPath, ct).ConfigureAwait(false)
            : input.CompactSummary;

        await QueueHookAsync(jobQueue, project.ProjectId, HookType.PostCompact, input with { CompactSummary = compactSummary }, ct).ConfigureAwait(false);
        return Results.Ok(new HookOutput(AdditionalContext: null));
    }

    internal static Task<string?> TryBuildSessionStartContextAsync(IContextEngine engine, Guid projectId, CancellationToken ct) =>
        HookContextBuilder.TryBuildSessionStartContextAsync(engine, projectId, ct);

    internal static string? FormatContextPack(ContextPack pack) =>
        HookContextBuilder.FormatContextPack(pack);

    /// <summary>Exposed as internal for Context.IntegrationTests verification of retrieval framing.</summary>
    internal static string WrapAsRetrievedData(string content) =>
        HookContextBuilder.WrapAsRetrievedData(content);

    private static Task QueueHookAsync(IJobQueue jobQueue, Guid projectId, HookType type, object input, CancellationToken ct) =>
        jobQueue.EnqueueAsync(
            JobType.ProcessHook,
            new ProcessHookPayload(type, JsonSerializer.Serialize(input, input.GetType()), DateTimeOffset.UtcNow),
            projectId,
            JobPriority.Hook,
            ct);
}
