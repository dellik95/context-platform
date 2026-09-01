using System.Text.Json;
using System.Text.Json.Nodes;

namespace Context.Application.Settings;

/// <summary>
/// Reads the Dashboard Settings tab's saved project-local override file
/// (<c>.claude/context-platform.local.json</c>'s <c>"settings"</c> node) -- the source of truth
/// every provider-options setup class layers under env-var/appsettings config. Centralizes the
/// path + parse-failure-is-null logic that used to be duplicated between <see cref="ProjectSettingsService"/>
/// and Context.Host's <c>Program.cs</c>.
/// </summary>
public static class LocalProjectSettingsReader
{
    private static string PathFor(string projectRoot) => Path.Combine(projectRoot, ".claude", "context-platform.local.json");

    /// <summary>Synchronous read -- for <c>IConfigureOptions&lt;T&gt;.Configure</c> implementations, which have no async variant.</summary>
    public static ProjectSettingsDto? TryRead(string projectRoot)
    {
        var path = PathFor(projectRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static async Task<ProjectSettingsDto?> TryReadAsync(string projectRoot, CancellationToken ct)
    {
        var path = PathFor(projectRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static ProjectSettingsDto? Parse(string json)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            return root?["settings"]?.Deserialize<ProjectSettingsDto>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
