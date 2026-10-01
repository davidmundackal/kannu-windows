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

namespace Kannu.Core;

/// <summary>
/// Claude Desktop's local agent mode ("Cowork"), which has no hooks: one <c>audit.jsonl</c> per session
/// in the Claude Code SDK stream shape. Tail-of-file inference; never claims yellow, but can say
/// "throttled". Port of macOS <c>ClaudeDesktopAgentSessionStore</c>.
/// </summary>
public sealed class ClaudeDesktopAgentStore(string root)
{
    public const string ProviderKey = "claudedesktop";
    private const string SessionPrefix = "local_";
    private const string DispatchPrefix = "local_ditto_";
    private const int CacheCap = 48;

    private readonly Dictionary<string, (DateTime Mtime, long Size, Parsed Value)> _cache = [];

    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "local-agent-mode-sessions");

    /// <param name="RawState">executing, thinking, stopped, quota_exceeded, or idle with no conversational record yet.</param>
    public sealed record Parsed(string? Model, string? Title, string? Cwd, string RawState, long? RecordTimestampMs, int ToolErrorCount, RunError? RunError);

    /// <summary><paramref name="head"/> holds system:init; <paramref name="tail"/> the newest records.</summary>
    public static Parsed Parse(string? head, string? tail)
    {
        string? model = null, title = null, cwd = null;
        foreach (var line in (head ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = SessionLogParser.TryParse(line);
            if (doc?.RootElement is not { ValueKind: JsonValueKind.Object } json) continue;
            if (SessionLogParser.Str(json, "type") == "system" && SessionLogParser.Str(json, "subtype") == "init")
            {
                model = SessionLogParser.Str(json, "model");
                cwd = SessionLogParser.Str(json, "cwd");
            }
            if (title is null && SessionLogParser.Str(json, "title")?.Trim() is { Length: > 0 }) title = SessionLogParser.Str(json, "title");
        }

        var raw = "idle";
        long? timestamp = null;
        var errors = 0;
        RunError? runError = null;
        var decided = false;
        var counting = true;
        foreach (var line in Enumerable.Reverse((tail ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)))
        {
            using var doc = SessionLogParser.TryParse(line);
            if (doc?.RootElement is not { ValueKind: JsonValueKind.Object } json) continue;
            switch (SessionLogParser.Str(json, "type"))
            {
                case "rate_limit_event":
                    if (!decided && SessionLogParser.Obj(json, "rate_limit_info") is { } info && SessionLogParser.Str(info, "status") == "rejected")
                    {
                        (raw, timestamp, decided) = ("quota_exceeded", SessionLogParser.RecordTimestamp(json), true);
                    }
                    break;
                case "result":
                    var isError = json.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                    if (counting && isError) errors++;
                    if (!decided)
                    {
                        (raw, timestamp, decided) = ("stopped", SessionLogParser.RecordTimestamp(json), true);
                        // The newest result is the run's verdict; a user cancel is not a failure.
                        if (isError || (SessionLogParser.Str(json, "subtype") ?? "").StartsWith("error", StringComparison.Ordinal))
                        {
                            runError = json.TryGetProperty("api_error_status", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var code)
                                ? RunError.ApiError(code)
                                : RunError.Failed;
                        }
                    }
                    break;
                case "assistant":
                    if (!decided)
                    {
                        var message = SessionLogParser.Obj(json, "message");
                        var content = message is { } m && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array
                            ? c.EnumerateArray().ToList() : [];
                        if (content.Any(b => SessionLogParser.Str(b, "type") == "tool_use")) raw = "executing";
                        else
                        {
                            var stop = message is { } mm ? SessionLogParser.Str(mm, "stop_reason") : null;
                            raw = stop is null or "tool_use" or "pause_turn" ? "thinking" : "stopped";
                        }
                        (timestamp, decided) = (SessionLogParser.RecordTimestamp(json), true);
                    }
                    break;
                case "user":
                    var blocks = SessionLogParser.Obj(json, "message") is { } um && um.TryGetProperty("content", out var uc) && uc.ValueKind == JsonValueKind.Array
                        ? uc.EnumerateArray().Where(b => b.ValueKind == JsonValueKind.Object).ToList() : [];
                    var results = blocks.Where(b => SessionLogParser.Str(b, "type") == "tool_result").ToList();
                    if (counting)
                    {
                        errors += results.Count(r => r.TryGetProperty("is_error", out var re) && re.ValueKind == JsonValueKind.True);
                        // A plain prompt is the turn boundary for the error count.
                        if (results.Count == 0) counting = false;
                    }
                    if (!decided) (raw, timestamp, decided) = ("thinking", SessionLogParser.RecordTimestamp(json), true);
                    break;
                default:
                    continue;
            }
            if (decided && !counting) break;
        }
        return new Parsed(model, title, cwd, raw, timestamp, errors, runError);
    }

    /// <summary>(session id, is dispatch) for an audit.jsonl path, or null.</summary>
    public static (string Id, bool IsDispatch)? SessionIdentity(string auditLog)
    {
        var directory = Path.GetFileName(Path.GetDirectoryName(auditLog)) ?? "";
        if (directory.StartsWith(DispatchPrefix, StringComparison.Ordinal))
        {
            var id = directory[DispatchPrefix.Length..];
            return id.Length == 0 ? null : (id, true);
        }
        if (directory.StartsWith(SessionPrefix, StringComparison.Ordinal))
        {
            var id = directory[SessionPrefix.Length..];
            return id.Length == 0 ? null : (id, false);
        }
        return null;
    }

    public IReadOnlyList<AgentSession> Sessions(AgentTimings timings, long nowMs)
    {
        if (!Directory.Exists(root)) return [];
        var cutoff = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime.AddMinutes(-timings.StaleMinutes);
        List<(string Path, DateTime Mtime)> logs;
        try
        {
            logs = Directory.EnumerateFiles(root, "audit.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 })
                .Where(p => SessionIdentity(p) is not null)
                .Select(p => (p, File.GetLastWriteTimeUtc(p)))
                .Where(p => p.Item2 >= cutoff)
                .OrderByDescending(p => p.Item2)
                .Take(24)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var sessions = new List<AgentSession>();
        foreach (var (path, mtime) in logs)
        {
            var (id, dispatch) = SessionIdentity(path)!.Value;
            var parsed = CachedParse(path, mtime);
            // The file's age, not the record's: SDK records rarely carry timestamps.
            var ts = new DateTimeOffset(mtime).ToUnixTimeMilliseconds();
            var (state, visible) = AgentStateMachine.ResolveHookState(parsed.RawState, nowMs - ts, timings.CollapseMs, timings.InactiveMs);
            sessions.Add(new AgentSession
            {
                Id = $"{ProviderKey}-{id}",
                Provider = ProviderKey,
                ConversationId = id,
                ChatName = parsed.Title ?? (dispatch ? "Dispatch agent" : null),
                ProjectName = StatusPaths.ProjectName(parsed.Cwd),
                RawState = parsed.RawState,
                DisplayState = state,
                UpdatedAtMs = ts,
                IsVisible = visible,
                Cwd = parsed.Cwd,
                ToolErrorCount = parsed.ToolErrorCount,
                RunError = parsed.RunError,
            });
        }
        return sessions;
    }

    private Parsed CachedParse(string path, DateTime mtime)
    {
        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            size = -1;
        }
        if (_cache.TryGetValue(path, out var c) && c.Mtime == mtime && c.Size == size) return c.Value;
        var parsed = Parse(SessionLogParser.ReadLeading(path), SessionLogParser.ReadTrailing(path, 16_000));
        if (_cache.Count > CacheCap) _cache.Clear();
        _cache[path] = (mtime, size, parsed);
        return parsed;
    }
}
