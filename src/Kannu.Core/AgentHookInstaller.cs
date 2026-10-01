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

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kannu.Core;

/// <summary>An install or uninstall that changed nothing, with the reason to show the user.</summary>
public sealed class HookInstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Adds Kannu's hook to, and removes it from, every agent's settings, following
/// <see cref="AgentHookLayout"/>. Everything else in those files — including the user's own hooks —
/// is kept. A port of the macOS <c>AgentHookInstaller</c>, pointed at <c>kannu-hook.exe</c>.
/// </summary>
public sealed partial class AgentHookInstaller(AgentHookLayout layout, string hookExePath)
{
    /// <summary>Commands Kannu wrote, on Windows and (for files shared with a Mac) macOS.</summary>
    private static readonly string[] OwnMarkers = ["kannu-hook", "kannu-agent-status", "atoll-agent-status"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public AgentHookLayout Layout { get; } = layout;

    public string HookExePath { get; } = hookExePath;

    /// <summary>
    /// The command an agent runs. Forward slashes work in cmd, PowerShell and Git Bash alike, and so
    /// does a bare path; quotes would break PowerShell, so they are added only when the path has a space.
    /// </summary>
    public string Command(AgentProvider provider, HookEntry entry)
    {
        var path = HookExePath.Replace('\\', '/');
        if (path.Contains(' ')) path = $"\"{path}\"";
        var command = $"{path} {entry.State} {provider.Id()} {entry.Event}";
        return entry.Matcher is null ? command : $"{command} {entry.MatcherKey}";
    }

    public void Install(AgentProvider provider)
    {
        var files = Layout.For(provider);
        switch (provider)
        {
            case AgentProvider.Cursor:
                Merge(files.Configs[0], provider, required: true);
                break;
            case AgentProvider.VSCode:
                WriteCopilotHookFile(files.Configs[0].Path);
                break;
            case AgentProvider.Codex:
                Merge(files.Configs[0], provider, required: true);
                EnableCodexHooks(files.SharedSettings[0]);
                break;
            case AgentProvider.Opencode:
                WriteText(files.Configs[0].Path, OpencodePluginSource.Source(HookExePath));
                break;
            default:
                // Antigravity reads whichever of its files exists: each is merged into its own content.
                // A broken optional file is skipped, never overwritten; only the always-written one fails.
                foreach (var config in files.Configs)
                {
                    if (config.Write == AgentHookLayout.WritePolicy.Always) Merge(config, provider, required: true);
                    else if (File.Exists(config.Path)) Merge(config, provider, required: false);
                }
                break;
        }
    }

    public void Uninstall(AgentProvider provider)
    {
        foreach (var config in Layout.For(provider).Configs)
        {
            if (config.Shape == AgentHookLayout.Shape.OwnFile)
            {
                // opencode's plugin folder holds the user's plugins too: delete only Kannu's.
                if (provider != AgentProvider.Opencode || ReadOrEmpty(config.Path).Contains(OpencodePluginSource.MarkerPrefix))
                {
                    DeleteIfExists(config.Path);
                }
                continue;
            }
            try
            {
                Strip(config);
            }
            catch (HookInstallException) when (config.Write == AgentHookLayout.WritePolicy.OnlyIfPresent)
            {
                // A broken optional file is left as it is.
            }
        }
    }

    /// <summary>Installed: the hook exists and any file the layout lists carries every required entry.</summary>
    public bool IsInstalled(AgentProvider provider)
    {
        if (!File.Exists(HookExePath)) return false;
        var files = Layout.For(provider);
        if (provider == AgentProvider.Opencode) return ReadOrEmpty(files.Configs[0].Path).Contains(OpencodePluginSource.MarkerPrefix);
        var required = AgentHookLayout.RequiredEvents(provider);
        return files.Configs.Any(config => HasEntries(config, required));
    }

    // ---- JSON settings files ----

    private void Merge(AgentHookLayout.ConfigFile config, AgentProvider provider, bool required)
    {
        JsonObject root;
        try
        {
            root = ReadForMerge(config.Path);
        }
        catch (HookInstallException) when (!required)
        {
            return;
        }

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = [];
            root["hooks"] = hooks;
        }
        StripFrom(hooks, config.Shape);
        foreach (var entry in AgentHookLayout.Events(provider))
        {
            if (hooks[entry.Event] is not JsonArray list)
            {
                list = [];
                hooks[entry.Event] = list;
            }
            list.Add(config.Shape == AgentHookLayout.Shape.FlatEntries
                ? (JsonNode)new JsonObject { ["command"] = Command(provider, entry) }
                : Group(provider, entry));
        }
        if (provider == AgentProvider.Cursor && root["version"] is null) root["version"] = 1;
        WriteJson(config.Path, root);
    }

    private JsonObject Group(AgentProvider provider, HookEntry entry)
    {
        var handler = new JsonObject
        {
            ["type"] = "command",
            ["command"] = Command(provider, entry),
            ["timeout"] = AgentHookLayout.Timeout(provider),
        };
        if (provider == AgentProvider.Gemini) handler["name"] = AgentHookLayout.HandlerName;
        var group = new JsonObject();
        if (entry.Matcher is not null) group["matcher"] = entry.Matcher;
        group["hooks"] = new JsonArray(handler);
        return group;
    }

    private void WriteCopilotHookFile(string path)
    {
        var events = new JsonObject();
        foreach (var entry in AgentHookLayout.VSCodeEvents)
        {
            events[entry.Event] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = Command(AgentProvider.VSCode, entry),
                ["timeout"] = AgentHookLayout.Timeout(AgentProvider.VSCode),
            });
        }
        WriteJson(path, new JsonObject { ["hooks"] = events });
    }

    private static void Strip(AgentHookLayout.ConfigFile config)
    {
        if (!File.Exists(config.Path)) return;
        var root = ReadForMerge(config.Path);
        if (root["hooks"] is not JsonObject hooks) return;
        StripFrom(hooks, config.Shape);
        WriteJson(config.Path, root);
    }

    /// <summary>Removes Kannu's entries in the file's shape; events left empty are removed too.</summary>
    private static void StripFrom(JsonObject hooks, AgentHookLayout.Shape shape)
    {
        foreach (var name in hooks.Select(h => h.Key).ToList())
        {
            if (hooks[name] is not JsonArray list) continue;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var ours = shape == AgentHookLayout.Shape.FlatEntries ? IsOurs(list[i]) : GroupIsOurs(list[i]);
                if (ours) list.RemoveAt(i);
            }
            if (list.Count == 0) hooks.Remove(name);
        }
    }

    private static bool HasEntries(AgentHookLayout.ConfigFile config, IReadOnlyList<string> events)
    {
        if (config.Shape == AgentHookLayout.Shape.OwnFile) return File.Exists(config.Path);
        JsonObject root;
        try
        {
            if (!File.Exists(config.Path)) return false;
            root = ReadForMerge(config.Path);
        }
        catch (HookInstallException)
        {
            return false;
        }
        if (root["hooks"] is not JsonObject hooks) return false;
        return events.All(e => hooks[e] is JsonArray list && list.Any(item =>
            config.Shape == AgentHookLayout.Shape.FlatEntries ? IsOurs(item) : GroupIsOurs(item)));
    }

    private static bool GroupIsOurs(JsonNode? group) =>
        group is JsonObject g && g["hooks"] is JsonArray handlers && handlers.Any(h =>
            IsOurs(h) || (h is JsonObject o && o["name"] is JsonValue n && n.TryGetValue<string>(out var s) && s == AgentHookLayout.HandlerName));

    private static bool IsOurs(JsonNode? handler) =>
        handler is JsonObject o && o["command"] is JsonValue v && v.TryGetValue<string>(out var command)
        && OwnMarkers.Any(m => command.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A settings file to merge into: missing reads as empty; anything that is not a strict JSON object
    /// stops the install with nothing changed. A file with comments or trailing commas is refused rather
    /// than rewritten without them.
    /// </summary>
    internal static JsonObject ReadForMerge(string path)
    {
        if (!File.Exists(path)) return [];
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return [];
        try
        {
            if (JsonNode.Parse(text) is JsonObject strict) return strict;
        }
        catch (JsonException)
        {
            var lenient = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            try
            {
                if (JsonNode.Parse(text, documentOptions: lenient) is JsonObject)
                {
                    throw new HookInstallException(
                        $"{Path.GetFileName(path)} has comments or trailing commas. Kannu won't rewrite it and lose them — remove them, or add the hook by hand (nothing was changed).");
                }
            }
            catch (JsonException)
            {
            }
        }
        throw new HookInstallException($"{Path.GetFileName(path)} exists but isn't valid JSON — fix or remove it, then retry (nothing was changed).");
    }

    private static void WriteJson(string path, JsonObject root) => WriteText(path, root.ToJsonString(Indented) + "\n");

    /// <summary>Atomic: a temp file beside the target, renamed over it. The previous version is kept as <c>.kannu-backup</c>.</summary>
    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Copy(path, path + ".kannu-backup", overwrite: true);
        var temp = path + ".kannu-tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static string ReadOrEmpty(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    // ---- Codex: hooks.json only runs with features.hooks = true in config.toml ----

    /// <summary>Turns on <c>features.hooks</c> with the smallest text edit, leaving the rest of the TOML untouched.</summary>
    internal static void EnableCodexHooks(string tomlPath)
    {
        var text = File.Exists(tomlPath) ? File.ReadAllText(tomlPath) : "";
        var updated = WithCodexHooksEnabled(text);
        if (updated != text) WriteText(tomlPath, updated);
    }

    internal static string WithCodexHooksEnabled(string text)
    {
        if (DottedTrue().IsMatch(text)) return text;
        var dottedFalse = DottedFalse().Match(text);
        if (dottedFalse.Success)
        {
            return text[..dottedFalse.Index] + dottedFalse.Value.Replace("false", "true") + text[(dottedFalse.Index + dottedFalse.Length)..];
        }

        var section = FeaturesSection().Match(text);
        if (section.Success)
        {
            if (SectionTrue().IsMatch(section.Value)) return text;
            var sectionFalse = SectionFalse().Match(section.Value);
            string updatedSection;
            if (sectionFalse.Success)
            {
                updatedSection = section.Value[..sectionFalse.Index] + sectionFalse.Value.Replace("false", "true")
                                 + section.Value[(sectionFalse.Index + sectionFalse.Length)..];
            }
            else
            {
                var header = FeaturesHeader().Match(section.Value);
                var headerText = header.Value.EndsWith('\n') ? header.Value : header.Value + "\n";
                updatedSection = headerText + "hooks = true\n" + section.Value[header.Length..];
            }
            return text[..section.Index] + updatedSection + text[(section.Index + section.Length)..];
        }

        if (text.Length > 0 && !text.EndsWith('\n')) text += "\n";
        return text + "\n[features]\nhooks = true\n";
    }

    [GeneratedRegex(@"(?m)^\s*features\.hooks\s*=\s*true\b")]
    private static partial Regex DottedTrue();

    [GeneratedRegex(@"(?m)^\s*features\.hooks\s*=\s*false\b")]
    private static partial Regex DottedFalse();

    [GeneratedRegex(@"(?m)^\[features\][^\[]*")]
    private static partial Regex FeaturesSection();

    [GeneratedRegex(@"(?m)^\s*hooks\s*=\s*true\b")]
    private static partial Regex SectionTrue();

    [GeneratedRegex(@"(?m)^\s*hooks\s*=\s*false\b")]
    private static partial Regex SectionFalse();

    [GeneratedRegex(@"^\[features\][^\n]*\n?")]
    private static partial Regex FeaturesHeader();
}
