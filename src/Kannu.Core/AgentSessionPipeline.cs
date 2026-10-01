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

/// <summary>Everything a rescan learned from outside the hook files (passive detection).</summary>
/// <param name="PassiveClaude">Claude sessions read from transcripts and live processes.</param>
/// <param name="DeadPidConversationIds">Claude conversations whose process is provably gone.</param>
/// <param name="LiveClaudeTails">Tail verdicts for Claude processes alive this scan.</param>
/// <param name="CursorPendingApprovalIds">Cursor chats with an approval card open.</param>
/// <param name="OtherSessions">Sessions from sources with no hook (Warp, Claude Desktop agent mode, Cursor transcripts).</param>
/// <param name="CursorAnalysis">Cursor transcript tails, layered onto Cursor hook cards.</param>
/// <param name="CursorSubagentParents">Cursor Task/subagent chats to the chat that launched them.</param>
/// <param name="CursorTitleSources">Where Cursor chat titles come from, for naming Cursor hook cards.</param>
public sealed record PassiveEvidence(
    IReadOnlyList<AgentSession> PassiveClaude,
    IReadOnlySet<string> DeadPidConversationIds,
    IReadOnlyDictionary<string, ClaudeTailState> LiveClaudeTails,
    IReadOnlySet<string> CursorPendingApprovalIds,
    IReadOnlyList<AgentSession> OtherSessions,
    IReadOnlyDictionary<string, TranscriptAnalysis>? CursorAnalysis = null,
    IReadOnlyDictionary<string, string>? CursorSubagentParents = null,
    ChatTitleSources? CursorTitleSources = null)
{
    public static readonly PassiveEvidence None = new([], new HashSet<string>(), new Dictionary<string, ClaudeTailState>(), new HashSet<string>(), []);
}

/// <param name="Sessions">The cards to show, most important first.</param>
/// <param name="Light">The aggregate light.</param>
/// <param name="StaleFiles">Hook files to delete if unchanged.</param>
public sealed record PipelineResult(IReadOnlyList<AgentSession> Sessions, AgentLightState Light, IReadOnlyList<HookFile> StaleFiles);

/// <summary>
/// One rescan, start to finish, in the macOS monitor's order: hook files to sessions (stale cap,
/// yellow hold, subagent fold), merge with passive evidence, keep ended red chats listed briefly,
/// carry the run clock, one card per conversation. Stateful only for what must survive between
/// rescans (the previous list, retained chats, the clock); every rule it applies is tested on its own.
/// </summary>
public sealed class AgentSessionPipeline(AgentTimings timings, string home)
{
    private IReadOnlyList<AgentSession> _previous = [];
    private Dictionary<string, AgentStateMachine.RetainedEndedSession> _retained = [];
    private Dictionary<string, long> _clock = [];

    public AgentTimings Timings { get; set; } = timings;

    public PipelineResult Update(IReadOnlyList<HookFile> files, long nowMs, PassiveEvidence? evidence = null)
    {
        evidence ??= PassiveEvidence.None;
        var hook = HookSessionReader.Build(files, Timings, nowMs, home, evidence.LiveClaudeTails, evidence.CursorPendingApprovalIds);

        IReadOnlyList<AgentSession> sessions = AgentStateMachine.ReconcileClaudeSessions(hook.Sessions, evidence.PassiveClaude,
            evidence.DeadPidConversationIds, Timings.CollapseMs, Timings.InactiveMs, nowMs);
        // Cursor: transcript context on the hook cards before the merge (a merged card can wear a
        // Cursor identity over a Claude-won state, and must not then be repainted by Cursor evidence).
        if (evidence.CursorAnalysis is { Count: > 0 } analysis) sessions = CursorSessions.EnrichHookSessions(sessions, analysis, nowMs);
        sessions = CursorSessions.Merge(sessions, evidence.OtherSessions, nowMs);
        if (evidence.CursorSubagentParents is { Count: > 0 } parents) sessions = CursorSessions.CollapseSubagents(sessions, parents, nowMs);
        if (evidence.CursorTitleSources is { } sources)
        {
            sessions = sessions.Select(s => s.Provider.Equals("cursor", StringComparison.OrdinalIgnoreCase)
                ? s with { ChatName = CursorSessions.ResolveChatName(s.ConversationId, s.ChatName, sources) ?? s.ChatName }
                : s).ToList();
        }
        sessions = sessions.Where(s => !hook.FoldedSubagentIds.Contains(s.ConversationId)).ToList();

        var (withRetained, retained) = AgentStateMachine.RetainEndedSessions(_previous, sessions, _retained, nowMs);
        _retained = retained;

        var latest = AgentStateMachine.LatestSessions(withRetained);
        var previousActive = _previous.Where(s => s.DisplayState.IsActiveRun()).Select(s => s.ConversationId).ToHashSet();
        var clock = AgentExecutionClock.Resolve(
            latest.Select(s => new AgentExecutionClock.Input(s.ConversationId, s.DisplayState.IsActiveRun(), s.HasActiveRawState,
                s.UpdatedAtMs, previousActive.Contains(s.ConversationId))),
            _clock, nowMs);
        _clock = clock.StartByConversationId;
        latest = latest.Select(s => s with
        {
            ExecutionStartedAtMs = clock.DisplayedStartByConversationId.TryGetValue(s.ConversationId, out var start) ? start : null,
        }).ToList();
        _previous = latest;

        var visible = latest
            .Where(s => s.IsVisible && !AgentStateMachine.IsSimulation(s))
            .OrderByDescending(s => s.DisplayState.SortPriority())
            .ThenByDescending(s => s.UpdatedAtMs)
            .ToList();
        return new PipelineResult(visible, AgentStateMachine.ResolveDisplayState(latest), hook.StaleFiles);
    }
}
