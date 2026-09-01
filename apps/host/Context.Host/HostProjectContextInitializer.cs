using System.Text.Json;
using Context.Domain.Entities;
using Context.Host.Hooks;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Context.Host;

/// <summary>
/// Resolves (creating if necessary) the single <see cref="Project"/> row this Context.Host process
/// serves, and populates <see cref="HostProjectContext"/> from it plus the bearer token in
/// <c>.claude/context-platform.local.json</c> (if that file exists — see
/// <see cref="Context.Claude.IClaudeIntegrationInstaller"/>). Runs synchronously, once, right after
/// the DI container is built and before the host starts accepting requests or running any
/// <see cref="Microsoft.Extensions.Hosting.IHostedService"/> — every hook endpoint, the file watcher, and every MCP tool's
/// default <c>projectId</c> depend on this having already happened.
/// </summary>
public static class HostProjectContextInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, string projectRoot)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Context.Host.Startup");
        var hostProject = services.GetRequiredService<HostProjectContext>();

        var (configProjectId, bearerToken) = await TryReadLocalConfigAsync(projectRoot, logger).ConfigureAwait(false);

        using var scope = services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        // Look up by the id from .claude/context-platform.local.json FIRST, before falling back to
        // root-path matching: the same mounted repo (and thus the same local config file) can be
        // served from different effective root paths across environments — e.g. a bare-metal run
        // sees "C:\...\repo" while a container run of the identical repo sees "/workspace" — so a
        // root-path-only lookup would treat them as two different projects and then fail inserting a
        // second row with the SAME configProjectId (UNIQUE constraint on Projects.Id). Reusing the
        // existing row (and just updating its RootPath) keeps memories/sessions/index data attached
        // to the one logical project regardless of which environment last touched it.
        var project = configProjectId is { } id
            ? await projects.GetByIdAsync(id, CancellationToken.None).ConfigureAwait(false)
            : null;
        project ??= await projects.GetByRootPathAsync(projectRoot, CancellationToken.None).ConfigureAwait(false);

        if (project is not null && !string.Equals(project.RootPath, projectRoot, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Project {ProjectId} root path changed from '{OldRoot}' to '{NewRoot}' (different environment serving the same project).",
                project.Id, project.RootPath, projectRoot);
            project.RootPath = projectRoot;
            await projects.UpdateAsync(project, CancellationToken.None).ConfigureAwait(false);
        }

        if (project is null)
        {
            project = await projects.AddAsync(new Project
            {
                Id = configProjectId ?? Guid.NewGuid(),
                Name = new DirectoryInfo(projectRoot).Name,
                RootPath = projectRoot,
                CreatedAt = DateTimeOffset.UtcNow
            }, CancellationToken.None).ConfigureAwait(false);

            logger.LogInformation("Created project {ProjectId} for root '{ProjectRoot}'.", project.Id, projectRoot);
        }

        hostProject.Initialize(project.Id, projectRoot, bearerToken);

        if (bearerToken is null)
        {
            logger.LogWarning(
                "No usable '.claude/context-platform.local.json' bearer token found under '{ProjectRoot}' — " +
                "/hooks/* endpoints will reject every request until `context claude install` is run for this project.",
                projectRoot);
        }

        logger.LogInformation("Context.Host is bound to project {ProjectId} at '{ProjectRoot}'.", project.Id, projectRoot);
    }

    private static async Task<(Guid? ProjectId, string? BearerToken)> TryReadLocalConfigAsync(string projectRoot, ILogger logger)
    {
        var path = Path.Combine(projectRoot, ".claude", "context-platform.local.json");
        if (!System.IO.File.Exists(path))
        {
            return (null, null);
        }

        try
        {
            var json = await System.IO.File.ReadAllTextAsync(path).ConfigureAwait(false);
            var config = JsonSerializer.Deserialize<HookLocalConfig>(json);
            if (config is null || string.IsNullOrWhiteSpace(config.BearerToken))
            {
                return (null, null);
            }

            var projectId = Guid.TryParse(config.ProjectId, out var parsed) ? parsed : (Guid?)null;
            return (projectId, config.BearerToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read/parse '{Path}'.", path);
            return (null, null);
        }
    }
}
