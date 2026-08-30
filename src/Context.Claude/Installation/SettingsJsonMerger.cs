using System.Text.Json.Nodes;

namespace Context.Claude.Installation;

/// <summary>
/// Merges Context Platform's hook wiring into an existing (possibly populated)
/// <c>.claude/settings.json</c> <see cref="JsonObject"/>, and its MCP server entry into an existing
/// <c>.mcp.json</c> <see cref="JsonObject"/> — without clobbering unrelated keys the user or other
/// tools may have already added. Idempotent: re-running against an already-merged document adds
/// nothing new.
/// </summary>
internal static class SettingsJsonMerger
{
    /// <summary>Adds a "command" hook entry for each <paramref name="hooks"/> under settings["hooks"][EventName].</summary>
    public static void MergeHooks(JsonObject settings, IEnumerable<HookDefinition> hooks, bool targetIsWindows)
    {
        if (settings["hooks"] is not JsonObject hooksNode)
        {
            hooksNode = new JsonObject();
            settings["hooks"] = hooksNode;
        }

        foreach (var hook in hooks)
        {
            var command = BuildHookCommand(hook, targetIsWindows);
            var otherPlatformCommand = BuildHookCommand(hook, !targetIsWindows);

            if (hooksNode[hook.ClaudeEventName] is not JsonArray eventArray)
            {
                eventArray = new JsonArray();
                hooksNode[hook.ClaudeEventName] = eventArray;
            }

            // A repo installed from more than one OS (e.g. bare-metal Windows, then a Linux
            // container mounting the same working tree) accumulates BOTH the .ps1 and .sh command
            // for every hook here, since each install only ever ADDED its own platform's command -
            // Claude Code then runs both on every single hook event. Only one variant can ever be
            // correct for whichever machine is running right now, so installing for one removes
            // the other rather than leaving it to double-fire alongside it.
            RemoveCommand(eventArray, otherPlatformCommand);

            if (ContainsCommand(eventArray, command))
            {
                continue;
            }

            var innerHooks = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                },
            };

            var entry = new JsonObject { ["hooks"] = innerHooks };
            if (string.Equals(hook.ClaudeEventName, "PostToolUse", StringComparison.Ordinal))
            {
                entry["matcher"] = "*";
            }

            eventArray.Add(entry);
        }
    }

    /// <summary>
    /// Adds/overwrites the "context-platform" entry under <c>mcpConfig["mcpServers"]</c> as an
    /// HTTP-type server pointing at Context.Host's own MCP endpoint (see
    /// <see cref="ClaudeIntegrationOptions.McpPath"/>) — there is no separate process to spawn.
    /// </summary>
    public static void MergeMcpServer(JsonObject mcpConfig, string url)
    {
        if (mcpConfig["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            mcpConfig["mcpServers"] = servers;
        }

        servers["context-platform"] = new JsonObject
        {
            ["type"] = "http",
            ["url"] = url,
        };
    }

    private static string BuildHookCommand(HookDefinition hook, bool targetIsWindows) => targetIsWindows
        ? $"powershell -NoProfile -ExecutionPolicy Bypass -File \"$CLAUDE_PROJECT_DIR/.claude/hooks/{hook.FileBaseName}.ps1\""
        : $"bash \"$CLAUDE_PROJECT_DIR/.claude/hooks/{hook.FileBaseName}.sh\"";

    /// <summary>Removes every top-level entry whose command matches, in place.</summary>
    private static void RemoveCommand(JsonArray eventArray, string command)
    {
        for (var i = eventArray.Count - 1; i >= 0; i--)
        {
            if (eventArray[i] is not JsonObject entry || entry["hooks"] is not JsonArray innerHooks)
            {
                continue;
            }

            var matches = innerHooks.OfType<JsonObject>()
                .Any(inner => string.Equals(inner["command"]?.GetValue<string>(), command, StringComparison.Ordinal));
            if (matches)
            {
                eventArray.RemoveAt(i);
            }
        }
    }

    private static bool ContainsCommand(JsonArray eventArray, string command)
    {
        foreach (var entry in eventArray.OfType<JsonObject>())
        {
            if (entry["hooks"] is not JsonArray innerHooks)
            {
                continue;
            }

            foreach (var inner in innerHooks.OfType<JsonObject>())
            {
                if (string.Equals(inner["command"]?.GetValue<string>(), command, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
