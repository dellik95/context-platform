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

    public bool TryGet(string claudeSessionId, Guid projectId, out Guid sessionId) =>
        _map.TryGetValue((claudeSessionId, projectId), out sessionId);

    public void Set(string claudeSessionId, Guid projectId, Guid sessionId) =>
        _map[(claudeSessionId, projectId)] = sessionId;

    public void Remove(string claudeSessionId, Guid projectId) =>
        _map.TryRemove((claudeSessionId, projectId), out _);
}
