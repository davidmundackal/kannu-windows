// Kannu for Windows
// Copyright (C) 2026 Kannu Contributors
//
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
// General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with this program. If
// not, see <https://www.gnu.org/licenses/>.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kannu.Core;

/// <summary>
/// Adds Kannu's hooks to, and removes them from, Claude Code's <c>settings.json</c>. A pure text
/// transform: the caller owns reading, backing up and writing the file. Every other hook the user
/// has is preserved; Kannu's own entries are recognised by <see cref="Marker"/> in their command.
/// </summary>
public static class ClaudeSettingsHooks
{
    public const string Marker = "kannu-hook";

    /// <summary>Seconds Claude waits for the hook. It takes milliseconds; this only bounds a hang.</summary>
    public const int TimeoutSeconds = 5;

    private sealed record Group(string Event, string? Matcher, RawState? ForcedState);

    private static readonly Group[] Groups =
    [
        new("SessionStart", null, null),
        new("UserPromptSubmit", null, null),
        new("PreToolUse", "*", null),
        new("PostToolUse", "*", null),
        new("PostToolUseFailure", "*", null),
        new("PermissionRequest", "*", null),
        // Prompts on screen, and Claude waiting on the next prompt (as on macOS); auth notices are not yellow.
        new("Notification", "permission_prompt|idle_prompt|elicitation_dialog", RawState.AwaitingInput),
        new("Stop", null, null),
        new("StopFailure", null, null),
        new("SessionEnd", null, null),
    ];

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// The command line Claude runs. Forward slashes and quotes, because Claude Code on Windows may run
    /// hook commands through Git Bash, where backslashes are escapes; cmd accepts the same form.
    /// </summary>
    public static string Command(string hookExePath, RawState? forcedState = null)
    {
        var command = $"\"{hookExePath.Replace('\\', '/')}\" claude";
        return forcedState is { } state ? $"{command} --state {state.ToWire()}" : command;
    }

    /// <summary>Returns <paramref name="settingsJson"/> with Kannu's hooks installed (replacing any older ones).</summary>
    /// <exception cref="InvalidDataException">The file is not a JSON object; it must not be overwritten.</exception>
    public static string Install(string? settingsJson, string hookExePath)
    {
        var root = ParseRoot(settingsJson);
        RemoveFrom(root);

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        foreach (var group in Groups)
        {
            if (hooks[group.Event] is not JsonArray list)
            {
                list = new JsonArray();
                hooks[group.Event] = list;
            }

            var entry = new JsonObject();
            if (group.Matcher is not null) entry["matcher"] = group.Matcher;
            entry["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = Command(hookExePath, group.ForcedState),
                ["timeout"] = TimeoutSeconds,
            });
            list.Add((JsonNode)entry);
        }

        return root.ToJsonString(WriteOptions);
    }

    /// <summary>Returns <paramref name="settingsJson"/> with every Kannu hook removed and nothing else changed.</summary>
    /// <exception cref="InvalidDataException">The file is not a JSON object; it must not be overwritten.</exception>
    public static string Remove(string? settingsJson)
    {
        var root = ParseRoot(settingsJson);
        RemoveFrom(root);
        return root.ToJsonString(WriteOptions);
    }

    public static bool IsInstalled(string? settingsJson)
    {
        try
        {
            return ParseRoot(settingsJson)["hooks"] is JsonObject hooks
                && hooks.Any(e => e.Value is JsonArray groups && groups.Any(IsOrHoldsKannuHook));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static JsonObject ParseRoot(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return new JsonObject();
        try
        {
            return JsonNode.Parse(settingsJson, documentOptions: ParseOptions) as JsonObject
                ?? throw new InvalidDataException("settings.json is not a JSON object.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"settings.json is not valid JSON: {e.Message}", e);
        }
    }

    private static void RemoveFrom(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;

        foreach (var eventName in hooks.Select(e => e.Key).ToList())
        {
            if (hooks[eventName] is not JsonArray groups) continue;

            for (var i = groups.Count - 1; i >= 0; i--)
            {
                if (groups[i] is not JsonObject group || group["hooks"] is not JsonArray entries) continue;
                for (var j = entries.Count - 1; j >= 0; j--)
                {
                    if (IsKannuEntry(entries[j])) entries.RemoveAt(j);
                }
                if (entries.Count == 0) groups.RemoveAt(i);
            }
            if (groups.Count == 0) hooks.Remove(eventName);
        }
        if (hooks.Count == 0) root.Remove("hooks");
    }

    private static bool IsOrHoldsKannuHook(JsonNode? group) =>
        group is JsonObject g && g["hooks"] is JsonArray entries && entries.Any(IsKannuEntry);

    private static bool IsKannuEntry(JsonNode? entry) =>
        entry is JsonObject e
        && e["command"] is JsonValue v
        && v.TryGetValue<string>(out var command)
        && command.Contains(Marker, StringComparison.OrdinalIgnoreCase);
}
