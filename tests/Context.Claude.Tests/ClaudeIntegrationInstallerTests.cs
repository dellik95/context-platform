using System.Text.Json.Nodes;
using Context.Claude.Installation;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Context.Claude.Tests;

/// <summary>
/// Covers <see cref="ClaudeIntegrationInstaller.InstallMcpOnlyAsync"/>, the narrow "just register
/// MCP" path <c>POST /api/projects</c> calls automatically at project-creation time (see
/// <see cref="IClaudeIntegrationInstaller.InstallMcpOnlyAsync"/>'s remarks). It must write exactly
/// <c>.mcp.json</c> and <c>.claude/context-platform.local.json</c>, and must NOT write any of the
/// other four artifacts <see cref="ClaudeIntegrationInstaller.InstallAsync"/>'s full install
/// produces (hook scripts, <c>.claude/settings.json</c>, <c>.claude-plugin/plugin.json</c>, the
/// skill doc) or touch <c>.gitignore</c>.
/// </summary>
public sealed class ClaudeIntegrationInstallerTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly ClaudeIntegrationInstaller _installer;

    public ClaudeIntegrationInstallerTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "context-platform-claude-installer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
        _installer = new ClaudeIntegrationInstaller(Options.Create(new ClaudeIntegrationOptions()));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_projectRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task InstallMcpOnlyAsync_WritesMcpJsonAndLocalConfig_WithTheSuppliedToken()
    {
        var projectId = Guid.NewGuid();
        const string token = "explicit-test-token";

        await _installer.InstallMcpOnlyAsync(projectId, _projectRoot, CancellationToken.None, bearerToken: token);

        var mcpPath = Path.Combine(_projectRoot, ".mcp.json");
        File.Exists(mcpPath).Should().BeTrue(".mcp.json is the whole point of the MCP-only install");
        var mcpJson = JsonNode.Parse(await File.ReadAllTextAsync(mcpPath))!.AsObject();
        var entry = mcpJson["mcpServers"]!["context-platform"]!.AsObject();
        entry["type"]!.GetValue<string>().Should().Be("http");
        entry["headers"]!["Authorization"]!.GetValue<string>().Should().Be($"Bearer {token}");

        var localConfigPath = Path.Combine(_projectRoot, ".claude", "context-platform.local.json");
        File.Exists(localConfigPath).Should().BeTrue("the local bearer-token/port config is the other half of the MCP-only install");
        var localConfig = JsonNode.Parse(await File.ReadAllTextAsync(localConfigPath))!.AsObject();
        localConfig["projectId"]!.GetValue<string>().Should().Be(projectId.ToString());
        localConfig["bearerToken"]!.GetValue<string>().Should().Be(token);
    }

    [Fact]
    public async Task InstallMcpOnlyAsync_DoesNotWriteAnyOfTheFullInstallsOtherFourArtifacts()
    {
        await _installer.InstallMcpOnlyAsync(Guid.NewGuid(), _projectRoot, CancellationToken.None, bearerToken: "token");

        Directory.Exists(Path.Combine(_projectRoot, ".claude", "hooks"))
            .Should().BeFalse("InstallMcpOnlyAsync must not write hook scripts");
        File.Exists(Path.Combine(_projectRoot, ".claude", "settings.json"))
            .Should().BeFalse("InstallMcpOnlyAsync must not touch .claude/settings.json");
        File.Exists(Path.Combine(_projectRoot, ".claude-plugin", "plugin.json"))
            .Should().BeFalse("InstallMcpOnlyAsync must not write the plugin manifest");
        File.Exists(Path.Combine(_projectRoot, ".claude", "skills", "context-memory", "SKILL.md"))
            .Should().BeFalse("InstallMcpOnlyAsync must not write the skill doc (that's InstallSkillAsync's job)");
        File.Exists(Path.Combine(_projectRoot, ".gitignore"))
            .Should().BeFalse("InstallMcpOnlyAsync must not touch .gitignore");
    }

    /// <summary>
    /// Same token-reuse precedence as <see cref="ClaudeIntegrationInstaller.InstallAsync"/>: with no
    /// caller-supplied token, an already-on-disk token from a prior install must be kept rather than
    /// replaced by a freshly generated one.
    /// </summary>
    [Fact]
    public async Task InstallMcpOnlyAsync_ReusesAnExistingOnDiskTokenWhenNoneIsSupplied()
    {
        var projectId = Guid.NewGuid();
        await _installer.InstallMcpOnlyAsync(projectId, _projectRoot, CancellationToken.None, bearerToken: "first-token");

        await _installer.InstallMcpOnlyAsync(projectId, _projectRoot, CancellationToken.None, bearerToken: null);

        var localConfigPath = Path.Combine(_projectRoot, ".claude", "context-platform.local.json");
        var localConfig = JsonNode.Parse(await File.ReadAllTextAsync(localConfigPath))!.AsObject();
        localConfig["bearerToken"]!.GetValue<string>().Should().Be("first-token");
    }
}
