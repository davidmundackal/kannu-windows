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

/// <summary>A live Claude process's transcript tail, as passive detection reads it.</summary>
public enum ClaudeTailState
{
    /// <summary>A tool_use with no result yet: a tool is running (or a prompt for it is open).</summary>
    ToolInFlight,

    /// <summary>A response is owed: the newest conversational record is the user's or a tool result.</summary>
    Working,

    /// <summary>The assistant's turn ended.</summary>
    TurnFinished,

    /// <summary>The tail could not be parsed.</summary>
    Unknown,
}

/// <param name="RecordTimestampMs">The deciding record's own time, when it has one.</param>
/// <param name="NewestRecordIsConversational">False when the newest record is a draft, title or attachment trailer.</param>
/// <param name="RunError">Set only with TurnFinished, when the record that ended the turn is an API error.</param>
public sealed record ClaudeTailResult(ClaudeTailState State, long? RecordTimestampMs, bool NewestRecordIsConversational = true, RunError? RunError = null)
{
    public static readonly ClaudeTailResult Unknown = new(ClaudeTailState.Unknown, null);
}

/// <summary>
/// The traffic-light rules, ported from macOS <c>AgentTrafficLightMapper</c>. Every rule here has
/// broken at least once on macOS; the docs/REGRESSIONS.md entry is named where it applies. Change a
/// clock only after reading it — "fix the state machine, not the clock".
/// </summary>
public static class AgentStateMachine
{
    /// <summary>
    /// Green with no event for this long is demoted. Must outlast the longest tool call: hook-only
    /// providers write nothing during one (REGRESSIONS entry 2 — never shorten it).
    /// </summary>
    public const long ActiveStaleMs = 360_000;

    /// <summary>Yellow nothing can corroborate gives up after this long (REGRESSIONS entry 12).</summary>
    public const long AwaitingInputStaleMs = 300_000;

    /// <summary>A passive "working" verdict stays green only this long past its last evidence.</summary>
    public const long PassiveWorkingStaleMs = 600_000;

    /// <summary>How long a chat that went red and then ended stays listed as a dim card.</summary>
    public const long EndedChatRetentionMs = 69_000;

    /// <summary>An unbacked Cursor hook file is dropped only once it is older than this.</summary>
    public const long UnbackedCursorHookGraceMs = 10 * 60 * 1000;

    private static readonly HashSet<string> HostProviders = ["cursor", "antigravity", "vscode", "copilot"];
    private static readonly HashSet<string> EmbeddedEngineProviders = ["claude", "codex"];

    /// <summary>A hook file's raw state and age to what its card shows.</summary>
    public static (AgentLightState State, bool Visible) ResolveHookState(
        string rawState, long ageMs, long collapseMs, long inactiveMs,
        long activeStaleMs = ActiveStaleMs, bool holdAwaitingInput = false)
    {
        switch (rawState.ToLowerInvariant())
        {
            case "executing" when ageMs <= activeStaleMs:
                return (AgentLightState.Executing, true);
            case "awaiting_input" or "awaitinginput" or "awaiting":
                // Held: live evidence says the prompt is still open, so the clock does not apply.
                return holdAwaitingInput || ageMs <= AwaitingInputStaleMs
                    ? (AgentLightState.AwaitingInput, true)
                    : (AgentLightState.Inactive, false);
            case "thinking" when ageMs <= activeStaleMs:
                return (AgentLightState.Thinking, true);
            case "idle":
                // Session opened but nothing running yet: a dim card, no lit light.
                return (AgentLightState.Inactive, true);
            case "session_end" or "ended":
                return (AgentLightState.Inactive, false);
            case "stopped" or "stop" or "completed" or "aborted" or "error":
                return ageMs <= collapseMs + inactiveMs ? (AgentLightState.Stopped, true) : (AgentLightState.Inactive, false);
        }
        if (ageMs <= collapseMs) return (AgentLightState.Stopped, true);
        if (ageMs <= collapseMs + inactiveMs) return (AgentLightState.Inactive, true);
        return (AgentLightState.Inactive, false);
    }

    public static bool IsAwaitingInputRawState(string rawState) =>
        rawState.ToLowerInvariant() is "awaiting_input" or "awaitinginput" or "awaiting";

    /// <summary>
    /// May this awaiting_input hook file outlive the stale cap? Claude only — the one provider whose
    /// liveness Kannu can see: a live process whose tail still shows the tool_use with no result.
    /// </summary>
    public static bool AwaitingInputOutlivesStaleCap(string provider, bool processAlive, ClaudeTailState? tail) =>
        provider.ToLowerInvariant() == "claude" && processAlive && tail == ClaudeTailState.ToolInFlight;

    /// <summary>
    /// Does the process holding a session record's pid still belong to that session? The record is
    /// written a while after the CLI starts, so the record's own procStart decides when it has one
    /// (±5 s); otherwise the process may start up to ten minutes before the record and at most five
    /// seconds after it. Never tighten this (REGRESSIONS entry 3, 2026-09-12 addendum).
    /// </summary>
    public static bool ProcessMatchesSessionRecord(long processStartMs, long recordStartedAtMs, long? recordProcStartMs = null)
    {
        if (recordProcStartMs is { } procStart) return Math.Abs(processStartMs - procStart) < 5_000;
        return processStartMs <= recordStartedAtMs + 5_000 && recordStartedAtMs - processStartMs <= 600_000;
    }

    /// <summary>
    /// A silent Claude hook file past the stale cap is kept while it reports work and something proves
    /// the work is real: the process is alive, a fresh subagent file names it, or it is a subagent of
    /// an open turn. A stopped file is never kept.
    /// </summary>
    public static bool HookFileOutlivesStaleCap(string provider, string rawState, bool processAlive,
        bool namedByFreshSubagent, bool subagentOfOpenTurn)
    {
        if (provider.ToLowerInvariant() != "claude") return false;
        return rawState.ToLowerInvariant() is "executing" or "thinking"
               && (processAlive || namedByFreshSubagent || subagentOfOpenTurn);
    }

    /// <summary>
    /// Does this hook's awaiting_input keep its yellow past the 5-minute window? Yellow originates from
    /// hooks only; evidence corroborates, never claims (REGRESSIONS entry 12). Hook-only providers hold
    /// because nothing can refute them; a newer event or the stale cap ends theirs.
    /// </summary>
    public static bool HoldsAwaitingInput(string provider, bool processAlive, ClaudeTailState? tail, bool cursorPendingApproval) =>
        provider.ToLowerInvariant() switch
        {
            "claude" => AwaitingInputOutlivesStaleCap(provider, processAlive, tail),
            "cursor" => cursorPendingApproval,
            "vscode" or "codex" or "antigravity" or "copilot" or "gemini" or "qwen" or "opencode" => true,
            _ => false,
        };

    /// <summary>
    /// A live Claude process's transcript tail to a display state. The tail is consulted before any
    /// mtime shortcut (post-turn bookkeeping writes must not repaint green), and a live process whose
    /// tail cannot be read is working, never idle (REGRESSIONS entry 3).
    /// </summary>
    public static (string RawState, AgentLightState State, bool Visible, long UpdatedAtMs) PassiveClaudeState(
        ClaudeTailResult tail, long? jsonlMtimeMs, long fallbackTsMs, long nowMs, long collapseMs, long inactiveMs,
        long workingStaleMs = PassiveWorkingStaleMs)
    {
        switch (tail.State)
        {
            case ClaudeTailState.ToolInFlight:
                // A tool may run for many minutes with zero writes: never age out.
                return ("executing", AgentLightState.Executing, true, fallbackTsMs);

            case ClaudeTailState.Working:
                // Newer file writes count as life signs only while the newest record is conversational:
                // a saved draft or title bumps mtime without the agent doing anything.
                long? evidence = tail.NewestRecordIsConversational
                    ? Max(tail.RecordTimestampMs, jsonlMtimeMs)
                    : tail.RecordTimestampMs;
                if (evidence is { } e && nowMs - e > workingStaleMs) return ("idle", AgentLightState.Inactive, true, fallbackTsMs);
                return ("thinking", AgentLightState.Thinking, true, fallbackTsMs);

            case ClaudeTailState.TurnFinished:
                var stopMs = tail.RecordTimestampMs ?? fallbackTsMs;
                var (state, visible) = ResolveHookState("stopped", nowMs - stopMs, collapseMs, inactiveMs);
                // The process is still running, so the session stays listed as a dim card.
                return visible ? ("stopped", state, true, stopMs) : ("stopped", AgentLightState.Inactive, true, stopMs);

            default:
                return ("thinking", AgentLightState.Thinking, true, fallbackTsMs);
        }
    }

    private static long? Max(long? a, long? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    /// <summary>
    /// Merges Claude hook sessions with passive transcript/process evidence. A hook file shadows the
    /// passive session for the same conversation, and everything the passive side knows that the hook
    /// does not is carried across on every exit (REGRESSIONS entries 5 and 7).
    /// </summary>
    public static IReadOnlyList<AgentSession> ReconcileClaudeSessions(
        IReadOnlyList<AgentSession> hookSessions, IReadOnlyList<AgentSession> passiveSessions,
        IReadOnlySet<string> deadPidConversationIds, long collapseMs, long inactiveMs, long nowMs)
    {
        if (passiveSessions.Count == 0 && deadPidConversationIds.Count == 0) return hookSessions;

        var passiveById = new Dictionary<string, AgentSession>();
        foreach (var p in passiveSessions) passiveById.TryAdd(p.ConversationId, p);

        var merged = hookSessions.Select(session =>
        {
            if (session.Provider.ToLowerInvariant() != "claude") return session;
            passiveById.TryGetValue(session.ConversationId, out var passive);
            var processDead = deadPidConversationIds.Contains(session.ConversationId);

            AgentSession Inherit(AgentSession candidate)
            {
                if (passive is null) return candidate;
                var repaired = candidate;
                if (string.IsNullOrEmpty(repaired.ChatName) && passive.ChatName is { } name) repaired = repaired with { ChatName = name };
                if (string.IsNullOrEmpty(repaired.ProjectName) && passive.ProjectName is { } project) repaired = repaired with { ProjectName = project };
                if (string.IsNullOrEmpty(repaired.Cwd) && passive.Cwd is { } cwd) repaired = repaired with { Cwd = cwd };
                // Safe: the passive side sets HostPid only while the process is provably alive.
                if (repaired.HostPid is null && passive.HostPid is { } pid) repaired = repaired with { HostPid = pid };
                return repaired.CarryingExtras(passive);
            }

            // Demote: the hook still claims work, but fresher passive evidence or a dead process says
            // otherwise (Stop never fires on an interrupt, and a kill skips it).
            if (session.DisplayState.IsActiveRun())
            {
                if (passive is not null && !passive.DisplayState.IsActiveRun()
                    && (processDead || passive.UpdatedAtMs >= session.UpdatedAtMs))
                {
                    return Inherit(session.WithDisplayState(passive.DisplayState, passive.IsVisible));
                }
                if (passive is null && processDead)
                {
                    var (state, visible) = ResolveHookState("stopped", nowMs - session.UpdatedAtMs, collapseMs, inactiveMs);
                    return Inherit(session.WithDisplayState(state, visible));
                }
                return Inherit(session);
            }

            // Promote: hooks fire only at tool boundaries, so a long tool leaves the file silent and
            // the ladder dims the session while it is hardest at work. A live process beats a stale ts.
            if (!session.HasActiveRawState || passive is null || !passive.DisplayState.IsActiveRun())
            {
                // Invisible must not hide visible: a kept-alive aged file must not shadow a finished
                // passive card. An aged yellow is still not repainted by a passive active verdict.
                if (passive is { IsVisible: true } && !passive.DisplayState.IsActiveRun() && !session.IsVisible)
                {
                    return Inherit(session.WithDisplayState(passive.DisplayState, true));
                }
                return Inherit(session);
            }
            return Inherit(session.WithDisplayState(passive.DisplayState, true, Math.Max(session.UpdatedAtMs, passive.UpdatedAtMs)));
        }).ToList();

        var hookIds = merged.Select(s => s.ConversationId).ToHashSet();
        merged.AddRange(passiveSessions.Where(p => !hookIds.Contains(p.ConversationId)));
        return merged;
    }

    /// <summary>The most urgent visible state across all sessions drives the light.</summary>
    public static AgentLightState ResolveDisplayState(IEnumerable<AgentSession> sessions) =>
        sessions.Where(s => s.IsVisible && !IsSimulation(s))
            .Select(s => s.DisplayState)
            .DefaultIfEmpty(AgentLightState.Inactive)
            .MaxBy(s => s.SortPriority());

    /// <summary>One card per conversation: the higher state wins, then a real title, then the newer.</summary>
    public static IReadOnlyList<AgentSession> LatestSessions(IEnumerable<AgentSession> sessions)
    {
        var byId = new Dictionary<string, AgentSession>();
        var order = new List<string>();
        foreach (var session in sessions)
        {
            if (byId.TryGetValue(session.ConversationId, out var existing))
            {
                byId[session.ConversationId] = PreferredSession(existing, session);
            }
            else
            {
                byId[session.ConversationId] = session;
                order.Add(session.ConversationId);
            }
        }
        return order.Select(id => byId[id]).ToList();
    }

    public static AgentSession PreferredSession(AgentSession existing, AgentSession incoming)
    {
        AgentSession winner;
        if (existing.DisplayState != incoming.DisplayState)
        {
            winner = existing.DisplayState.SortPriority() > incoming.DisplayState.SortPriority() ? existing : incoming;
        }
        else if (HasReliableChatName(existing.ChatName) != HasReliableChatName(incoming.ChatName))
        {
            winner = HasReliableChatName(incoming.ChatName) ? incoming : existing;
        }
        else
        {
            winner = incoming.UpdatedAtMs >= existing.UpdatedAtMs ? incoming : existing;
        }
        return HostIdentitySession(existing, incoming) is { } identity
               && !string.Equals(identity.Provider, winner.Provider, StringComparison.OrdinalIgnoreCase)
            ? winner.AdoptingIdentity(identity)
            : winner;
    }

    /// <summary>The side whose provider names a host/engine pair (Cursor driving Claude Code), or null.</summary>
    public static AgentSession? HostIdentitySession(AgentSession a, AgentSession b)
    {
        var pa = a.Provider.ToLowerInvariant();
        var pb = b.Provider.ToLowerInvariant();
        if (pa == pb) return null;
        if (HostProviders.Contains(pa) && EmbeddedEngineProviders.Contains(pb)) return a;
        if (HostProviders.Contains(pb) && EmbeddedEngineProviders.Contains(pa)) return b;
        return null;
    }

    /// <summary>The session the collapsed notch names: top state, then a real title, then the newest.</summary>
    public static AgentSession? PrimarySession(IEnumerable<AgentSession> sessions)
    {
        var visible = sessions.Where(s => s.IsVisible && !IsSimulation(s)).ToList();
        if (visible.Count == 0) return null;
        var top = visible.Max(s => s.DisplayState.SortPriority());
        return visible.Where(s => s.DisplayState.SortPriority() == top)
            .OrderBy(s => HasReliableChatName(s.ChatName))
            .ThenBy(s => s.UpdatedAtMs)
            .Last();
    }

    public static bool IsSimulation(AgentSession session) =>
        IsSimulationConversationId(session.ConversationId) || IsSimulationConversationId(session.Id);

    public static bool IsSimulationConversationId(string value)
    {
        var id = value.ToLowerInvariant();
        return id.Contains("kannu-test") || id == "default" || id.StartsWith("test-", StringComparison.Ordinal);
    }

    public static bool HasReliableChatName(string? value) =>
        value?.Trim() is { Length: > 0 } trimmed && !LooksLikeToolName(trimmed);

    /// <summary>A candidate title that is really a tool name leaked into the name field.</summary>
    public static bool LooksLikeToolName(string? value)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0) return false;
        if (RequiresUserApproval(trimmed)) return true;
        return HookEventMapper.Compact(trimmed) is "shell" or "read" or "grep" or "rg" or "edit" or "write" or "task"
            or "applypatch" or "strreplace" or "glob" or "openresource" or "todowrite" or "createsubagent" or "subagent";
    }

    /// <summary>Built-in tools that commonly pause for explicit user approval.</summary>
    public static bool RequiresUserApproval(string toolName)
    {
        var lower = toolName.Trim().ToLowerInvariant();
        if (lower.Length == 0) return false;
        if (lower is "websearch" or "webfetch" or "web_search" or "web_fetch" or "askquestion" or "ask_question"
            or "userquestion" or "permissionrequest" or "shell" or "run_terminal_cmd" or "search") return true;
        var compact = lower.Replace("_", "").Replace("-", "");
        return compact is "websearch" or "webfetch" or "search" or "askquestion" or "shell" or "runterminalcmd";
    }

    public static bool ShouldDropUnbackedCursorHookFile(long tsMs, long nowMs) => nowMs - tsMs > UnbackedCursorHookGraceMs;

    /// <summary>A conversation id as the hook writes it: 1–64 of <c>[A-Za-z0-9_-]</c>.</summary>
    public static bool IsHookConversationId(string? value) =>
        value is { Length: >= 1 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public sealed record RetainedEndedSession(AgentSession Session, long EndedAtMs);

    /// <summary>
    /// Keeps a conversation that was visibly red and has since gone — its file deleted by SessionEnd,
    /// or its dim window elapsed — listed as an inactive, visible card for the retention window.
    /// Inactive, so the light and the primary pick ignore it; dropped the moment it is live again.
    /// </summary>
    public static (IReadOnlyList<AgentSession> Sessions, Dictionary<string, RetainedEndedSession> Retained) RetainEndedSessions(
        IReadOnlyList<AgentSession> previous, IReadOnlyList<AgentSession> current,
        IReadOnlyDictionary<string, RetainedEndedSession> retained, long nowMs, long retentionMs = EndedChatRetentionMs)
    {
        var map = retained.Where(r => nowMs - r.Value.EndedAtMs < retentionMs).ToDictionary(r => r.Key, r => r.Value);
        var currentById = new Dictionary<string, AgentSession>();
        foreach (var c in current) currentById.TryAdd(c.ConversationId, c);

        foreach (var (id, session) in currentById)
        {
            if (session.IsVisible && session.DisplayState != AgentLightState.Inactive) map.Remove(id);
        }

        foreach (var prior in previous)
        {
            if (!prior.IsVisible || prior.DisplayState != AgentLightState.Stopped || IsSimulation(prior) || map.ContainsKey(prior.ConversationId)) continue;
            if (currentById.TryGetValue(prior.ConversationId, out var now) && now.IsVisible) continue;
            map[prior.ConversationId] = new RetainedEndedSession(prior.WithDisplayState(AgentLightState.Inactive, true, nowMs), nowMs);
        }

        var output = current.Select(s => !s.IsVisible && map.TryGetValue(s.ConversationId, out var kept) ? kept.Session : s).ToList();
        var listed = output.Select(s => s.ConversationId).ToHashSet();
        output.AddRange(map.Where(m => !listed.Contains(m.Key)).Select(m => m.Value.Session));
        return (output, map);
    }
}
