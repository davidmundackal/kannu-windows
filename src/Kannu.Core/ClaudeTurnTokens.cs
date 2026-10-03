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

namespace Kannu.Core;

/// <summary>A Claude request's tokens: which turn they belong to, and the totals.</summary>
public sealed record TurnTokens(long StartedMs, long StartOffset, long Input, long Output)
{
    /// <summary>812, 4.5k, 10k, 1.4M, 1.2B — rounding rolls over into the next unit.</summary>
    public static string Compact(long value)
    {
        if (value < 1000) return Math.Max(0, value).ToString(CultureInfo.InvariantCulture);
        (double Divisor, string Suffix)[] units = [(1e3, "k"), (1e6, "M"), (1e9, "B")];
        for (var i = 0; ; i++)
        {
            var scaled = value / units[i].Divisor;
            var rounded = scaled < 10 ? Math.Round(scaled * 10) / 10 : Math.Round(scaled);
            if (rounded >= 1000 && i < units.Length - 1) continue;
            var digits = rounded < 10 && rounded != Math.Floor(rounded)
                ? rounded.ToString("0.0", CultureInfo.InvariantCulture)
                : ((long)rounded).ToString(CultureInfo.InvariantCulture);
            return digits + units[i].Suffix;
        }
    }

    /// <summary>"1.4M in · 45k out".</summary>
    public string Text => $"{Compact(Input)} in · {Compact(Output)} out";
}

/// <summary>One transcript line's usage. "Input" is fresh input plus cache creation plus cache reads.</summary>
public sealed record UsageRecord(long TimestampMs, string Model, long InputTokens, long OutputTokens, string? DedupKey)
{
    /// <summary>Claude splits one message across records that repeat its usage; the dedup key counts it once.</summary>
    public static UsageRecord? Parse(JsonElement obj)
    {
        var message = SessionLogParser.Obj(obj, "message");
        var usage = message is { } m && SessionLogParser.Obj(m, "usage") is { } u ? u : SessionLogParser.Obj(obj, "usage");
        if (usage is not { } usageObj) return null;
        long Int(string key) => usageObj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
        var input = Int("input_tokens") + Int("cache_creation_input_tokens") + Int("cache_read_input_tokens");
        var output = Int("output_tokens");
        if (input + output <= 0) return null;
        var model = (message is { } mm ? SessionLogParser.Str(mm, "model") : null) ?? SessionLogParser.Str(obj, "model") ?? "unknown";
        var timestamp = SessionLogParser.RecordTimestamp(obj) ?? (message is { } mt ? SessionLogParser.RecordTimestamp(mt) : null);
        if (timestamp is null) return null;
        var messageId = message is { } mi ? SessionLogParser.Str(mi, "id") : null;
        var requestId = SessionLogParser.Str(obj, "requestId") ?? SessionLogParser.Str(obj, "request_id");
        var key = messageId is not null || requestId is not null ? $"{messageId}-{requestId}" : null;
        return new UsageRecord(timestamp.Value, model, input, output, key);
    }
}

/// <summary>What to follow for one chat card.</summary>
public sealed record TurnTokenRequest(string ConversationId, string MainPath, long StartOffset, long StartedMs)
{
    /// <summary>Records a little older than the turn still count; anything older is history copied in by a resume or fork.</summary>
    public const long ClockSlackMs = 60_000;

    public long WindowStartMs => StartedMs - ClockSlackMs;

    /// <summary><c>&lt;session&gt;.jsonl</c> keeps its subagents in <c>&lt;session&gt;\subagents\</c>.</summary>
    public string SubagentDirectory => Path.Combine(MainPath[..^".jsonl".Length], "subagents");

    /// <summary>Visible Claude cards whose turn names a followable transcript and a start offset.</summary>
    public static IReadOnlyList<TurnTokenRequest> From(IEnumerable<AgentSession> sessions, string home)
    {
        var seen = new HashSet<string>();
        var requests = new List<TurnTokenRequest>();
        foreach (var s in sessions)
        {
            if (!s.IsVisible || !s.Provider.Equals("claude", StringComparison.OrdinalIgnoreCase)) continue;
            if (s.Turn is not { TranscriptPath: { } path, TranscriptOffset: { } offset } turn) continue;
            if (TurnMetrics.TranscriptPath(path, home).Length == 0 || !seen.Add(s.ConversationId)) continue;
            requests.Add(new TurnTokenRequest(s.ConversationId, path, offset, turn.StartedMs));
        }
        return requests;
    }
}

/// <summary>
/// Reads one transcript forward from a byte offset and adds up the assistant records' usage inside the
/// time window. Pure over the bytes it is handed. Port of macOS <c>ClaudeTranscriptTokenAccumulator</c>.
/// </summary>
public sealed class TranscriptTokenAccumulator(long startOffset, long windowStartMs)
{
    public const int ChunkLimit = 4 << 20;

    /// <summary>A longer line is skipped; a split record repeats its message's usage, so a sibling still counts.</summary>
    public const int PartialLimit = 8 << 20;

    private const int SeenLimit = 1024;
    private static readonly byte[] AssistantMarker = Encoding.UTF8.GetBytes("\"type\":\"assistant\"");
    private static readonly byte[] UsageMarker = Encoding.UTF8.GetBytes("\"usage\":{");

    private readonly MemoryStream _partial = new();
    private readonly Queue<string> _seenOrder = new();
    private readonly HashSet<string> _seen = [];
    private bool _skipping;

    public long ReadOffset { get; private set; } = Math.Max(0, startOffset);
    public long? FileId { get; private set; }
    public long Input { get; private set; }
    public long Output { get; private set; }

    /// <summary>
    /// What to read next. A replaced file or one that shrank below what was read is read again from the
    /// start — never from an offset into the old file; the time window keeps older records out.
    /// </summary>
    public (long Offset, int Count)? Step(long size, long fileId)
    {
        if ((FileId is { } known && known != fileId) || size < ReadOffset) Restart();
        FileId = fileId;
        return ReadOffset < size ? (ReadOffset, (int)Math.Min(size - ReadOffset, ChunkLimit)) : null;
    }

    /// <summary>The bytes at <see cref="ReadOffset"/>. Complete lines are counted; the tail waits for the next chunk.</summary>
    public void Consume(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty) return;
        ReadOffset += chunk.Length;
        if (_skipping)
        {
            var nl = chunk.IndexOf((byte)'\n');
            if (nl < 0) return;
            chunk = chunk[(nl + 1)..];
            _skipping = false;
        }
        int newline;
        while ((newline = chunk.IndexOf((byte)'\n')) >= 0)
        {
            if (_partial.Length == 0)
            {
                Tally(chunk[..newline]);
            }
            else
            {
                _partial.Write(chunk[..newline]);
                Tally(_partial.ToArray());
                _partial.SetLength(0);
            }
            chunk = chunk[(newline + 1)..];
        }
        _partial.Write(chunk);
        if (_partial.Length > PartialLimit)
        {
            _partial.SetLength(0);
            _skipping = true;
        }
    }

    private void Tally(ReadOnlySpan<byte> line)
    {
        // A cheap hint first: most lines are tool results and user records. The parse decides.
        if (line.IndexOf(AssistantMarker) < 0 || line.IndexOf(UsageMarker) < 0) return;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions { MaxDepth = 256 });
        }
        catch (JsonException)
        {
            return;
        }
        using (doc)
        {
            var obj = doc.RootElement;
            if (SessionLogParser.Str(obj, "type") != "assistant" || UsageRecord.Parse(obj) is not { } record
                || record.TimestampMs < windowStartMs) return;
            if (record.DedupKey is { } key)
            {
                if (!_seen.Add(key)) return;
                _seenOrder.Enqueue(key);
                if (_seenOrder.Count > SeenLimit) _seen.Remove(_seenOrder.Dequeue());
            }
            Input += record.InputTokens;
            Output += record.OutputTokens;
        }
    }

    private void Restart()
    {
        ReadOffset = 0;
        Input = Output = 0;
        _partial.SetLength(0);
        _skipping = false;
        _seen.Clear();
        _seenOrder.Clear();
    }
}

/// <summary>
/// Follows the requested turns' transcripts across passes: each pass reads at most a budget of new
/// bytes and reports a chat's tokens only once all its files are caught up, so numbers never climb in
/// steps. Reads only inside <c>~/.claude/projects</c>, never through a link, and only token counts —
/// never content. Not thread-safe: one caller at a time. Port of macOS <c>ClaudeTurnTokenReader</c>.
/// </summary>
public sealed class TurnTokenReader(string projectsRoot)
{
    public const int SubagentFileLimit = 256;

    private readonly Dictionary<string, (TranscriptTokenAccumulator Accumulator, long LastUsedMs)> _entries = [];

    public int PassBudget { get; set; } = 16 << 20;
    public long IdleEvictionMs { get; set; } = 30_000;

    /// <param name="Behind">Some chat is not caught up yet; run another pass soon.</param>
    public sealed record Pass(IReadOnlyDictionary<string, TurnTokens> Tokens, bool Behind);

    public Pass Read(IReadOnlyList<TurnTokenRequest> requests, long nowMs)
    {
        var tokens = new Dictionary<string, TurnTokens>();
        var behind = false;
        var budget = PassBudget;
        var root = Path.GetFullPath(projectsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var request in requests)
        {
            var subagents = SubagentTranscripts(request.SubagentDirectory, request.WindowStartMs);
            if (subagents.Truncated) continue; // hide rather than undercount
            long input = 0, output = 0;
            var complete = true;
            var available = true;
            foreach (var (path, start) in subagents.Paths.Select(p => (p, 0L)).Prepend((request.MainPath, request.StartOffset)))
            {
                var key = $"{request.ConversationId}|{request.StartedMs}|{start}|{path}";
                var accumulator = _entries.TryGetValue(key, out var e) ? e.Accumulator : new TranscriptTokenAccumulator(start, request.WindowStartMs);
                switch (Follow(path, root, accumulator, ref budget))
                {
                    case Outcome.Behind:
                        complete = false;
                        break;
                    case Outcome.Unavailable:
                        available = false;
                        break;
                }
                _entries[key] = (accumulator, nowMs);
                input += accumulator.Input;
                output += accumulator.Output;
            }
            if (!complete) behind = true;
            if (complete && available) tokens[request.ConversationId] = new TurnTokens(request.StartedMs, request.StartOffset, input, output);
        }
        foreach (var stale in _entries.Where(e => nowMs - e.Value.LastUsedMs > IdleEvictionMs).Select(e => e.Key).ToList()) _entries.Remove(stale);
        return new Pass(tokens, behind);
    }

    private enum Outcome
    {
        CaughtUp,
        Behind,
        Unavailable,
    }

    private static Outcome Follow(string path, string root, TranscriptTokenAccumulator accumulator, ref int budget)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || !Path.GetFullPath(path).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return Outcome.Unavailable;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var size = stream.Length;
            // The file's identity: a replaced transcript gets a new creation time.
            var id = info.CreationTimeUtc.Ticks;
            while (accumulator.Step(size, id) is { } read)
            {
                if (budget <= 0) return Outcome.Behind;
                var length = Math.Min(read.Count, budget);
                var buffer = new byte[length];
                stream.Seek(read.Offset, SeekOrigin.Begin);
                var got = stream.ReadAtLeast(buffer, length, throwOnEndOfStream: false);
                if (got <= 0) return Outcome.Unavailable;
                accumulator.Consume(buffer.AsSpan(0, got));
                budget -= got;
            }
            return Outcome.CaughtUp;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Outcome.Unavailable;
        }
    }

    /// <summary>Subagent transcripts (<c>agent-*.jsonl</c>, at most three levels down) written since the turn began.</summary>
    internal static (IReadOnlyList<string> Paths, bool Truncated) SubagentTranscripts(string directory, long modifiedSinceMs)
    {
        if (!Directory.Exists(directory)) return ([], false);
        var since = DateTimeOffset.FromUnixTimeMilliseconds(modifiedSinceMs).UtcDateTime;
        var paths = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "agent-*.jsonl",
                         new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden }))
            {
                if (File.GetLastWriteTimeUtc(path) < since) continue;
                paths.Add(path);
                if (paths.Count > SubagentFileLimit) return ([], true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        paths.Sort(StringComparer.Ordinal);
        return (paths, false);
    }
}
