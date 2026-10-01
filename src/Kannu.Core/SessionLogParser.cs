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
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kannu.Core;

public enum SessionLogProvider
{
    Claude,
    Codex,
}

/// <summary>
/// Reads Claude Code and Codex transcripts (<c>.jsonl</c>): the run state from a Claude tail, and
/// chat and project names. Port of macOS <c>AgentSessionLogParser</c>. Transcripts are large and read
/// on every rescan, so every read is bounded and every verdict is cached against (mtime, size).
/// </summary>
public sealed partial class SessionLogParser(string home)
{
    private const int LeadingByteLimit = 32_000;
    private const int MaxSessionsPerScan = 24;
    private static readonly TimeSpan PathListCacheTtl = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Escalating tail windows: one record can exceed the first, and a truncated tail must widen
    /// rather than report Unknown (macOS REGRESSIONS entry 4).
    /// </summary>
    private static readonly int[] TailWindowLimits = [16_000, 262_144, 1_048_576, 4_194_304];

    /// <summary>An Esc interrupt is a user record with this text; no Stop hook fires for it.</summary>
    private const string InterruptMarkerPrefix = "[Request interrupted by user";

    private readonly Dictionary<string, (DateTime Mtime, long Size, ClaudeTailResult Result)> _tailCache = [];
    private readonly Dictionary<string, (DateTime Mtime, long Size, string? Name)> _nameCache = [];
    private readonly Dictionary<string, (DateTime Mtime, long Size, string? Title)> _titleCache = [];
    private readonly Dictionary<string, (DateTime Mtime, long Size, string? Name)> _projectCache = [];
    private readonly Dictionary<string, string> _lastTailTitle = [];
    private readonly Dictionary<SessionLogProvider, (DateTime At, int MaxAge, IReadOnlyList<string> Paths)> _pathCache = [];
    private readonly Dictionary<string, string> _transcriptBySession = [];

    public string ClaudeProjectsDirectory => Path.Combine(home, ".claude", "projects");
    public string ClaudeSessionsDirectory => Path.Combine(home, ".claude", "sessions");
    public string CodexSessionsDirectory => Path.Combine(home, ".codex", "sessions");

    public void InvalidatePathCache() => _pathCache.Clear();

    /// <summary>The newest transcripts modified within <paramref name="maxAgeMinutes"/>, at most 24.</summary>
    public IReadOnlyList<string> RecentSessionPaths(SessionLogProvider provider, int maxAgeMinutes, DateTime nowUtc)
    {
        if (_pathCache.TryGetValue(provider, out var cached) && cached.MaxAge == maxAgeMinutes && nowUtc - cached.At < PathListCacheTtl)
        {
            return cached.Paths;
        }
        var root = provider == SessionLogProvider.Claude ? ClaudeProjectsDirectory : CodexSessionsDirectory;
        var cutoff = nowUtc.AddMinutes(-maxAgeMinutes);
        var found = new List<(string Path, DateTime Mtime)>();
        if (Directory.Exists(root))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    if (!IsSessionLogFile(path, provider)) continue;
                    var mtime = File.GetLastWriteTimeUtc(path);
                    if (mtime >= cutoff) found.Add((path, mtime));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        var paths = found.OrderByDescending(f => f.Mtime).Take(MaxSessionsPerScan).Select(f => f.Path).ToList();
        _pathCache[provider] = (nowUtc, maxAgeMinutes, paths);
        return paths;
    }

    public static string SessionId(string path, SessionLogProvider provider)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (provider == SessionLogProvider.Claude || !name.StartsWith("rollout-", StringComparison.Ordinal)) return name;
        var parts = name["rollout-".Length..].Split('-');
        return parts.Length >= 5 ? string.Join('-', parts[^5..]) : name["rollout-".Length..];
    }

    private static bool IsSessionLogFile(string path, SessionLogProvider provider)
    {
        var name = Path.GetFileName(path);
        if (provider == SessionLogProvider.Codex) return name.StartsWith("rollout-", StringComparison.Ordinal);
        var sep = Path.DirectorySeparatorChar;
        return !path.Contains($"{sep}subagents{sep}") && !name.StartsWith("agent-", StringComparison.Ordinal);
    }

    /// <summary>A Claude conversation's main transcript, found once and then remembered.</summary>
    public string? ClaudeTranscript(string sessionId)
    {
        if (_transcriptBySession.TryGetValue(sessionId, out var cached) && File.Exists(cached)) return cached;
        if (!Directory.Exists(ClaudeProjectsDirectory)) return null;
        try
        {
            var sep = Path.DirectorySeparatorChar;
            var match = Directory.EnumerateFiles(ClaudeProjectsDirectory, sessionId + ".jsonl",
                    new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .FirstOrDefault(p => !p.Contains($"{sep}subagents{sep}"));
            if (match is not null) _transcriptBySession[sessionId] = match;
            return match;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- Claude run state from the transcript tail ----

    /// <summary>
    /// What the newest conversational record of a Claude transcript says. Never reports "awaiting
    /// approval": a pending tool_use looks the same whether the tool runs or a card is open, and
    /// guessing there produced permanent false yellow. Cached against (mtime, size).
    /// </summary>
    public ClaudeTailResult ClaudeTailState(string path)
    {
        if (!Stat(path, out var mtime, out var size)) return ClaudeTailResult.Unknown;
        if (_tailCache.TryGetValue(path, out var cached) && cached.Mtime == mtime && cached.Size == size) return cached.Result;

        var result = ClaudeTailResult.Unknown;
        foreach (var limit in TailWindowLimits)
        {
            // Continue, not break: each window starts at a different offset, so one failed window
            // says nothing about the wider ones.
            if (ReadTrailing(path, limit) is not { } text) continue;
            result = ClaudeTailStateFromText(text);
            if (result.State != Core.ClaudeTailState.Unknown || limit >= size) break;
        }
        if (_tailCache.Count > 2 * MaxSessionsPerScan) _tailCache.Clear();
        _tailCache[path] = (mtime, size, result);
        return result;
    }

    /// <summary>Pure core of <see cref="ClaudeTailState(string)"/>: classifies the newest conversational record.</summary>
    public static ClaudeTailResult ClaudeTailStateFromText(string text)
    {
        // Whether the deciding record is also the newest line: a skipped line (bookkeeping, a torn
        // write) means mtime no longer vouches for it.
        var isNewestLine = true;
        foreach (var line in Enumerable.Reverse(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)))
        {
            var newest = isNewestLine;
            isNewestLine = false;
            using var doc = TryParse(line);
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) continue;
            var json = doc.RootElement;
            var type = Str(json, "type");
            switch (type)
            {
                case "assistant":
                {
                    var timestamp = RecordTimestamp(json);
                    // The turn died on the API: decided first, and only on a literal true.
                    if (json.TryGetProperty("isApiErrorMessage", out var apiError) && apiError.ValueKind == JsonValueKind.True)
                    {
                        int? status = json.TryGetProperty("apiErrorStatus", out var s) && s.ValueKind == JsonValueKind.Number
                                      && s.TryGetInt32(out var code) ? code : null;
                        return new ClaudeTailResult(Core.ClaudeTailState.TurnFinished, timestamp, RunError: RunError.ApiError(status));
                    }
                    var message = Obj(json, "message");
                    if (Blocks(message).Any(b => Str(b, "type") == "tool_use"))
                    {
                        return new ClaudeTailResult(Core.ClaudeTailState.ToolInFlight, timestamp);
                    }
                    // null / tool_use / pause_turn: the turn is still open. Anything else is terminal.
                    var stopReason = message is { } m ? Str(m, "stop_reason") : null;
                    if (stopReason is null or "tool_use" or "pause_turn")
                    {
                        return new ClaudeTailResult(Core.ClaudeTailState.Working, timestamp, newest);
                    }
                    return new ClaudeTailResult(Core.ClaudeTailState.TurnFinished, timestamp);
                }
                case "user":
                {
                    var timestamp = RecordTimestamp(json);
                    // Esc leaves the session idle at its prompt; without this the trailing user record
                    // reads as "owes a response" and the light stays green forever.
                    if (IsInterruptRecord(Obj(json, "message"))) return new ClaudeTailResult(Core.ClaudeTailState.TurnFinished, timestamp);
                    return new ClaudeTailResult(Core.ClaudeTailState.Working, timestamp, newest);
                }
                default:
                    // attachment, queue-operation, last-prompt, titles, system: bookkeeping.
                    continue;
            }
        }
        return ClaudeTailResult.Unknown;
    }

    /// <summary>The interrupt text as plain content, a text block, or inside a tool_result.</summary>
    internal static bool IsInterruptRecord(JsonElement? message)
    {
        if (message is not { } m) return false;
        if (m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String) return HasInterrupt(content.GetString());
        foreach (var block in Blocks(m))
        {
            switch (Str(block, "type"))
            {
                case "text" when HasInterrupt(Str(block, "text")):
                    return true;
                case "tool_result":
                    if (block.TryGetProperty("content", out var c))
                    {
                        if (c.ValueKind == JsonValueKind.String && HasInterrupt(c.GetString())) return true;
                        if (c.ValueKind == JsonValueKind.Array && c.EnumerateArray().Any(inner =>
                                inner.ValueKind == JsonValueKind.Object && Str(inner, "type") == "text" && HasInterrupt(Str(inner, "text"))))
                        {
                            return true;
                        }
                    }
                    break;
            }
        }
        return false;
    }

    private static bool HasInterrupt(string? text) => text?.Trim().StartsWith(InterruptMarkerPrefix, StringComparison.Ordinal) == true;

    public static long? RecordTimestamp(JsonElement json) =>
        Str(json, "timestamp") is { } raw && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var t) ? t.ToUnixTimeMilliseconds() : null;

    // ---- Names ----

    /// <summary>
    /// The chat's name: Claude's own title (custom-title over ai-title, newest copy found in the tail),
    /// else the first prompt. Remembered against (mtime, size), "no name yet" included.
    /// </summary>
    public string? DisplayChatName(string path, SessionLogProvider provider)
    {
        if (!Stat(path, out var mtime, out var size)) return null;
        if (_nameCache.TryGetValue(path, out var cached) && cached.Mtime == mtime && cached.Size == size) return cached.Name;
        if (ReadLeading(path) is not { } text) return null;

        string? name = null;
        if (provider == SessionLogProvider.Claude) name = CachedClaudeTitle(path, text, mtime, size);
        if (name is null)
        {
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var doc = TryParse(line);
                if (doc?.RootElement is not { ValueKind: JsonValueKind.Object } json) continue;
                if (UserPromptText(json, provider) is { } raw && NormalizedChatTitle(raw) is { } title)
                {
                    name = title;
                    break;
                }
            }
        }
        if (_nameCache.Count > 4 * MaxSessionsPerScan) _nameCache.Clear();
        _nameCache[path] = (mtime, size, name);
        return name;
    }

    private string? CachedClaudeTitle(string path, string leading, DateTime mtime, long size)
    {
        if (_titleCache.TryGetValue(path, out var cached) && cached.Mtime == mtime && cached.Size == size) return cached.Title;
        string? tailTitle = null;
        foreach (var limit in TailWindowLimits)
        {
            if (ReadTrailing(path, limit) is not { } tail) continue;
            if (ClaudeTitle(tail) is { } title)
            {
                tailTitle = title;
                break;
            }
            if (limit >= size) break;
        }
        if (tailTitle is not null)
        {
            if (_lastTailTitle.Count > 4 * MaxSessionsPerScan) _lastTailTitle.Clear();
            _lastTailTitle[path] = tailTitle;
        }
        var resolved = tailTitle ?? (_lastTailTitle.TryGetValue(path, out var last) ? last : null) ?? ClaudeTitle(leading);
        if (_titleCache.Count > 2 * MaxSessionsPerScan) _titleCache.Clear();
        _titleCache[path] = (mtime, size, resolved);
        return resolved;
    }

    /// <summary>The title Claude shows: the last custom-title, else the last ai-title.</summary>
    public static string? ClaudeTitle(string text)
    {
        string? custom = null, ai = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("-title", StringComparison.Ordinal)) continue;
            using var doc = TryParse(line);
            if (doc?.RootElement is not { ValueKind: JsonValueKind.Object } json) continue;
            switch (Str(json, "type"))
            {
                case "custom-title" when CleanTitle(Str(json, "customTitle")) is { } t:
                    custom = t;
                    break;
                case "ai-title" when CleanTitle(Str(json, "aiTitle")) is { } t:
                    ai = t;
                    break;
            }
        }
        return custom ?? ai;
    }

    private static string? CleanTitle(string? raw) => raw?.Trim() is { Length: > 0 } t ? Truncate(t, 72) : null;

    /// <summary>The project folder name from the transcript's first records, cached against (mtime, size).</summary>
    public string? ProjectName(string path, SessionLogProvider provider)
    {
        if (!Stat(path, out var mtime, out var size)) return null;
        if (_projectCache.TryGetValue(path, out var cached) && cached.Mtime == mtime && cached.Size == size) return cached.Name;
        var name = DisplayProjectName(path, provider);
        if (_projectCache.Count > 4 * MaxSessionsPerScan) _projectCache.Clear();
        _projectCache[path] = (mtime, size, name);
        return name;
    }

    /// <summary>The project folder name from the transcript's first records.</summary>
    public static string? DisplayProjectName(string path, SessionLogProvider provider)
    {
        if (ReadLeading(path) is not { } text) return null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = TryParse(line);
            if (doc?.RootElement is not { ValueKind: JsonValueKind.Object } json) continue;
            var cwd = provider == SessionLogProvider.Claude
                ? Str(json, "cwd")
                : (Obj(json, "payload") is { } p ? Str(p, "cwd") : null)
                  ?? (Obj(json, "session_meta") is { } meta && Obj(meta, "payload") is { } nested ? Str(nested, "cwd") : null);
            if (!string.IsNullOrEmpty(cwd)) return StatusPaths.ProjectName(cwd);
        }
        return null;
    }

    private static string? UserPromptText(JsonElement json, SessionLogProvider provider)
    {
        if (provider == SessionLogProvider.Claude)
        {
            if (Str(json, "type") != "user") return null;
            if (Obj(json, "message") is { } message)
            {
                if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                    && content.GetString() is { Length: > 0 } s) return s;
                foreach (var block in Blocks(message))
                {
                    if (Str(block, "type") == "text" && Str(block, "text") is { Length: > 0 } text) return text;
                }
            }
            return Str(json, "prompt") is { Length: > 0 } prompt ? prompt : null;
        }

        var type = Str(json, "type") ?? "";
        if (type == "user_message" && Obj(json, "payload") is { } p1 && Str(p1, "message") is { Length: > 0 } m1) return m1;
        if (type == "event_msg" && Obj(json, "payload") is { } p2 && Str(p2, "type") == "user_message"
            && Str(p2, "message") is { Length: > 0 } m2) return m2;
        if (Str(json, "role") == "user" && Obj(json, "message") is { } msg)
        {
            foreach (var block in Blocks(msg))
            {
                if (Str(block, "type") == "text" && Str(block, "text") is { Length: > 0 } text) return text;
            }
        }
        return null;
    }

    /// <summary>A prompt as a title: the user_query tag when present, first non-empty line, 4–72 characters.</summary>
    internal static string? NormalizedChatTitle(string raw)
    {
        var tagged = Tagged("user_query", raw) ?? Tagged("user_query", raw.Replace("&lt;", "<"));
        var candidate = TimestampTag().Replace(tagged ?? raw, "").Trim();
        if (candidate.Length == 0) return null;
        var firstLine = candidate.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? candidate;
        var collapsed = Whitespace().Replace(firstLine, " ");
        return collapsed.Length >= 4 ? Truncate(collapsed, 72) : null;
    }

    private static string? Tagged(string tag, string text)
    {
        var match = Regex.Match(text, $"<{tag}>(.*?)</{tag}>", RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string Truncate(string value, int max) =>
        new StringInfo(value).LengthInTextElements <= max ? value : new StringInfo(value).SubstringByTextElements(0, max);

    // ---- File reads ----

    /// <summary>The first 32 KB; a split multibyte character costs one replacement character, not the read.</summary>
    public static string? ReadLeading(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[Math.Min(LeadingByteLimit, Math.Max(0, stream.Length))];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The last <paramref name="limit"/> bytes (the whole file when shorter).</summary>
    public static string? ReadTrailing(string path, int limit)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var offset = Math.Max(0, length - limit);
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[length - offset];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Stat(string path, out DateTime mtime, out long size)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                mtime = info.LastWriteTimeUtc;
                size = info.Length;
                return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        mtime = default;
        size = 0;
        return false;
    }

    // ---- JSON helpers ----

    internal static JsonDocument? TryParse(string line)
    {
        try
        {
            return JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 128 });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static JsonElement? Obj(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static IEnumerable<JsonElement> Blocks(JsonElement? message) =>
        message is { } m && m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray().Where(b => b.ValueKind == JsonValueKind.Object)
            : [];

    [GeneratedRegex("<timestamp>.*?</timestamp>", RegexOptions.Singleline)]
    private static partial Regex TimestampTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
