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
using Kannu.Core;
using Microsoft.Data.Sqlite;

namespace Kannu.Detection;

/// <summary>
/// Cursor's own state database (<c>%APPDATA%\Cursor\User\globalStorage\state.vscdb</c>, plus one per
/// workspace): composer status and titles, the plan registry, and Glass agent titles. Port of macOS
/// <c>CursorComposerStore</c> and <c>CursorGlassAgentStore</c>. Opened read-only and never held open;
/// a database Cursor holds locked or that is mid-write simply yields nothing this scan.
/// </summary>
public sealed class CursorDatabase(string cursorUserDirectory)
{
    private static readonly TimeSpan HeadersTtl = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PlanRegistryTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GlassTtl = TimeSpan.FromSeconds(3);

    private (DateTime At, Dictionary<string, ComposerMeta> Value)? _headers;
    private (DateTime At, HashSet<string> Value)? _planNames;
    private (DateTime At, Dictionary<string, string> Value)? _glassTitles;

    /// <summary>The default location on Windows.</summary>
    public static string DefaultUserDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User");

    public string GlobalDatabase => Path.Combine(cursorUserDirectory, "globalStorage", "state.vscdb");
    public string WorkspaceStorage => Path.Combine(cursorUserDirectory, "workspaceStorage");

    /// <summary>Composer metadata for these chats: headers first, then per-chat records, then workspace databases for the rest.</summary>
    public IReadOnlyDictionary<string, ComposerMeta> ComposerMeta(IReadOnlySet<string> ids, DateTime nowUtc)
    {
        if (ids.Count == 0) return new Dictionary<string, ComposerMeta>();
        var merged = new Dictionary<string, ComposerMeta>(Headers(nowUtc));
        foreach (var meta in DiskKV(ids)) Merge(merged, meta);

        var unresolved = ids.Where(id => !merged.ContainsKey(id)).ToHashSet();
        if (unresolved.Count > 0)
        {
            foreach (var meta in AllComposers(GlobalDatabase).Where(m => unresolved.Contains(m.ComposerId))) Merge(merged, meta);
            if (Directory.Exists(WorkspaceStorage))
            {
                foreach (var dir in SafeDirectories(WorkspaceStorage))
                {
                    foreach (var meta in AllComposers(Path.Combine(dir, "state.vscdb")).Where(m => unresolved.Contains(m.ComposerId))) Merge(merged, meta);
                }
            }
        }
        return merged.Where(m => ids.Contains(m.Key)).ToDictionary(m => m.Key, m => m.Value);
    }

    /// <summary>Plan names, which must never become chat titles.</summary>
    public IReadOnlySet<string> PlanRegistryNames(DateTime nowUtc)
    {
        if (_planNames is { } c && nowUtc - c.At < PlanRegistryTtl) return c.Value;
        var names = new HashSet<string>();
        if (ReadItem(GlobalDatabase, "composer.planRegistry") is { } json && Parse(json) is { RootElement: { ValueKind: JsonValueKind.Object } root } doc)
        {
            using (doc)
            {
                foreach (var entry in root.EnumerateObject())
                {
                    if (Str(entry.Value, "name")?.Trim() is { Length: > 0 } name) names.Add(name);
                }
            }
        }
        _planNames = (nowUtc, names);
        return names;
    }

    /// <summary>Glass agent titles: the first non-plan tab's label, in tab order.</summary>
    public IReadOnlyDictionary<string, string> GlassTitles(IReadOnlySet<string> ids, DateTime nowUtc)
    {
        if (_glassTitles is not { } c || nowUtc - c.At >= GlassTtl)
        {
            var titles = new Dictionary<string, string>();
            foreach (var (key, value) in Query(GlobalDatabase, "SELECT key, value FROM ItemTable WHERE key LIKE 'cursor/glass.tabs.v2/%/state.json'"))
            {
                var parts = key.Split('/');
                if (parts is not ["cursor", "glass.tabs.v2", _, var agentId, "state.json"]) continue;
                using var doc = Parse(value);
                if (doc?.RootElement is { ValueKind: JsonValueKind.Object } state && GlassTitle(state) is { } title) titles[agentId] = title;
            }
            _glassTitles = (nowUtc, titles);
        }
        return _glassTitles.Value.Value.Where(t => ids.Contains(t.Key)).ToDictionary(t => t.Key, t => t.Value);
    }

    internal static string? GlassTitle(JsonElement state)
    {
        if (!state.TryGetProperty("planTabs", out var tabs) || tabs.ValueKind != JsonValueKind.Array || tabs.GetArrayLength() == 0) return null;
        var byId = tabs.EnumerateArray().Where(t => Str(t, "id") is not null).GroupBy(t => Str(t, "id")!).ToDictionary(g => g.Key, g => g.First());
        if (state.TryGetProperty("tabOrder", out var order) && order.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in order.EnumerateArray())
            {
                if (id.ValueKind == JsonValueKind.String && byId.TryGetValue(id.GetString()!, out var tab) && TabLabel(tab) is { } label) return label;
            }
        }
        return tabs.EnumerateArray().Select(TabLabel).FirstOrDefault(l => l is not null);
    }

    private static string? TabLabel(JsonElement tab)
    {
        if (Str(tab, "kind")?.ToLowerInvariant() == "plan") return null;
        if (Str(tab, "label")?.Trim() is { Length: > 0 } label) return label;
        return tab.TryGetProperty("props", out var props) && Str(props, "planTitle")?.Trim() is { Length: > 0 } title ? title : null;
    }

    private Dictionary<string, ComposerMeta> Headers(DateTime nowUtc)
    {
        if (_headers is { } c && nowUtc - c.At < HeadersTtl) return c.Value;
        var result = new Dictionary<string, ComposerMeta>();
        if (ReadItem(GlobalDatabase, "composer.composerHeaders") is { } json && Parse(json) is { } doc)
        {
            using (doc)
            {
                if (doc.RootElement.TryGetProperty("allComposers", out var all) && all.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in all.EnumerateArray())
                    {
                        if (HeaderEntry(entry) is { } meta) result[meta.ComposerId] = meta;
                    }
                }
            }
        }
        _headers = (nowUtc, result);
        return result;
    }

    private IEnumerable<ComposerMeta> DiskKV(IReadOnlySet<string> ids)
    {
        if (!File.Exists(GlobalDatabase)) yield break;
        foreach (var id in ids)
        {
            var value = Query(GlobalDatabase, "SELECT 'k', value FROM cursorDiskKV WHERE key = $key", ("$key", $"composerData:{id}")).FirstOrDefault().Value;
            if (value is null) continue;
            using var doc = Parse(value);
            if (doc?.RootElement is not { ValueKind: JsonValueKind.Object } d) continue;
            var checkpoint = Int(d, "conversationCheckpointLastUpdatedAt");
            yield return new ComposerMeta(id, Str(d, "status"), Math.Max(Int(d, "lastUpdatedAt"), checkpoint), checkpoint, Int(d, "createdAt"), Str(d, "name"));
        }
    }

    private static IEnumerable<ComposerMeta> AllComposers(string database)
    {
        foreach (var (_, value) in Query(database,
                     "SELECT key, value FROM ItemTable WHERE key = 'composer.composerHeaders' OR key LIKE 'composerData:%' LIMIT 200"))
        {
            using var doc = Parse(value);
            if (doc is null) continue;
            var root = doc.RootElement;
            var entries = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList()
                : root.TryGetProperty("allComposers", out var all) && all.ValueKind == JsonValueKind.Array ? all.EnumerateArray().ToList()
                : root.ValueKind == JsonValueKind.Object ? [root] : [];
            foreach (var entry in entries)
            {
                if (ComposerEntry(entry) is { } meta) yield return meta;
            }
        }
    }

    private static ComposerMeta? HeaderEntry(JsonElement d)
    {
        if (Str(d, "composerId") is not { Length: > 0 } id) return null;
        var checkpoint = Int(d, "conversationCheckpointLastUpdatedAt");
        return new ComposerMeta(id, Str(d, "status"), Math.Max(Int(d, "lastUpdatedAt"), checkpoint), checkpoint, Int(d, "createdAt"), Str(d, "name"));
    }

    private static ComposerMeta? ComposerEntry(JsonElement d)
    {
        var id = Str(d, "composerId") ?? Str(d, "composerID") ?? Str(d, "id");
        if (string.IsNullOrEmpty(id)) return null;
        return new ComposerMeta(id, Str(d, "status") ?? Str(d, "composerStatus"),
            First(d, "lastUpdatedAt", "updatedAt", "updated_ms"), First(d, "checkpointMs", "checkpoint_ms"),
            First(d, "createdAt", "created_ms"), Str(d, "name"));
    }

    private static void Merge(Dictionary<string, ComposerMeta> merged, ComposerMeta meta)
    {
        if (!merged.TryGetValue(meta.ComposerId, out var existing) || meta.UpdatedMs >= existing.UpdatedMs) merged[meta.ComposerId] = meta;
    }

    // ---- SQLite ----

    private static string? ReadItem(string database, string key) =>
        Query(database, "SELECT key, value FROM ItemTable WHERE key = $key", ("$key", key)).FirstOrDefault().Value;

    /// <summary>Rows of (text, text). Any failure — missing, locked, corrupt — is an empty result.</summary>
    internal static IReadOnlyList<(string Key, string Value)> Query(string database, string sql, params (string Name, string Value)[] parameters)
    {
        var rows = new List<(string, string)>();
        if (!File.Exists(database)) return rows;
        try
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 1;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(1)) continue;
                var value = reader.GetValue(1) switch
                {
                    string s => s,
                    byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
                    var other => other?.ToString(),
                };
                if (value is not null) rows.Add((reader.IsDBNull(0) ? "" : reader.GetValue(0)?.ToString() ?? "", value));
            }
        }
        catch (Exception e) when (e is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
        }
        return rows;
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static JsonDocument? Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string key) => SessionLogParser.Str(e, key);

    private static long Int(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt64(out var n) => n,
                JsonValueKind.Number => (long)v.GetDouble(),
                JsonValueKind.String when long.TryParse(v.GetString(), out var s) => s,
                _ => 0,
            }
            : 0;

    private static long First(JsonElement e, params string[] keys) =>
        keys.Select(k => (Has: e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out _), Value: Int(e, k)))
            .FirstOrDefault(p => p.Has).Value;
}
