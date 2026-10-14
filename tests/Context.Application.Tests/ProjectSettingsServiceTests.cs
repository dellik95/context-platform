using System.Text.Json;
using Context.Application.Settings;
using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Context.Providers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;

namespace Context.Application.Tests;

public sealed class ProjectSettingsServiceTests
{
    [Fact]
    public async Task GetAsync_applies_local_over_database_over_appsettings_defaults()
    {
        var projectId = Guid.NewGuid();
        var root = CreateTempProjectRoot();
        Directory.CreateDirectory(Path.Combine(root, ".claude"));

        var appsettingsDefaults = BuildSettings("embed-default", "llm-default", 3);
        var databaseSettings = BuildSettings("embed-db", "llm-db", 5);
        var localSettings = BuildSettings("embed-local", "llm-local", 7);

        var project = new Project
        {
            Id = projectId,
            Name = "demo",
            RootPath = root,
            CreatedAt = DateTimeOffset.UtcNow,
            ConfigJson = JsonSerializer.Serialize(databaseSettings)
        };

        var localJson = JsonSerializer.Serialize(new
        {
            projectId = projectId.ToString(),
            bearerToken = "token",
            settings = localSettings
        });
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, ".claude", "context-platform.local.json"), localJson);

        var repo = new Mock<IProjectRepository>();
        repo.Setup(r => r.GetByIdAsync(projectId, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ContextPlatform:ProjectSettingsDefaults:EmbeddingProvider"] = appsettingsDefaults.EmbeddingProvider,
            ["ContextPlatform:ProjectSettingsDefaults:LlmProvider"] = appsettingsDefaults.LlmProvider,
            ["ContextPlatform:ProjectSettingsDefaults:MaxMemoryNotesPerSection"] = appsettingsDefaults.MaxMemoryNotesPerSection.ToString()
        }).Build();

        var service = new ProjectSettingsService(repo.Object, Options.Create(new ContextProvidersOptions()), config);

        var effective = await service.GetAsync(projectId, CancellationToken.None);

        effective.EmbeddingProvider.Should().Be("embed-local");
        effective.LlmProvider.Should().Be("llm-local");
        effective.MaxMemoryNotesPerSection.Should().Be(7);
    }

    [Fact]
    public async Task GetAsync_keeps_the_database_scheduler_stamps_when_a_local_settings_file_overrides()
    {
        var projectId = Guid.NewGuid();
        var root = CreateTempProjectRoot();
        Directory.CreateDirectory(Path.Combine(root, ".claude"));

        var reindexedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var reconsolidatedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var databaseSettings = BuildSettings("embed-db", "llm-db", 5) with
        {
            LastReindexScheduledAt = reindexedAt,
            LastMemoryReconsolidationScheduledAt = reconsolidatedAt,
        };
        var localSettings = BuildSettings("embed-local", "llm-local", 7) with
        {
            LastReindexScheduledAt = null,
            LastMemoryReconsolidationScheduledAt = DateTimeOffset.UnixEpoch,
        };

        var project = new Project
        {
            Id = projectId,
            Name = "demo",
            RootPath = root,
            CreatedAt = DateTimeOffset.UtcNow,
            ConfigJson = JsonSerializer.Serialize(databaseSettings)
        };
        await System.IO.File.WriteAllTextAsync(
            Path.Combine(root, ".claude", "context-platform.local.json"),
            JsonSerializer.Serialize(new { settings = localSettings }));

        var repo = new Mock<IProjectRepository>();
        repo.Setup(r => r.GetByIdAsync(projectId, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        var service = new ProjectSettingsService(repo.Object, Options.Create(new ContextProvidersOptions()));

        var effective = await service.GetAsync(projectId, CancellationToken.None);

        effective.EmbeddingProvider.Should().Be("embed-local");
        effective.LastReindexScheduledAt.Should().Be(reindexedAt);
        effective.LastMemoryReconsolidationScheduledAt.Should().Be(reconsolidatedAt);
    }

    [Fact]
    public async Task UpdateAsync_with_both_target_updates_database_and_local_file()
    {
        var projectId = Guid.NewGuid();
        var root = CreateTempProjectRoot();
        var project = new Project
        {
            Id = projectId,
            Name = "demo",
            RootPath = root,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var repo = new Mock<IProjectRepository>();
        repo.Setup(r => r.GetByIdAsync(projectId, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        repo.Setup(r => r.UpdateAsync(project, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var service = new ProjectSettingsService(
            repo.Object,
            Options.Create(new ContextProvidersOptions()),
            new ConfigurationBuilder().Build());

        var updated = BuildSettings("embed-x", "llm-x", 11);

        await service.UpdateAsync(projectId, updated, ProjectSettingsWriteTarget.Both, CancellationToken.None);

        project.ConfigJson.Should().NotBeNullOrWhiteSpace();
        var dbSaved = JsonSerializer.Deserialize<ProjectSettingsDto>(project.ConfigJson!);
        dbSaved.Should().NotBeNull();
        dbSaved!.MaxMemoryNotesPerSection.Should().Be(11);

        var localPath = Path.Combine(root, ".claude", "context-platform.local.json");
        System.IO.File.Exists(localPath).Should().BeTrue();

        var localRoot = JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(localPath));
        localRoot.RootElement.TryGetProperty("settings", out var settingsNode).Should().BeTrue();
        settingsNode.GetProperty("MaxMemoryNotesPerSection").GetInt32().Should().Be(11);
    }

    [Fact]
    public async Task UpdateAsync_with_both_target_leaves_the_database_untouched_when_the_local_file_cannot_be_written()
    {
        var projectId = Guid.NewGuid();
        var root = CreateTempProjectRoot();
        // A file where the .claude directory belongs makes the local write fail.
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, ".claude"), "not a directory");
        var project = new Project { Id = projectId, Name = "demo", RootPath = root, CreatedAt = DateTimeOffset.UtcNow, ConfigJson = null };
        var repo = new Mock<IProjectRepository>();
        repo.Setup(r => r.GetByIdAsync(projectId, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        var service = new ProjectSettingsService(repo.Object, Options.Create(new ContextProvidersOptions()), new ConfigurationBuilder().Build());

        var act = () => service.UpdateAsync(projectId, BuildSettings("embed-x", "llm-x", 11), ProjectSettingsWriteTarget.Both, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        project.ConfigJson.Should().BeNull("the database is only written once the overriding local file is");
        repo.Verify(r => r.UpdateAsync(It.IsAny<Project>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ProjectSettingsDto BuildSettings(string embedding, string llm, int maxMemory) =>
        new(
            EmbeddingProvider: embedding,
            LlmProvider: llm,
            OllamaBaseUrl: null,
            OllamaEmbeddingModel: null,
            OllamaChatModel: null,
            ReindexEnabled: false,
            ReindexIntervalMinutes: 60,
            LastReindexScheduledAt: null,
            SessionSummaryIntervalMinutes: 0,
            MinEventsForSessionSummary: 15,
            MaxMemoryNotesPerSection: maxMemory);

    private static string CreateTempProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "context-platform-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
