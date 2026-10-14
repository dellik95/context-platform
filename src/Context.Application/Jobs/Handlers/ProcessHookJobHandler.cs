using System.Text.Json;
using Context.Application.Notifications;
using Context.Claude.Hooks;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// Applies one captured Claude Code hook call: resolves its session, records its event or ends the
/// session, and queues the follow-up extraction/summary jobs, all in one transaction. The hook's
/// <see cref="HookInvocation"/> row, stored under the job's id in that same transaction, is the
/// "already processed" marker, so a retry after a crash between commit and completion does nothing.
/// SQLite serializes the write transactions, which is what keeps two jobs for one Claude session from
/// both creating its Session row.
/// </summary>
public sealed class ProcessHookJobHandler(
    IContextEngine engine,
    IUnitOfWork unitOfWork,
    IJobRepository jobs,
    IHookInvocationRepository invocations,
    IProjectRepository projects,
    IHookInvocationNotifier? notifier = null,
    TimeProvider? timeProvider = null,
    ILogger<ProcessHookJobHandler>? logger = null) : IJobHandler
{
    /// <summary>How long a SessionEnd or PostCompact waits for earlier hooks of its project before it
    /// stops waiting: an earlier job stuck in retries must not hold the barrier up for good.</summary>
    public static readonly TimeSpan MaxBarrierWait = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan BarrierRecheckDelay = TimeSpan.FromSeconds(2);

    private readonly IHookInvocationNotifier _notifier = notifier ?? NullHookInvocationNotifier.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger<ProcessHookJobHandler> _logger = logger ?? NullLogger<ProcessHookJobHandler>.Instance;

    public JobType Type => JobType.ProcessHook;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ProcessHookPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(ProcessHookPayload)}.");
        var projectId = job.ProjectId
            ?? throw new InvalidOperationException($"Job {job.Id}: {nameof(JobType)}.{nameof(JobType.ProcessHook)} jobs must carry a ProjectId.");

        if (await invocations.ExistsAsync(job.Id, ct).ConfigureAwait(false))
        {
            return;
        }

        if (payload.Type is HookType.SessionEnd or HookType.PostCompact)
        {
            await WaitForEarlierHooksAsync(job, projectId, ct).ConfigureAwait(false);
        }

        var totalCount = await unitOfWork.InTransactionAsync(async () =>
        {
            if (await invocations.ExistsAsync(job.Id, ct).ConfigureAwait(false))
            {
                return -1;
            }

            await ApplyAsync(job.Id, projectId, payload, ct).ConfigureAwait(false);
            await invocations.RecordAsync(job.Id, projectId, payload.Type, payload.InputJson, payload.ReceivedAt, ct).ConfigureAwait(false);
            return await invocations.CountAsync(projectId, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (totalCount < 0)
        {
            return;
        }

        // No explicit wake: a lane re-kicks itself when this job completes, which is when the extraction or
        // summary jobs queued above first become claimable, and the poll tick covers other processes.
        await NotifyAsync(projectId, payload, totalCount, ct).ConfigureAwait(false);
    }

    /// <summary>Hooks of one project run in parallel lanes, so a session's last events can still be queued
    /// when its SessionEnd job starts. Ending first would flush a batch that is missing them.</summary>
    private async Task WaitForEarlierHooksAsync(Job job, Guid projectId, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        if (now - job.CreatedAt > MaxBarrierWait)
        {
            return;
        }

        var active = await jobs.ListActiveByTypeAsync(JobType.ProcessHook, projectId, ct).ConfigureAwait(false);
        if (active.Any(other => other.Id != job.Id && other.CreatedAt < job.CreatedAt))
        {
            throw new JobDeferredException(now + BarrierRecheckDelay, "waiting for earlier hooks of the project to finish");
        }
    }

    private async Task ApplyAsync(Guid jobId, Guid projectId, ProcessHookPayload payload, CancellationToken ct)
    {
        switch (payload.Type)
        {
            case HookType.SessionStart:
            {
                var input = Read<SessionStartInput>(payload);
                await engine.StartSessionAsync(projectId, input.SessionId, ct, input.Cwd).ConfigureAwait(false);
                break;
            }

            case HookType.UserPromptSubmit:
            {
                var input = Read<UserPromptSubmitInput>(payload);
                var sessionId = await engine.StartSessionAsync(projectId, input.SessionId, ct).ConfigureAwait(false);
                await engine.RecordEventAsync(
                    projectId, sessionId, SessionEventType.Prompt, new { prompt = input.Prompt }, ct, jobId, payload.ReceivedAt).ConfigureAwait(false);
                break;
            }

            case HookType.PostToolUse:
            {
                var input = Read<PostToolUseInput>(payload);
                var project = await projects.GetByIdAsync(projectId, ct).ConfigureAwait(false);
                var sessionId = await engine.StartSessionAsync(projectId, input.SessionId, ct).ConfigureAwait(false);

                // `path` is lifted out of the tool input here so the extractor downstream never needs to
                // know Claude Code's snake_case tool-input schema.
                await engine.RecordEventAsync(projectId, sessionId, ClassifyTool(input.ToolName), new
                {
                    toolName = input.ToolName,
                    path = HookToolPath.TryExtract(input.ToolInputJson, project?.RootPath),
                    toolInput = input.ToolInputJson,
                    toolResponse = input.ToolResponseJson
                }, ct, jobId, payload.ReceivedAt).ConfigureAwait(false);
                break;
            }

            case HookType.SessionEnd:
            {
                var input = Read<SessionEndInput>(payload);
                var sessionId = await engine.StartSessionAsync(projectId, input.SessionId, ct).ConfigureAwait(false);
                await engine.EndSessionAsync(projectId, sessionId, ct, input.Reason).ConfigureAwait(false);
                break;
            }

            case HookType.PostCompact:
            {
                var input = Read<PostCompactInput>(payload);
                var sessionId = await engine.StartSessionAsync(projectId, input.SessionId, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(input.CompactSummary))
                {
                    await engine.ExtractMemoryFromCompactSummaryAsync(projectId, sessionId, input.CompactSummary, ct).ConfigureAwait(false);
                }

                await engine.RequestSessionSummaryAsync(projectId, sessionId, ct).ConfigureAwait(false);
                break;
            }

            case HookType.Stop:
            {
                var input = Read<StopInput>(payload);
                if (!string.IsNullOrWhiteSpace(input.LastAssistantMessage))
                {
                    var sessionId = await engine.StartSessionAsync(projectId, input.SessionId, ct).ConfigureAwait(false);
                    await engine.RecordEventAsync(
                        projectId, sessionId, SessionEventType.AssistantMessage, new { message = input.LastAssistantMessage }, ct, jobId, payload.ReceivedAt)
                        .ConfigureAwait(false);
                }

                break;
            }

            default:
                throw new InvalidOperationException($"Unsupported hook type {payload.Type}.");
        }
    }

    private async Task NotifyAsync(Guid projectId, ProcessHookPayload payload, int totalCount, CancellationToken ct)
    {
        try
        {
            await _notifier.NotifyAsync(projectId, new HookInvocationEvent(payload.Type, payload.InputJson, payload.ReceivedAt, totalCount), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to push a hook invocation to the dashboard; the hook itself was processed.");
        }
    }

    private static T Read<T>(ProcessHookPayload payload) =>
        JsonSerializer.Deserialize<T>(payload.InputJson)
            ?? throw new InvalidOperationException($"Could not deserialize the {payload.Type} hook input.");

    private static SessionEventType ClassifyTool(string toolName) => toolName switch
    {
        "Read" or "NotebookRead" => SessionEventType.FileRead,
        "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => SessionEventType.FileEdit,
        "Bash" or "BashOutput" or "KillShell" => SessionEventType.Command,
        _ => SessionEventType.ToolCall
    };
}
