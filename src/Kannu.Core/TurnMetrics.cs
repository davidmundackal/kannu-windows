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

using System.Text.RegularExpressions;

namespace Kannu.Core;

/// <summary>One request's run: when it started and ended, and the tools it called.</summary>
public sealed class Turn
{
    public long StartedMs { get; set; }
    public long? EndedMs { get; set; }
    public int ToolCalls { get; set; }
    public List<string> ToolIds { get; set; } = [];
    public long? TranscriptOffset { get; set; }
}

/// <summary>
/// Turn metrics, ported from the macOS hook (v39). A turn is one request: it starts when a prompt
/// reaches an agent that is not already working and ends at the Stop that answers it. Work after that
/// Stop without a new prompt — a background task finishing, a stop hook sending the agent back —
/// reopens the same turn, and a prompt that arrives while the request is still running joins it, so
/// the time still counts from the first prompt (docs/REGRESSIONS.md entry 15 on macOS). Never
/// touches state or ts.
/// </summary>
public static partial class TurnMetrics
{
    public static readonly HashSet<string> PromptEvents = ["UserPromptSubmit", "beforeSubmitPrompt", "BeforeAgent"];

    public static readonly HashSet<string> WakeEvents =
        ["PreToolUse", "preToolUse", "beforeShellExecution", "beforeMCPExecution", "BeforeTool", "PreInvocation", "PermissionRequest"];

    public static readonly HashSet<string> EndEvents = ["Stop", "StopFailure", "stop", "AfterAgent"];

    /// <summary>A tool finished: counted once per call.</summary>
    public static readonly HashSet<string> DoneEvents = ["PostToolUse", "postToolUse", "PostToolUseFailure", "postToolUseFailure", "AfterTool"];

    private const int MaxIds = 16;
    private const int MaxCalls = 99_999;
    private const long MaxOffset = 9_007_199_254_740_992;
    private const int PathMax = 1024;
    private const long NoIdWindowMs = 2_000;

    /// <summary>Earliest timestamp a real turn can carry (2001); anything smaller is junk.</summary>
    internal const long PlausibleMs = 1_000_000_000_000;

    /// <summary>Everything the turn rules read about this hook call.</summary>
    internal sealed record Input(
        string Provider,
        string ParentId,
        string HookEvent,
        string PayloadTranscriptPath,
        bool StopHookActive,
        string ToolUseId,
        string Tool,
        int RawLength,
        long NowMs,
        string Home,
        Func<long> ParentTurnStart,
        Func<string, long?> FileSize);

    /// <summary>The turn keys a status file carries (untrusted), re-checked; null without a plausible start.</summary>
    public static Turn? Carried(StatusRecord? record, long nowMs)
    {
        if (record?.TurnStartedMs is not { } start || start < PlausibleMs || start > nowMs + 60_000) return null;
        var turn = new Turn
        {
            StartedMs = start,
            ToolCalls = Math.Min(record.TurnToolCalls ?? 0, MaxCalls),
            ToolIds = (record.TurnToolIds ?? []).TakeLast(MaxIds).Select(id => Token(id)).Where(t => t.Length > 0).ToList(),
        };
        if (record.TurnEndedMs is { } end && start <= end && end <= nowMs + 60_000) turn.EndedMs = end;
        if (record.TurnTranscriptOffset is { } offset && offset <= MaxOffset) turn.TranscriptOffset = offset;
        return turn;
    }

    /// <returns>The turn to write (null for none) and the transcript path to carry ("" for none).</returns>
    internal static (Turn? Turn, string Transcript) Next(StatusRecord? existing, Input input)
    {
        var turn = Carried(existing, input.NowMs);
        var previous = existing?.HookEvent ?? "";
        var mainThread = input.Provider == "claude" && input.ParentId.Length == 0;
        var transcript = mainThread ? TranscriptPath(existing?.TranscriptPath, input.Home) : "";
        var fresh = mainThread ? TranscriptPath(input.PayloadTranscriptPath, input.Home) : "";
        if (fresh.Length > 0 && fresh != transcript)
        {
            // First seen, or the chat moved to another file mid-turn: no offset rather than a wrong one.
            if (turn is not null) turn.TranscriptOffset = null;
            transcript = fresh;
        }

        var hookEvent = input.HookEvent;
        if (hookEvent == "SessionStart")
        {
            // A startup or /clear. opencode spawns SessionStart and the first prompt without waiting,
            // so a prompt that won the lock keeps its open turn.
            if (turn is not null && (turn.EndedMs is not null || !PromptEvents.Contains(previous))) turn = null;
            return (turn, transcript);
        }

        // A prompt while the request is still running joins it; only a prompt to an agent that has
        // stopped starts one.
        var openTurn = turn is not null && turn.EndedMs is null;
        var starts = (PromptEvents.Contains(hookEvent) && !openTurn) || (WakeEvents.Contains(hookEvent) && turn is null);
        if (input.ParentId.Length > 0 && turn is not null && WakeEvents.Contains(hookEvent)
            && input.ParentTurnStart() > turn.StartedMs)
        {
            // A subagent resumed in a later request of its chat.
            starts = true;
        }

        if (starts)
        {
            turn = new Turn { StartedMs = input.NowMs };
            if (transcript.Length > 0)
            {
                var size = input.FileSize(transcript);
                if (size is null)
                {
                    // Not written yet: the whole file is this turn, but only for a chat that has just
                    // started. Any other chat may be about to copy an earlier history into it.
                    if (previous == "SessionStart") turn.TranscriptOffset = 0;
                }
                else if (size >= 0)
                {
                    turn.TranscriptOffset = Math.Min(size.Value, MaxOffset);
                }
            }
        }
        else if (turn is not null && WakeEvents.Contains(hookEvent))
        {
            // Working again after the Stop without a new prompt: still the same request.
            turn.EndedMs = null;
        }

        if (turn is not null && DoneEvents.Contains(hookEvent))
        {
            var (key, seen) = CallKey(turn, input);
            if (!seen)
            {
                turn.ToolCalls = Math.Min(turn.ToolCalls + 1, MaxCalls);
                turn.ToolIds = turn.ToolIds.Append(key).TakeLast(MaxIds).ToList();
            }
        }

        if (turn is not null && EndEvents.Contains(hookEvent) && (turn.EndedMs is null || input.StopHookActive))
        {
            // The first Stop ends the turn; a Stop after a stop hook's continuation moves the end.
            turn.EndedMs = input.NowMs;
        }
        return (turn, transcript);
    }

    /// <summary>
    /// (key, already counted). The tool call's id when the agent sends one; otherwise the tool and the
    /// payload size, so one completion delivered twice within 2 s counts once.
    /// </summary>
    private static (string Key, bool Seen) CallKey(Turn turn, Input input)
    {
        var call = Token(input.ToolUseId);
        if (call.Length > 0) return (call, turn.ToolIds.Contains(call));

        var tool = Token(input.Tool);
        var stem = "nx:" + (tool.Length > 24 ? tool[..24] : tool) + ":" + input.RawLength + ":";
        foreach (var token in turn.ToolIds)
        {
            if (!token.StartsWith(stem, StringComparison.Ordinal)) continue;
            var tail = token[stem.Length..];
            if (tail.Length > 0 && tail.All(char.IsAsciiDigit) && long.TryParse(tail, out var ms)
                && input.NowMs - ms <= NoIdWindowMs)
            {
                return ("", true);
            }
        }
        return (stem + input.NowMs, false);
    }

    /// <summary>
    /// Claude's main transcript only: under <c>~/.claude/projects</c>, a <c>.jsonl</c>, never a
    /// subagent's, printable ASCII and already normalised. Anything else is "".
    /// </summary>
    internal static string TranscriptPath(string? value, string home)
    {
        if (string.IsNullOrEmpty(value) || value.Length > PathMax) return "";
        if (value.Any(c => c < 32 || c > 126)) return "";
        string full;
        try
        {
            full = Path.GetFullPath(value);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
        if (full != value) return "";

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.Combine(home, ".claude", "projects") + Path.DirectorySeparatorChar;
        if (!value.StartsWith(root, comparison) || !value.EndsWith(".jsonl", comparison)) return "";
        var sep = Path.DirectorySeparatorChar;
        return value.Contains($"{sep}subagents{sep}", comparison) ? "" : value;
    }

    /// <summary>
    /// The transcript's size without following a link: null while it does not exist, -1 when it is
    /// not a plain file or cannot be read (then no offset is recorded).
    /// </summary>
    public static long? FileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Directory.Exists(path) ? -1 : null;
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return -1;
            return info.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return -1;
        }
    }

    /// <summary>The macOS hook's <c>ht_token</c>: <c>[A-Za-z0-9_.:-]</c>, at most 64 characters.</summary>
    internal static string Token(string? value, int limit = 64)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var clean = UnsafeToken().Replace(value.Length > limit * 4 ? value[..(limit * 4)] : value, "");
        return clean.Length > limit ? clean[..limit] : clean;
    }

    [GeneratedRegex("[^A-Za-z0-9_.:-]")]
    private static partial Regex UnsafeToken();
}
