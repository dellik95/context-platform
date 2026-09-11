using System.Collections.Concurrent;

namespace Context.Dashboard.Server.Hooks;

/// <summary>
/// In-process map from Claude Code's own session id (a string, stable for the lifetime of one
/// Claude Code session) to the internal <see cref="Context.Domain.Entities.Session"/> id Context
/// Platform created for it.
/// </summary>
/// <remarks>
/// Deliberately in-memory only — purely a fast-path cache to avoid a DB round-trip on every hook
/// call. Being in-memory, a Context.Dashboard.Server restart mid-Claude-Code-session loses the mapping, but that
/// no longer risks a duplicate <c>Session</c> row: <see cref="Context.Application.ContextEngine.StartSessionAsync"/>
/// falls back to <c>ISessionRepository.GetByClaudeSessionIdAsync</c> and reuses the existing row for
/// that Claude session id (see <c>Hooks.HookEndpoints.GetOrStartSessionAsync</c>) before ever
/// creating a new one.
/// </remarks>
/// <remarks>
/// Keyed by (claudeSessionId, projectId) rather than claudeSessionId alone: Claude Code's session
/// id is only unique within one Claude Code installation, not globally, so two different
/// Context.Dashboard.Server processes (different projects) could in principle observe the same claudeSessionId.
/// Scoping the key by projectId too keeps one project's entry from ever being read or clobbered by
/// another's.
/// </remarks>
public sealed class ClaudeSessionMap
{
    private readonly ConcurrentDictionary<(string ClaudeSessionId, Guid ProjectId), Guid> _map = new();
    private readonly ConcurrentDictionary<(string ClaudeSessionId, Guid ProjectId), SemaphoreSlim> _locks = new();

    public bool TryGet(string claudeSessionId, Guid projectId, out Guid sessionId) =>
        _map.TryGetValue((claudeSessionId, projectId), out sessionId);

    public void Set(string claudeSessionId, Guid projectId, Guid sessionId) =>
        _map[(claudeSessionId, projectId)] = sessionId;

    public void Remove(string claudeSessionId, Guid projectId) =>
        _map.TryRemove((claudeSessionId, projectId), out _);

    /// <summary>
    /// Race-safe "look up, or start and cache" - closes a check-then-act gap where two concurrent
    /// hook calls for the same (claudeSessionId, projectId) could both miss <see cref="TryGet"/>
    /// and each call <paramref name="startAsync"/>, forking one logical Claude Code session across
    /// two internal Session rows. Locking is keyed per (claudeSessionId, projectId) only, so
    /// unrelated sessions/projects never contend.
    /// </summary>
    public Task<Guid> GetOrStartAsync(
        string claudeSessionId, Guid projectId, Func<CancellationToken, Task<Guid>> startAsync, CancellationToken ct)
    {
        return TryGet(claudeSessionId, projectId, out var existing)
            ? Task.FromResult(existing)
            : StartUnderLockAsync(claudeSessionId, projectId, startAsync, recheckCache: true, ct);
    }

    /// <summary>
    /// Unlike <see cref="GetOrStartAsync"/>, always invokes <paramref name="startAsync"/> even on a
    /// cache hit - required for the SessionStart hook, whose <c>ContextEngine.StartSessionAsync</c>
    /// carries un-end-a-stale-timed-out-session semantics that a cached fast-path would silently
    /// skip. Still takes the same keyed lock as <see cref="GetOrStartAsync"/> so the two can't race
    /// each other into creating duplicate rows for the same id.
    /// </summary>
    public Task<Guid> StartAsync(
        string claudeSessionId, Guid projectId, Func<CancellationToken, Task<Guid>> startAsync, CancellationToken ct) =>
        StartUnderLockAsync(claudeSessionId, projectId, startAsync, recheckCache: false, ct);

    private async Task<Guid> StartUnderLockAsync(
        string claudeSessionId, Guid projectId, Func<CancellationToken, Task<Guid>> startAsync, bool recheckCache, CancellationToken ct)
    {
        var key = (claudeSessionId, projectId);
        var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (recheckCache && TryGet(claudeSessionId, projectId, out var existing))
            {
                return existing;
            }

            var sessionId = await startAsync(ct).ConfigureAwait(false);
            Set(claudeSessionId, projectId, sessionId);
            return sessionId;
        }
        finally
        {
            gate.Release();
            _locks.TryRemove(key, out _);
        }
    }
}
