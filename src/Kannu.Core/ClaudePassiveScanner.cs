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

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Kannu.Core;

/// <summary>What Kannu can learn about a process id.</summary>
/// <param name="Alive">A process with this id is running.</param>
/// <param name="StartMs">Its start time, when readable; null skips the identity check (counted alive).</param>
public readonly record struct ProcessProbe(bool Alive, long? StartMs)
{
    public static readonly ProcessProbe Gone = new(false, null);

    /// <summary>The real process table.</summary>
    public static ProcessProbe Of(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return Gone;
            try
            {
                return new ProcessProbe(true, new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds());
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Running but not ours to inspect: alive, identity unknown.
                return new ProcessProbe(true, null);
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return Gone;
        }
    }
}

/// <param name="DeadPidConversationIds">Conversations whose process is provably gone.</param>
/// <param name="LiveTails">Tail verdicts for conversations whose process is alive this scan.</param>
public sealed record ClaudePassiveResult(IReadOnlyList<AgentSession> Sessions, IReadOnlySet<string> DeadPidConversationIds,
    IReadOnlyDictionary<string, ClaudeTailState> LiveTails);

/// <summary>
/// Claude Code sessions seen without hooks: Claude writes <c>~/.claude/sessions/&lt;pid&gt;.json</c> for
/// each interactive process; Kannu checks the process and reads the transcript's tail. Port of macOS
/// <c>buildClaudeSessions</c>. Passive detection never claims yellow: it can see writes and liveness,
/// not whether Claude is asking anything. It only corroborates a hook's yellow (REGRESSIONS entry 12).
/// </summary>
public static class ClaudePassiveScanner
{
    public static ClaudePassiveResult Scan(SessionLogParser parser, AgentTimings timings, long nowMs, Func<int, ProcessProbe> probe)
    {
        var sessions = new List<AgentSession>();
        var dead = new HashSet<string>();
        var live = new HashSet<string>();
        var tails = new Dictionary<string, ClaudeTailState>();

        IEnumerable<string> files;
        try
        {
            files = Directory.Exists(parser.ClaudeSessionsDirectory)
                ? Directory.EnumerateFiles(parser.ClaudeSessionsDirectory, "*.json").ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            files = [];
        }

        foreach (var file in files)
        {
            if (ReadRecord(file) is not { } record) continue;
            var (pid, sessionId, startedAtMs, cwd, procStartMs) = record;

            var process = probe(pid);
            var alive = process.Alive && (process.StartMs is not { } start
                                          || AgentStateMachine.ProcessMatchesSessionRecord(start, startedAtMs, procStartMs));
            if (alive)
            {
                // No stale check for a live process: a session may run for many hours.
                live.Add(sessionId);
            }
            else
            {
                // Recorded before the stale skip: a long-lived session that was killed has an old
                // startedAt, and the reconciler still needs to know it is dead.
                dead.Add(sessionId);
                if (nowMs - startedAtMs > timings.StaleMs) continue;
            }

            var transcript = parser.ClaudeTranscript(sessionId);
            long? jsonlMtime = transcript is null ? null : Mtime(transcript);
            var tsMs = jsonlMtime ?? startedAtMs;

            string rawState;
            AgentLightState state;
            bool visible;
            var updatedAt = tsMs;
            RunError? runError = null;
            if (alive)
            {
                // The tail, not mtime, decides: a long tool writes its tool_use up front and then
                // nothing, while post-turn bookkeeping keeps bumping mtime.
                var tail = transcript is null ? ClaudeTailResult.Unknown : parser.ClaudeTailState(transcript);
                tails[sessionId] = tail.State;
                (rawState, state, visible, updatedAt) = AgentStateMachine.PassiveClaudeState(tail, jsonlMtime, tsMs, nowMs,
                    timings.CollapseMs, timings.InactiveMs);
                runError = tail.RunError;
            }
            else
            {
                rawState = "stopped";
                (state, visible) = AgentStateMachine.ResolveHookState(rawState, nowMs - tsMs, timings.CollapseMs, timings.InactiveMs);
            }

            sessions.Add(new AgentSession
            {
                Id = $"claude-{sessionId}",
                Provider = "claude",
                ConversationId = sessionId,
                ChatName = transcript is null ? null : parser.DisplayChatName(transcript, SessionLogProvider.Claude),
                ProjectName = StatusPaths.ProjectName(cwd),
                RawState = rawState,
                DisplayState = state,
                UpdatedAtMs = updatedAt,
                IsVisible = visible,
                Cwd = cwd,
                // Only while provably alive: a dead pid must not make the card clickable.
                HostPid = alive ? pid : null,
                RunError = runError,
            });
        }

        // A live process for a conversation always beats a stale orphan file for the same id: a
        // resumed session reuses its id under a new pid.
        dead.ExceptWith(live);
        return new ClaudePassiveResult(sessions, dead, tails);
    }

    private static long? Mtime(string path)
    {
        try
        {
            return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>An interactive session record, or null for anything else (untrusted input).</summary>
    internal static (int Pid, string SessionId, long StartedAtMs, string? Cwd, long? ProcStartMs)? ReadRecord(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 64 * 1024) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var json = doc.RootElement;
            if (json.ValueKind != JsonValueKind.Object) return null;
            if (!json.TryGetProperty("pid", out var pidValue) || pidValue.ValueKind != JsonValueKind.Number
                || !pidValue.TryGetInt32(out var pid) || pid <= 0) return null;
            if (SessionLogParser.Str(json, "sessionId") is not { } sessionId || !AgentStateMachine.IsHookConversationId(sessionId)) return null;
            if (!json.TryGetProperty("startedAt", out var started) || started.ValueKind != JsonValueKind.Number) return null;
            if (SessionLogParser.Str(json, "kind") != "interactive") return null;
            var startedAt = started.TryGetInt64(out var whole) ? whole : (long)started.GetDouble();
            return (pid, sessionId, startedAt, SessionLogParser.Str(json, "cwd"), ProcStartMs(SessionLogParser.Str(json, "procStart")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The record's own view of when its process started (<c>"Sat Sep 12 07:12:39 2026"</c>, UTC).
    /// Anything unparseable falls back to the window around startedAt, so a format change can never
    /// mark a chat dead.
    /// </summary>
    internal static long? ProcStartMs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var squeezed = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return DateTime.TryParseExact(squeezed, "ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? new DateTimeOffset(date, TimeSpan.Zero).ToUnixTimeMilliseconds()
            : null;
    }
}
