using System.Text.Json.Nodes;
using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Context.Shared;
using Serilog;

namespace Context.Dashboard.Server.Security;

/// <summary>
/// One-time startup reconciliation pass for the Phase 3 pivot to per-project bearer tokens
/// (<see cref="Project.BearerTokenHash"/>): a project installed before that pivot has a working
/// token sitting in its own <c>.claude/context-platform.local.json</c> (written by
/// <c>ClaudeIntegrationInstaller</c>), but its <see cref="Project"/> row was created with no
/// <see cref="Project.BearerTokenHash"/> at all — <see cref="BearerTokenAuth"/> would reject that
/// token forever, silently, the moment the shared-host auth model goes live, and the only visible
/// symptom would be every hook/MCP call from that project failing 401.
/// <para/>
/// Run once at startup (see <c>Program.cs</c>, right after the EF Core migration) rather than
/// lazily inside <see cref="BearerTokenAuth.TryResolveProjectAsync"/>: that lookup only ever has
/// the presented token's hash to go on, with no row to match it against yet (that's exactly the
/// problem being fixed) — the only way to discover which project a stray on-disk token belongs to
/// is to check every registered project's own <see cref="Project.RootPath"/>, which is naturally a
/// one-time sweep over <see cref="IProjectRepository.ListAsync"/> rather than extra work on every
/// unauthenticated request.
/// </summary>
public static class ProjectTokenBackfill
{
    private const string LocalConfigRelativePath = ".claude/context-platform.local.json";

    /// <summary>
    /// For every project with no <see cref="Project.BearerTokenHash"/> yet, looks for
    /// <c>{RootPath}/.claude/context-platform.local.json</c> and, if it carries a
    /// <c>bearerToken</c>, hashes it into that project's row. Best-effort: a project with no such
    /// file (never installed), or one whose file can't be read/parsed, is left alone — it will pick
    /// up a hash normally the next time <c>context claude install</c> / the Dashboard's install
    /// action runs.
    /// </summary>
    public static async Task ReconcileAsync(IProjectRepository projects, CancellationToken ct)
    {
        var all = await projects.ListAsync(ct).ConfigureAwait(false);
        foreach (var project in all)
        {
            if (!string.IsNullOrEmpty(project.BearerTokenHash))
            {
                continue;
            }

            var (token, issuedAt) = await TryReadLocalTokenAsync(project.RootPath, ct).ConfigureAwait(false);
            if (token is null)
            {
                continue;
            }

            project.BearerTokenHash = Hashing.HashToken(token);
            project.BearerTokenIssuedAt = issuedAt ?? DateTimeOffset.UtcNow;
            await projects.UpdateAsync(project, ct).ConfigureAwait(false);

            Log.Information(
                "Backfilled bearer token hash for project {ProjectId} ({ProjectName}) from its existing {LocalConfigPath}",
                project.Id, project.Name, LocalConfigRelativePath);
        }
    }

    private static async Task<(string? Token, DateTimeOffset? IssuedAt)> TryReadLocalTokenAsync(string rootPath, CancellationToken ct)
    {
        var path = Path.Combine(rootPath, ".claude", "context-platform.local.json");
        if (!System.IO.File.Exists(path))
        {
            return (null, null);
        }

        try
        {
            await using var stream = System.IO.File.OpenRead(path);
            var node = await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var obj = node as JsonObject;

            var token = obj?["bearerToken"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(token))
            {
                return (null, null);
            }

            var issuedAtText = obj?["generatedAt"]?.GetValue<string>();
            var issuedAt = DateTimeOffset.TryParse(issuedAtText, out var parsed) ? parsed : (DateTimeOffset?)null;

            return (token, issuedAt);
        }
        catch
        {
            // Best-effort reconciliation — a missing/unreadable/malformed local config just means
            // this project keeps whatever BearerTokenHash it already has (null), same as before
            // this pass ran; it is not this startup sweep's job to fail the whole host over it.
            return (null, null);
        }
    }
}
