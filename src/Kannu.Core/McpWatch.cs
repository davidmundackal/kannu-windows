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

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kannu.Core;

/// <summary>
/// "A new MCP server appeared": reads the MCP server names each agent's settings declare, remembers
/// them per file, and reports a name that was not there before. The first look at a file only learns
/// it (trust on first use). Names and a short "runs" line only: never env values or headers, and URLs
/// cut to scheme and host. Port of macOS <c>MCPServerWatch</c>, with Windows locations.
/// </summary>
public static class McpWatch
{
    public enum Format
    {
        /// <summary><c>{"mcpServers": {...}}</c>: Claude Desktop, Cursor, Gemini CLI, Qwen Code, <c>.mcp.json</c>.</summary>
        McpServers,

        /// <summary><c>{"servers": {...}}</c>: VS Code's <c>mcp.json</c>.</summary>
        VsCodeServers,

        /// <summary><c>{"mcp": {...}}</c>: opencode.</summary>
        Opencode,

        /// <summary><c>~/.claude.json</c>: user-scope <c>mcpServers</c> plus <c>projects.&lt;path&gt;.mcpServers</c>.</summary>
        ClaudeUserConfig,

        /// <summary><c>[mcp_servers.&lt;name&gt;]</c> tables in Codex's <c>config.toml</c>.</summary>
        CodexToml,
    }

    /// <param name="Lenient">Comments and trailing commas allowed (VS Code, Gemini CLI, Qwen Code, opencode).</param>
    public sealed record Location(string Path, string AppName, Format Format, bool Lenient, string? ProjectRoot);

    public sealed record Server(string? Scope, string Name, string Runs)
    {
        public string Key => (Scope ?? "") + "|" + Name;
    }

    public sealed class Baseline
    {
        public const int Cap = 300;
        public Dictionary<string, List<string>> ServersByConfig { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed record Addition(string ConfigPath, string AppName, string? ProjectRoot, string? Scope, string Name, string Runs, long FirstSeenMs)
    {
        public string Key => (Scope ?? "") + "|" + Name;

        public SecurityFinding Finding(string home)
        {
            string Shown(string path) => path.Length > home.Length && path.StartsWith(home, StringComparison.OrdinalIgnoreCase)
                                         && path[home.Length] is '\\' or '/'
                ? "~" + path[home.Length..]
                : path;
            var place = $"In {Shown(ConfigPath)}" + (Scope is { } scope ? $" · project {Shown(scope)}" : "");
            return new SecurityFinding
            {
                // The time is in the id: removed and added again is news again.
                Id = SecurityFindings.StableId(SecurityFindings.McpAddedRule, ConfigPath, [Key, FirstSeenMs.ToString(CultureInfo.InvariantCulture)]),
                Rule = SecurityFindings.McpAddedRule,
                Severity = FindingSeverity.Medium,
                Title = $"New MCP server: {Name}",
                Summary = $"“{Name}” was added to {AppName}'s MCP servers. If you did not add it, check where it came from.",
                Evidence = new[] { Runs.Length > 0 ? $"Runs: {Runs}" : null, place, SecurityFindings.FirstSeen(FirstSeenMs) }.OfType<string>().ToList(),
                RevealPath = ConfigPath,
                FirstSeenMs = FirstSeenMs,
                LastSeenMs = FirstSeenMs,
                GroupSubject = $"{ConfigPath}|{Key}",
            };
        }
    }

    public static IReadOnlyList<Location> GlobalLocations(string home, string appData)
    {
        string H(params string[] parts) => Path.Combine([home, .. parts]);
        string A(params string[] parts) => Path.Combine([appData, .. parts]);
        return
        [
            new(H(".claude.json"), "Claude Code", Format.ClaudeUserConfig, false, null),
            new(H(".claude", "mcp.json"), "Claude Code", Format.McpServers, false, null),
            new(A("Claude", "claude_desktop_config.json"), "Claude Desktop", Format.McpServers, false, null),
            new(H(".cursor", "mcp.json"), "Cursor", Format.McpServers, false, null),
            new(A("Code", "User", "mcp.json"), "VS Code", Format.VsCodeServers, true, null),
            new(H(".codex", "config.toml"), "Codex", Format.CodexToml, false, null),
            new(H(".gemini", "settings.json"), "Gemini CLI", Format.McpServers, true, null),
            new(H(".qwen", "settings.json"), "Qwen Code", Format.McpServers, true, null),
            new(H(".config", "opencode", "opencode.json"), "opencode", Format.Opencode, true, null),
        ];
    }

    public static IReadOnlyList<Location> ProjectLocations(string root) =>
    [
        new(Path.Combine(root, ".mcp.json"), "Claude Code", Format.McpServers, false, root),
        new(Path.Combine(root, ".cursor", "mcp.json"), "Cursor", Format.McpServers, false, root),
        new(Path.Combine(root, ".vscode", "mcp.json"), "VS Code", Format.VsCodeServers, true, root),
        new(Path.Combine(root, ".gemini", "settings.json"), "Gemini CLI", Format.McpServers, true, root),
        new(Path.Combine(root, ".qwen", "settings.json"), "Qwen Code", Format.McpServers, true, root),
        new(Path.Combine(root, "opencode.json"), "opencode", Format.Opencode, true, root),
    ];

    /// <summary>
    /// Sessions' working folders worth a look: absolute, not the home folder (its configs are the
    /// global ones), not a drive root, not a network share, at most <paramref name="limit"/>.
    /// </summary>
    public static IReadOnlyList<string> ProjectRoots(IEnumerable<string?> cwds, string home, int limit = 20)
    {
        var result = new List<string>();
        foreach (var cwd in cwds)
        {
            if (string.IsNullOrWhiteSpace(cwd) || !SecurityFindings.IsAbsolute(cwd) || cwd.StartsWith(@"\\", StringComparison.Ordinal)) continue;
            var root = cwd.TrimEnd('\\', '/');
            if (root.Length <= 3 || string.Equals(root, home.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) continue;
            if (result.Contains(root, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(root);
            if (result.Count >= limit) break;
        }
        return result;
    }

    /// <summary>Servers in one file's text; null when it cannot be read as that format (a half-written file must not look like "every server was removed").</summary>
    public static IReadOnlyList<Server>? Servers(string text, Format format, bool lenient)
    {
        if (format == Format.CodexToml) return CodexServers(text);
        if (string.IsNullOrWhiteSpace(text)) return [];
        JsonObject root;
        try
        {
            var options = lenient ? new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true } : default;
            if (JsonNode.Parse(text, documentOptions: options) is not JsonObject parsed) return null;
            root = parsed;
        }
        catch (JsonException)
        {
            return null;
        }
        switch (format)
        {
            case Format.McpServers: return Entries(root["mcpServers"], null);
            case Format.VsCodeServers: return Entries(root["servers"], null);
            case Format.Opencode: return Entries(root["mcp"], null);
            default:
                var result = Entries(root["mcpServers"], null).ToList();
                if (root["projects"] is JsonObject projects)
                {
                    foreach (var (path, project) in projects.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        if (project is JsonObject p) result.AddRange(Entries(p["mcpServers"], Sanitized(path, 300)));
                    }
                }
                return result;
        }
    }

    private static IReadOnlyList<Server> Entries(JsonNode? value, string? scope)
    {
        if (value is not JsonObject table) return [];
        var result = new List<Server>();
        foreach (var (raw, entry) in table.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var name = Sanitized(raw, 80);
            if (name.Length > 0) result.Add(new Server(scope, name, Runs(entry as JsonObject)));
        }
        return result;
    }

    /// <summary><c>[mcp_servers.name]</c> and <c>[mcp_servers."dotted.name"]</c>; sub-tables name the same server.</summary>
    public static IReadOnlyList<Server> CodexServers(string text)
    {
        var names = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            const string header = "[mcp_servers.";
            if (!line.StartsWith(header, StringComparison.Ordinal)) continue;
            var rest = line[header.Length..];
            string name;
            if (rest.StartsWith('"'))
            {
                var close = rest.IndexOf('"', 1);
                if (close < 0) continue;
                name = rest[1..close];
            }
            else
            {
                name = new string(rest.TakeWhile(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
            }
            var clean = Sanitized(name, 80);
            if (clean.Length > 0 && !names.Contains(clean)) names.Add(clean);
        }
        return names.Select(n => new Server(null, n, "")).ToList();
    }

    /// <summary>A short, non-secret description of what a server runs.</summary>
    public static string Runs(JsonObject? entry)
    {
        if (entry is null) return "";
        foreach (var key in new[] { "url", "serverUrl", "httpUrl" })
        {
            if (Str(entry[key]) is { } raw && Uri.TryCreate(raw, UriKind.Absolute, out var url) && url.Host.Length > 0)
            {
                return Sanitized($"{url.Scheme}://{url.Host}", 120);
            }
        }
        var command = Str(entry["command"]);
        var args = entry["args"] is JsonArray a ? a.Select(Str).OfType<string>().ToList() : [];
        if (command is null && entry["command"] is JsonArray list)
        {
            var strings = list.Select(Str).OfType<string>().ToList();
            command = strings.FirstOrDefault();
            args = strings.Skip(1).ToList();
        }
        if (string.IsNullOrEmpty(command)) return "";
        var parts = new List<string> { Sanitized(Path.GetFileName(command.Replace('\\', '/').Split('/')[^1]), 40) };
        if (args.FirstOrDefault(IsPackageLike) is { } package) parts.Add(package);
        return string.Join(" ", parts.Where(p => p.Length > 0));
    }

    /// <summary>"@scope/name", "name@1.2.3", "ghcr.io/org/image": lowercase package-like text only, so a token passed as an argument is never shown.</summary>
    public static bool IsPackageLike(string arg)
    {
        if (arg.Length is < 2 or > 80 || arg.StartsWith('-')) return false;
        if (!arg.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '@' or '/' or '.' or '_' or '-') || !arg.Any(c => c is >= 'a' and <= 'z')) return false;
        var run = 0;
        foreach (var c in arg)
        {
            run = c is >= 'a' and <= 'z' or >= '0' and <= '9' ? run + 1 : 0;
            if (run >= 24) return false;
        }
        return true;
    }

    /// <summary>New servers in files already known; every read file's servers become the new baseline.</summary>
    public static IReadOnlyList<Addition> Compare(Baseline baseline, IReadOnlyDictionary<string, IReadOnlyList<Server>> reads,
        IReadOnlyList<Location> locations, long nowMs)
    {
        var additions = new List<Addition>();
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in locations)
        {
            if (!handled.Add(location.Path) || !reads.TryGetValue(location.Path, out var servers)) continue;
            if (baseline.ServersByConfig.TryGetValue(location.Path, out var known))
            {
                var knownKeys = known.ToHashSet();
                var added = new HashSet<string>();
                foreach (var server in servers)
                {
                    if (knownKeys.Contains(server.Key) || !added.Add(server.Key)) continue;
                    additions.Add(new Addition(location.Path, location.AppName, location.ProjectRoot, server.Scope, server.Name, server.Runs, nowMs));
                }
            }
            baseline.ServersByConfig[location.Path] = servers.Select(s => s.Key).Distinct().Order(StringComparer.Ordinal).ToList();
        }
        if (baseline.ServersByConfig.Count > Baseline.Cap)
        {
            var current = locations.Select(l => l.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in baseline.ServersByConfig.Keys.Order(StringComparer.Ordinal).ToList())
            {
                if (current.Contains(path)) continue;
                baseline.ServersByConfig.Remove(path);
                if (baseline.ServersByConfig.Count <= Baseline.Cap) break;
            }
        }
        return additions;
    }

    /// <summary>An addition goes once its server is no longer in its file (a file not read this time keeps it).</summary>
    public static List<Addition> Pruning(IEnumerable<Addition> additions, IReadOnlyDictionary<string, IReadOnlyList<Server>> reads) =>
        additions.Where(a => !reads.TryGetValue(a.ConfigPath, out var servers) || servers.Any(s => s.Key == a.Key)).ToList();

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Sanitized(string text, int limit) => new(text.Where(c => c is >= ' ' and <= '~').Take(limit).ToArray());
}
