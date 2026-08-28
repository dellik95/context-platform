using System.Collections.Concurrent;

namespace Context.Host.Hooks;

/// <summary>
/// In-process map from Claude Code's own session id (a string, stable for the lifetime of one
/// Claude Code session) to the internal <see cref="Context.Domain.Entities.Session"/> id Context
/// Platform created for it.
/// </summary>
/// <remarks>
/// This is a deliberate, documented simplification: CONTRACTS.md's
/// <c>Context.Infrastructure.Abstractions.ISessionRepository</c> exposes no lookup by
/// <c>ClaudeSessionId</c> (only by our own internal <see cref="Guid"/>), so there is nowhere durable
/// to keep this mapping without adding a new repository method — out of scope for Context.Host.
/// Being in-memory only, a Context.Host restart mid-Claude-Code-session loses the mapping; the next
/// hook call for that Claude session id transparently starts a *new* internal session instead of
/// failing (see <c>Hooks.HookEndpoints.GetOrStartSessionAsync</c>). Tracked as follow-up: add
/// <c>ISessionRepository.GetByClaudeSessionIdAsync</c> in Context.Infrastructure.
/// </remarks>
public sealed class ClaudeSessionMap
{
    private readonly ConcurrentDictionary<string, Guid> _map = new(StringComparer.Ordinal);

    public bool TryGet(string claudeSessionId, out Guid sessionId) => _map.TryGetValue(claudeSessionId, out sessionId);

    public void Set(string claudeSessionId, Guid sessionId) => _map[claudeSessionId] = sessionId;

    public void Remove(string claudeSessionId) => _map.TryRemove(claudeSessionId, out _);
}
