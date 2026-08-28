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

            if (hooksNode[hook.ClaudeEventName] is not JsonArray eventArray)
            {
                eventArray = new JsonArray();
                hooksNode[hook.ClaudeEventName] = eventArray;
            }

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

    /// <summary>Adds/overwrites the "context-platform" entry under mcpConfig["mcpServers"].</summary>
    public static void MergeMcpServer(JsonObject mcpConfig, string command, IReadOnlyList<string> args)
    {
        if (mcpConfig["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            mcpConfig["mcpServers"] = servers;
        }

        var argsArray = new JsonArray();
        foreach (var arg in args)
        {
            argsArray.Add(JsonValue.Create(arg));
        }

        servers["context-platform"] = new JsonObject
        {
            ["command"] = command,
            ["args"] = argsArray,
        };
    }

    private static string BuildHookCommand(HookDefinition hook, bool targetIsWindows) => targetIsWindows
        ? $"powershell -NoProfile -ExecutionPolicy Bypass -File \"$CLAUDE_PROJECT_DIR/.claude/hooks/{hook.FileBaseName}.ps1\""
        : $"bash \"$CLAUDE_PROJECT_DIR/.claude/hooks/{hook.FileBaseName}.sh\"";

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
