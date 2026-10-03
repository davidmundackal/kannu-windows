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

namespace Kannu.Core;

/// <summary>The user's timing settings for the light (macOS defaults: 30 min, 5 s, 5 s).</summary>
public sealed record AgentTimings(int StaleMinutes = 30, int StoppedCollapseSeconds = 5, int InactiveDisplaySeconds = 5)
{
    public long StaleMs => StaleMinutes * 60_000L;
    public long CollapseMs => StoppedCollapseSeconds * 1_000L;
    public long InactiveMs => InactiveDisplaySeconds * 1_000L;
}

/// <summary>One status file as read, with the modification time sampled before the read.</summary>
public sealed record HookFile(string Path, string Key, StatusRecord Record, DateTime? MtimeAtRead);

/// <param name="StaleFiles">Files past every rule that keeps them; the caller deletes each if unchanged.</param>
/// <param name="FoldedSubagentIds">Subagent conversations folded into their chat's card.</param>
public sealed record HookSessionResult(IReadOnlyList<AgentSession> Sessions, IReadOnlyList<HookFile> StaleFiles, HashSet<string> FoldedSubagentIds);

/// <summary>
/// Status files to sessions: the macOS monitor's <c>parseHookSessions</c>, minus its I/O. The stale
/// cap, the yellow hold and the subagent fold are decided here; the evidence they need (live Claude
/// tails, Cursor's pending approvals) is handed in by passive detection.
/// </summary>
public static class HookSessionReader
{
    public static HookSessionResult Build(
        IReadOnlyList<HookFile> files, AgentTimings timings, long nowMs, string home,
        IReadOnlyDictionary<string, ClaudeTailState>? liveClaudeTails = null,
        IReadOnlySet<string>? cursorPendingApprovalIds = null)
    {
        liveClaudeTails ??= new Dictionary<string, ClaudeTailState>();
        cursorPendingApprovalIds ??= new HashSet<string>();

        var parsed = files.Select(f =>
        {
            var record = f.Record;
            var ts = record.Ts > 0 ? record.Ts : f.MtimeAtRead is { } m ? new DateTimeOffset(m.ToUniversalTime()).ToUnixTimeMilliseconds() : 0;
            var provider = record.Provider.Length > 0 ? record.Provider : "unknown";
            var conversationId = f.Key.StartsWith(provider + "-", StringComparison.Ordinal) ? f.Key[(provider.Length + 1)..] : f.Key;
            return (File: f, Ts: ts, Provider: provider, ConversationId: conversationId, Turn: HookTurn.FromRecord(record, home, nowMs));
        }).ToList();

        // For the stale-cap rule: which chats a recently written subagent file names, and which chats
        // have an open turn (since when).
        var freshSubagentParents = new HashSet<string>();
        var openTurnStart = new Dictionary<string, long>();
        foreach (var p in parsed)
        {
            var providerKey = p.Provider.ToLowerInvariant();
            if (AgentStateMachine.IsHookConversationId(p.File.Record.ParentId) && nowMs - p.Ts <= timings.StaleMs)
            {
                freshSubagentParents.Add(providerKey + "|" + p.File.Record.ParentId);
            }
            if (p.Turn is { EndedMs: null } turn) openTurnStart[providerKey + "|" + p.ConversationId] = turn.StartedMs;
        }

        var sessions = new List<AgentSession>();
        var stale = new List<HookFile>();
        var parentByKey = new Dictionary<string, string>();
        foreach (var p in parsed)
        {
            var record = p.File.Record;
            var providerKey = p.Provider.ToLowerInvariant();
            var state = record.State;

            // A prompt nobody has answered keeps its yellow only while evidence says it is open
            // (REGRESSIONS entry 12). A Claude tail exists only for a process alive this scan.
            var hasTail = liveClaudeTails.TryGetValue(p.ConversationId, out var tail);
            ClaudeTailState? claudeTail = hasTail ? tail : null;
            var holdsYellow = AgentStateMachine.HoldsAwaitingInput(p.Provider, hasTail, claudeTail,
                cursorPendingApprovalIds.Contains(p.ConversationId));
            var awaitingOutlivesCap = AgentStateMachine.IsAwaitingInputRawState(state)
                                      && AgentStateMachine.AwaitingInputOutlivesStaleCap(p.Provider, hasTail, claudeTail);
            var subagentOfOpenTurn = record.ParentId is { } parent
                                     && openTurnStart.TryGetValue(providerKey + "|" + parent, out var parentStart)
                                     && p.Turn is { } own && own.StartedMs >= parentStart;
            var activeOutlivesCap = AgentStateMachine.HookFileOutlivesStaleCap(p.Provider, state, hasTail,
                freshSubagentParents.Contains(providerKey + "|" + p.ConversationId), subagentOfOpenTurn);

            if (nowMs - p.Ts > timings.StaleMs && !awaitingOutlivesCap && !activeOutlivesCap
                || AgentStateMachine.IsSimulationConversationId(p.ConversationId))
            {
                stale.Add(p.File);
                continue;
            }

            var (display, visible) = AgentStateMachine.ResolveHookState(state, nowMs - p.Ts, timings.CollapseMs,
                timings.InactiveMs, holdAwaitingInput: holdsYellow);

            sessions.Add(new AgentSession
            {
                Id = p.File.Key,
                Provider = p.Provider,
                ConversationId = p.ConversationId,
                ChatName = AgentStateMachine.LooksLikeToolName(record.Name) ? null : record.Name,
                ProjectName = record.Project,
                RawState = state,
                DisplayState = display,
                UpdatedAtMs = p.Ts,
                IsVisible = visible,
                Cwd = record.Cwd,
                HostPid = record.HostPid,
                HostName = record.HostName,
                HostWindow = record.HostWindow,
                ToolErrorCount = Math.Clamp(record.ToolErrors ?? 0, 0, 999),
                IsUnattended = record.Unattended == true,
                RunError = record.EndedOnError == true ? RunError.Failed : null,
                Turn = p.Turn,
            });
            if (record.ParentId is { } parentId && parentId != p.ConversationId && AgentStateMachine.IsHookConversationId(parentId))
            {
                parentByKey[providerKey + "|" + p.ConversationId] = parentId;
            }
        }

        var (folded, foldedIds) = SubagentFold.Fold(sessions, parentByKey);
        return new HookSessionResult(folded, stale, foldedIds);
    }

    /// <summary>Every readable status file in the directory, with mtimes sampled before each read.</summary>
    public static IReadOnlyList<HookFile> ReadFiles(string statusDirectory)
    {
        var result = new List<HookFile>();
        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(statusDirectory, "*.json").ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return result;
        }
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('.')) continue;
            // The stat must come before the read: sampled after, a replace landing in between pairs
            // the new file's mtime with the old contents, and the delete guard destroys a fresh write.
            DateTime? mtime;
            try
            {
                mtime = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (StatusStore.Read(path) is { } record && record.State.Length > 0)
            {
                result.Add(new HookFile(path, Path.GetFileNameWithoutExtension(name), record, mtime));
            }
        }
        return result;
    }

    /// <summary>
    /// Deletes a stale file only if it is still the one judged, and only under the hooks' lock, so a
    /// hook mid read-modify-write never has its file pulled away. Skipped (left for the next scan)
    /// when a hook holds the lock.
    /// </summary>
    public static void RemoveIfUnchanged(string statusDirectory, HookFile file)
    {
        using var lockHandle = StatusStore.TryLock(statusDirectory, TimeSpan.Zero);
        if (lockHandle is null) return;
        try
        {
            if (File.GetLastWriteTimeUtc(file.Path) == file.MtimeAtRead) File.Delete(file.Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
