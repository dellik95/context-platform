using System.Text.Json.Serialization;

namespace Context.Host.Hooks;

/// <summary>
/// Shape of <c>.claude/context-platform.local.json</c>, written by
/// <see cref="Context.Claude.Installation.ClaudeIntegrationInstaller"/> — read once at startup
/// (see <c>Program.cs</c>) to learn this project's bearer token and the projectId the installer
/// baked into the generated hook scripts/config.
/// </summary>
public sealed record HookLocalConfig(
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("bearerToken")] string? BearerToken,
    [property: JsonPropertyName("generatedAt")] string? GeneratedAt);
