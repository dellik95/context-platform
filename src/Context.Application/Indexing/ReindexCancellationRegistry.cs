using System.Collections.Concurrent;

namespace Context.Application.Indexing;

/// <summary>
/// Tracks one <see cref="CancellationTokenSource"/> per project with a reindex currently running,
/// so a client can ask to stop a specific project's in-progress <c>IIndexingPipeline.ReindexProjectAsync</c>
/// scan (see <c>Context.Dashboard.Server.Api.ProjectEndpoints</c>'s <c>POST /api/projects/{id}/index/cancel</c>)
/// without <c>CancellationToken.None</c> making that impossible, as it did before this type existed.
/// <para/>
/// Deliberately scoped to the foreground scan only: a full reindex also enqueues background jobs
/// (<c>EmbedChunk</c>, <c>UpdateGraph</c>, ...) that run independently through <c>JobDispatcher</c>'s
/// own worker loop. Cancelling here stops the scan from walking more files / enqueuing more work,
/// but does not retroactively cancel jobs already queued — draining/cancelling those is a materially
/// bigger feature this registry doesn't attempt.
/// </summary>
/// <remarks>
/// Registered as a singleton (<c>Context.Application.ServiceCollectionExtensions.AddContextApplication</c>)
/// so the same instance is shared between the request that starts a reindex and the request that
/// later cancels it.
/// </remarks>
public sealed class ReindexCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    /// <summary>
    /// Starts tracking a new reindex for <paramref name="projectId"/> and returns the token it
    /// should run with. A project can only ever have one reindex in flight at a time: if an entry
    /// is already tracked (a previous pass that never called <see cref="End"/> — e.g. a leaked
    /// entry from a process that never observed cancellation, or a second trigger while one is
    /// genuinely still running), that entry is cancelled and replaced rather than left to leak.
    /// </summary>
    public CancellationToken Begin(Guid projectId)
    {
        var cts = new CancellationTokenSource();
        _running.AddOrUpdate(projectId, cts, (_, existing) =>
        {
            existing.Cancel();
            existing.Dispose();
            return cts;
        });

        return cts.Token;
    }

    /// <summary>
    /// <see cref="Begin"/> for callers that must not interrupt a run already in flight (the
    /// scheduler): returns false and tracks nothing if <paramref name="projectId"/> is running.
    /// </summary>
    public bool TryBegin(Guid projectId, out CancellationToken token)
    {
        var cts = new CancellationTokenSource();
        if (!_running.TryAdd(projectId, cts))
        {
            cts.Dispose();
            token = default;
            return false;
        }

        token = cts.Token;
        return true;
    }

    /// <summary>Requests cancellation of <paramref name="projectId"/>'s running reindex, if any.</summary>
    /// <returns>False if no reindex is currently tracked for this project.</returns>
    public bool Cancel(Guid projectId)
    {
        if (!_running.TryGetValue(projectId, out var cts))
        {
            return false;
        }

        cts.Cancel();
        return true;
    }

    /// <summary>
    /// Stops tracking <paramref name="projectId"/>'s reindex once it finishes (success, failure, or
    /// cancellation) — must be called from a <c>finally</c> block around every
    /// <c>ReindexProjectAsync</c> call started via <see cref="Begin"/>, or this dictionary leaks one
    /// entry per reindex forever.
    /// </summary>
    /// <param name="token">
    /// The exact token <see cref="Begin"/> returned for this pass. Only removes the tracked entry
    /// if it still matches — a newer <see cref="Begin"/> call may have already replaced it (see that
    /// method's remarks), and blindly removing whatever is currently there could drop a different,
    /// still-running pass's entry out from under it.
    /// </param>
    public void End(Guid projectId, CancellationToken token)
    {
        if (!_running.TryGetValue(projectId, out var cts) || cts.Token != token)
        {
            return;
        }

        _running.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(projectId, cts));
        cts.Dispose();
    }

    /// <summary>True while a reindex is tracked as running for <paramref name="projectId"/>.</summary>
    public bool IsRunning(Guid projectId) => _running.ContainsKey(projectId);
}
