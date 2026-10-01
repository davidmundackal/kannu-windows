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

namespace Kannu.Core;

/// <summary>What a Cursor agent transcript's tail says about its run.</summary>
public sealed record TranscriptAnalysis(
    long MtimeMs,
    bool IsDone,
    bool HasActiveToolUse,
    bool HasPendingToolApproval,
    bool IsUserPromptAwaitingResponse,
    bool IsTurnEndedAtTail);

/// <summary>
/// Cursor's agent transcripts (<c>~/.cursor/projects/&lt;slug&gt;/agent-transcripts/…jsonl</c>). Port of
/// macOS <c>CursorTranscriptParser</c>. Used for sessions without hooks, and to layer transcript
/// context onto Cursor hook sessions — never to override a hook's yellow.
/// </summary>
public sealed class CursorTranscripts(string home)
{
    private const int TailByteLimit = 48_000;
    private const int MaxTranscriptsPerScan = 24;
    private static readonly TimeSpan PathListCacheTtl = TimeSpan.FromSeconds(2);

    private (DateTime At, int MaxAge, IReadOnlyList<string> Paths)? _paths;
    private readonly Dictionary<string, (DateTime Mtime, long Size, string? Title)> _titleCache = [];
    private readonly Dictionary<string, (DateTime Mtime, long Size, IReadOnlyList<string> Snippets)> _snippetCache = [];

    public string ProjectsDirectory => Path.Combine(home, ".cursor", "projects");

    public void InvalidatePathCache() => _paths = null;

    public IReadOnlyList<string> RecentTranscriptPaths(int maxAgeMinutes, DateTime nowUtc)
    {
        if (_paths is { } cached && cached.MaxAge == maxAgeMinutes && nowUtc - cached.At < PathListCacheTtl) return cached.Paths;
        var found = new List<(string Path, DateTime Mtime)>();
        var cutoff = nowUtc.AddMinutes(-maxAgeMinutes);
        var sep = Path.DirectorySeparatorChar;
        if (Directory.Exists(ProjectsDirectory))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(ProjectsDirectory, "*.jsonl",
                             new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    var name = Path.GetFileName(path);
                    if (!path.Contains($"{sep}agent-transcripts{sep}") && !name.StartsWith("agent-", StringComparison.Ordinal)) continue;
                    var mtime = File.GetLastWriteTimeUtc(path);
                    if (mtime >= cutoff) found.Add((path, mtime));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        var paths = found.OrderByDescending(f => f.Mtime).Take(MaxTranscriptsPerScan).Select(f => f.Path).ToList();
        _paths = (nowUtc, maxAgeMinutes, paths);
        return paths;
    }

    public static string SessionId(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("agent-", StringComparison.Ordinal) ? name["agent-".Length..] : name;
    }

    /// <summary>A Task/subagent transcript: <c>…/agent-transcripts/&lt;parent&gt;/subagents/&lt;id&gt;.jsonl</c>.</summary>
    public static string? ParentSessionId(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        var sub = path.IndexOf($"{sep}subagents{sep}", StringComparison.Ordinal);
        if (sub < 0) return null;
        var before = path[..sub];
        var marker = $"{sep}agent-transcripts{sep}";
        var at = before.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;
        var parent = before[(at + marker.Length)..];
        return parent.Length == 0 ? null : parent;
    }

    public IReadOnlyDictionary<string, string> SubagentParents(int maxAgeMinutes, DateTime nowUtc)
    {
        var map = new Dictionary<string, string>();
        foreach (var path in RecentTranscriptPaths(maxAgeMinutes, nowUtc))
        {
            if (ParentSessionId(path) is { } parent) map[SessionId(path)] = parent;
        }
        return map;
    }

    public string? ProjectSlug(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        var marker = $"{sep}.cursor{sep}projects{sep}";
        var start = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var tail = path[(start + marker.Length)..];
        var end = tail.IndexOf($"{sep}agent-transcripts{sep}", StringComparison.Ordinal);
        return end <= 0 ? null : tail[..end];
    }

    /// <summary>
    /// A readable project name from Cursor's slug: the path with separators as dashes, so the part
    /// after the home folder's own slug, else the slug. Temp folders and numeric slugs have none.
    /// </summary>
    public string? DisplayProjectName(string? slug)
    {
        var trimmed = slug?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("var-", StringComparison.Ordinal) || trimmed.All(char.IsDigit)) return null;
        var homeSlug = home.Replace(":", "").Replace('\\', '-').Replace('/', '-').Trim('-');
        if (homeSlug.Length > 0 && trimmed.StartsWith(homeSlug + "-", StringComparison.OrdinalIgnoreCase))
        {
            var parts = trimmed[(homeSlug.Length + 1)..].Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) return parts[^1];
        }
        return trimmed;
    }

    public IReadOnlyDictionary<string, TranscriptAnalysis> AnalyzeRecent(int maxAgeMinutes, DateTime nowUtc) =>
        RecentTranscriptPaths(maxAgeMinutes, nowUtc).ToDictionary(SessionId, Analyze);

    public static TranscriptAnalysis Analyze(string path)
    {
        long mtime = 0;
        try
        {
            mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return AnalyzeEvents(mtime, ReadTailLines(path).Select(ParseEvent).OfType<TranscriptEvent>().ToList());
    }

    internal sealed record TranscriptEvent(string Kind, IReadOnlyList<string> ToolUses, string? StopReason);

    internal static TranscriptAnalysis AnalyzeEvents(long mtimeMs, IReadOnlyList<TranscriptEvent> events)
    {
        var isDone = DetectDone(events);
        var turnEnded = events.Count > 0 && events[^1].Kind == "turn_ended";
        var awaitingResponse = IsUserPromptAwaitingResponse(events);
        var pendingApproval = HasPendingApprovalGatedTool(events) && !isDone && !turnEnded;
        var pendingTool = events.LastOrDefault(e => e.Kind == "assistant" && e.ToolUses.Count > 0)?.ToolUses[^1];
        var activeTool = pendingTool is not null && !pendingApproval && !isDone && !awaitingResponse;
        return new TranscriptAnalysis(mtimeMs, isDone, activeTool, pendingApproval, awaitingResponse, turnEnded);
    }

    internal static TranscriptEvent? ParseEvent(string line)
    {
        using var doc = SessionLogParser.TryParse(line);
        if (doc?.RootElement is not { ValueKind: JsonValueKind.Object } json) return null;
        var kind = SessionLogParser.Str(json, "role") ?? SessionLogParser.Str(json, "type") ?? "unknown";
        var tools = new List<string>();
        string? stopReason = null;
        if (SessionLogParser.Obj(json, "message") is { } message)
        {
            stopReason = SessionLogParser.Str(message, "stop_reason");
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (SessionLogParser.Str(block, "type") == "tool_use" && SessionLogParser.Str(block, "name") is { } name) tools.Add(name);
                }
            }
        }
        return new TranscriptEvent(kind, tools, stopReason);
    }

    private static bool DetectDone(IReadOnlyList<TranscriptEvent> events)
    {
        var last = events.LastOrDefault(e => e.Kind is "assistant" or "user");
        if (last is not { Kind: "assistant" } || last.ToolUses.Count > 0) return false;
        return last.StopReason is null or "end_turn" or "stop" or "stop_sequence" or "max_tokens";
    }

    /// <summary>
    /// The latest tool-bearing assistant message is still an approval-gated proposal. A best-effort
    /// fallback for sessions without hooks; the transcript lags the live card.
    /// </summary>
    private static bool HasPendingApprovalGatedTool(IReadOnlyList<TranscriptEvent> events)
    {
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var e = events[i];
            // Walking back, either means the proposal was resolved.
            if (e.Kind is "turn_ended" or "user") return false;
            if (e.Kind != "assistant") break;
            if (e.ToolUses.Count == 0) continue;
            return e.ToolUses.Any(AgentStateMachine.RequiresUserApproval);
        }
        return false;
    }

    /// <summary>The user sent a prompt after turn_ended and the agent has not replied yet.</summary>
    private static bool IsUserPromptAwaitingResponse(IReadOnlyList<TranscriptEvent> events)
    {
        if (events.Count == 0 || events[^1].Kind != "user") return false;
        for (var i = events.Count - 2; i >= 0; i--)
        {
            if (events[i].Kind == "turn_ended") return true;
            if (events[i].Kind == "assistant") return false;
        }
        return false;
    }

    private static IReadOnlyList<string> ReadTailLines(string path)
    {
        var text = SessionLogParser.ReadTrailing(path, TailByteLimit);
        if (string.IsNullOrEmpty(text)) return [];
        try
        {
            // A cut-off first line is a fragment: drop it when the file is longer than the window.
            if (new FileInfo(path).Length > TailByteLimit && text.IndexOf('\n') is var nl and >= 0) text = text[(nl + 1)..];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    // ---- Names ----

    /// <summary>Last-resort title: the first user prompt. Composer headers and Glass titles come first.</summary>
    public string? DisplayChatName(string path)
    {
        if (!Stamp(path, out var mtime, out var size)) return null;
        if (_titleCache.TryGetValue(path, out var cached) && cached.Mtime == mtime && cached.Size == size) return cached.Title;
        string? title = null;
        foreach (var block in Blocks(path, "user"))
        {
            if (SessionLogParser.NormalizedChatTitle(block) is { } t)
            {
                title = t;
                break;
            }
        }
        if (_titleCache.Count > 2 * MaxTranscriptsPerScan) _titleCache.Clear();
        _titleCache[path] = (mtime, size, title);
        return title;
    }

    /// <summary>Assistant narration lines, used to reject interim titles that mirror in-chat prose.</summary>
    public IReadOnlyList<string> AssistantSnippets(string path)
    {
        if (!Stamp(path, out var mtime, out var size)) return [];
        if (_snippetCache.TryGetValue(path, out var cached) && cached.Mtime == mtime && cached.Size == size) return cached.Snippets;
        var snippets = Blocks(path, "assistant").Select(NormalizedSnippet).OfType<string>().ToList();
        if (_snippetCache.Count > 2 * MaxTranscriptsPerScan) _snippetCache.Clear();
        _snippetCache[path] = (mtime, size, snippets);
        return snippets;
    }

    private static IEnumerable<string> Blocks(string path, string role)
    {
        if (SessionLogParser.ReadLeading(path) is not { } text) yield break;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = SessionLogParser.TryParse(line);
            if (doc?.RootElement is not { ValueKind: JsonValueKind.Object } json || SessionLogParser.Str(json, "role") != role) continue;
            if (SessionLogParser.Obj(json, "message") is not { } message
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            var texts = content.EnumerateArray()
                .Where(b => SessionLogParser.Str(b, "type") == "text")
                .Select(b => SessionLogParser.Str(b, "text"))
                .OfType<string>()
                .ToList();
            foreach (var t in texts) yield return t;
        }
    }

    internal static string? NormalizedSnippet(string raw)
    {
        var candidate = raw.Replace("[REDACTED]", "").Trim();
        if (candidate.Length == 0) return null;
        var first = candidate.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? candidate;
        var collapsed = System.Text.RegularExpressions.Regex.Replace(first, @"\s+", " ");
        return collapsed.Length >= 4 ? (collapsed.Length > 72 ? collapsed[..72] : collapsed) : null;
    }

    private static bool Stamp(string path, out DateTime mtime, out long size)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                (mtime, size) = (info.LastWriteTimeUtc, info.Length);
                return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        (mtime, size) = (default, 0);
        return false;
    }
}
