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

/// <summary>A Cursor composer as its database describes it.</summary>
public sealed record ComposerMeta(string ComposerId, string? Status, long UpdatedMs, long CheckpointMs, long CreatedMs, string? Name);

/// <summary>A passive session's facts before they become a light (port of macOS <c>AgentSessionSnapshot</c>).</summary>
public sealed record AgentSessionSnapshot(
    string SessionId,
    long LastActivityMs,
    string? ComposerStatus,
    bool IsDone,
    bool HasActiveToolUse,
    bool HasPendingToolApproval,
    bool IsUserPromptAwaitingResponse,
    long TranscriptMtimeMs);

/// <summary>Every title source a Cursor chat may be named from.</summary>
public sealed record ChatTitleSources(
    IReadOnlyDictionary<string, ComposerMeta> ComposerMeta,
    IReadOnlyDictionary<string, string> GlassTitles,
    IReadOnlyDictionary<string, string> TranscriptTitles,
    IReadOnlyDictionary<string, IReadOnlyList<string>> TranscriptAssistantSnippets,
    IReadOnlySet<string> PlanRegistryTitles);

/// <summary>
/// How Cursor's hook files, transcripts and composer database combine into one card per chat. Port of
/// the Cursor half of the macOS monitor (<c>map</c>, <c>enrichHookSessionsWithTranscripts</c>,
/// <c>mergeSessions</c>, <c>collapseSubagentSessions</c>, <c>resolveCursorChatName</c>).
/// </summary>
public static class CursorSessions
{
    private const long RunningStaleMs = 360_000;
    private const long AbortedIdleMs = 90_000;
    private const long FreshHookMs = 90_000;

    private static readonly HashSet<string> AwaitingStatuses =
        ["awaiting_input", "awaitinginput", "awaiting-user", "needs_user", "needs-user", "requires_approval",
         "permission_required", "permission_request"];

    /// <summary>A transcript/composer snapshot to a light. Yellow here only from a live awaiting status or a pending gated tool.</summary>
    public static (AgentLightState State, bool Visible) Map(AgentSessionSnapshot? session, long nowMs, AgentTimings timings)
    {
        if (session is null || nowMs - session.LastActivityMs > timings.StaleMs) return (AgentLightState.Inactive, false);

        (AgentLightState, bool) AfterStop()
        {
            var age = nowMs - session.LastActivityMs;
            if (age <= timings.CollapseMs) return (AgentLightState.Stopped, true);
            return age <= timings.CollapseMs + timings.InactiveMs ? (AgentLightState.Inactive, true) : (AgentLightState.Inactive, false);
        }

        var live = (session.ComposerStatus ?? "").ToLowerInvariant();
        if (AwaitingStatuses.Contains(live)) return (AgentLightState.AwaitingInput, true);

        var generating = live is "generating" or "running" or "streaming" or "thinking";
        // The transcript lags the live card: a generating status or an in-flight tool is fresher.
        if (session.HasPendingToolApproval && !generating && !session.HasActiveToolUse) return (AgentLightState.AwaitingInput, true);

        var abortedIdle = live == "aborted" && session.TranscriptMtimeMs > 0 && nowMs - session.TranscriptMtimeMs > AbortedIdleMs;
        if (session.IsUserPromptAwaitingResponse) return (AgentLightState.Thinking, true);
        if (session.IsDone && !generating) return AfterStop();
        if (abortedIdle) return AfterStop();
        if (live == "thinking") return (AgentLightState.Thinking, true);
        if (generating || session.HasActiveToolUse)
        {
            return session.HasActiveToolUse || live is "generating" or "running" or "streaming"
                ? (AgentLightState.Executing, true)
                : (AgentLightState.Thinking, true);
        }
        if (nowMs - session.LastActivityMs <= RunningStaleMs) return (AgentLightState.Thinking, true);
        return AfterStop();
    }

    /// <summary>
    /// Transcript context on top of Cursor hook states. The hook file is the source of truth for
    /// yellow (preToolUse fires when the card shows); transcript signals never override it, and a
    /// fresh hook is never demoted by a lagging turn_ended.
    /// </summary>
    public static IReadOnlyList<AgentSession> EnrichHookSessions(IReadOnlyList<AgentSession> sessions,
        IReadOnlyDictionary<string, TranscriptAnalysis> analysis, long nowMs) =>
        sessions.Select(session =>
        {
            if (session.Provider.ToLowerInvariant() != "cursor") return session;
            var raw = session.RawState.ToLowerInvariant();
            if (raw is "stopped" or "stop" || session.DisplayState == AgentLightState.Stopped) return session;
            if (raw == "awaiting_input" || session.DisplayState == AgentLightState.AwaitingInput) return session;

            analysis.TryGetValue(session.ConversationId, out var a);
            if (a?.IsUserPromptAwaitingResponse == true)
            {
                return session with { RawState = "thinking", DisplayState = AgentLightState.Thinking, IsVisible = true };
            }
            if (a?.HasPendingToolApproval == true && session.DisplayState is AgentLightState.Thinking or AgentLightState.Executing)
            {
                return session with { RawState = "awaiting_input", DisplayState = AgentLightState.AwaitingInput, IsVisible = true };
            }
            var hookLive = raw is "thinking" or "executing" or "awaiting_input" && nowMs - session.UpdatedAtMs < FreshHookMs;
            if (a?.IsTurnEndedAtTail == true && session.DisplayState is AgentLightState.Executing or AgentLightState.Thinking && !hookLive)
            {
                return session with { RawState = "stopped", DisplayState = AgentLightState.Stopped, IsVisible = true };
            }
            return session;
        }).ToList();

    /// <summary>Hook and passive sessions for the same conversation, merged: the more urgent state, else the newer.</summary>
    public static IReadOnlyList<AgentSession> Merge(IReadOnlyList<AgentSession> hookSessions, IReadOnlyList<AgentSession> transcriptSessions, long nowMs)
    {
        var merged = new Dictionary<string, AgentSession>();
        var order = new List<string>();
        foreach (var session in hookSessions.Concat(transcriptSessions))
        {
            if (merged.TryGetValue(session.ConversationId, out var existing))
            {
                merged[session.ConversationId] = PreferredMerged(existing, session, nowMs);
            }
            else
            {
                merged[session.ConversationId] = session;
                order.Add(session.ConversationId);
            }
        }
        return order.Select(id => merged[id]).ToList();
    }

    public static AgentSession PreferredMerged(AgentSession existing, AgentSession incoming, long nowMs)
    {
        AgentSession winner, loser;
        if (PreferFreshHookActive(existing, incoming, nowMs)) (winner, loser) = (existing, incoming);
        else if (PreferFreshHookActive(incoming, existing, nowMs)) (winner, loser) = (incoming, existing);
        else if (existing.DisplayState != incoming.DisplayState)
        {
            (winner, loser) = existing.DisplayState.SortPriority() > incoming.DisplayState.SortPriority() ? (existing, incoming) : (incoming, existing);
        }
        else (winner, loser) = incoming.UpdatedAtMs >= existing.UpdatedAtMs ? (incoming, existing) : (existing, incoming);

        var merged = (winner with
        {
            ChatName = PreferredChatName(winner.ChatName, loser.ChatName),
            ProjectName = NonEmpty(winner.ProjectName) ?? NonEmpty(loser.ProjectName),
            IsVisible = winner.IsVisible || loser.IsVisible,
            ExecutionStartedAtMs = winner.ExecutionStartedAtMs ?? loser.ExecutionStartedAtMs,
            Cwd = winner.Cwd ?? loser.Cwd,
            HostPid = winner.HostPid ?? loser.HostPid,
            HostName = winner.HostPid is null ? loser.HostName : winner.HostName,
            HostWindow = winner.HostWindow ?? loser.HostWindow,
        }).CarryingExtras(winner).CarryingExtras(loser);

        // A host and the engine it embeds report one conversation: the host names the card.
        return AgentStateMachine.HostIdentitySession(existing, incoming) is { } identity
               && !string.Equals(identity.Provider, winner.Provider, StringComparison.OrdinalIgnoreCase)
            ? merged.AdoptingIdentity(identity)
            : merged;
    }

    /// <summary>
    /// A fresh hook's executing state beats a transcript's lingering yellow (a WebSearch already
    /// approved); a transcript yellow still beats hook thinking (a Shell card).
    /// </summary>
    private static bool PreferFreshHookActive(AgentSession primary, AgentSession secondary, long nowMs) =>
        IsHookState(primary) && secondary.DisplayState == AgentLightState.AwaitingInput && !IsHookState(secondary)
        && nowMs - primary.UpdatedAtMs < FreshHookMs && primary.DisplayState == AgentLightState.Executing;

    private static bool IsHookState(AgentSession s) => s.RawState.ToLowerInvariant() is
        "thinking" or "executing" or "awaiting_input" or "awaitinginput" or "awaiting" or "stopped" or "stop" or "completed" or "aborted" or "error";

    private static string? PreferredChatName(string? primary, string? fallback) =>
        AgentStateMachine.HasReliableChatName(primary) ? primary!.Trim()
        : AgentStateMachine.HasReliableChatName(fallback) ? fallback!.Trim()
        : NonEmpty(primary) ?? NonEmpty(fallback);

    /// <summary>Cursor Task/subagent activity rolls into the parent chat instead of extra cards.</summary>
    public static IReadOnlyList<AgentSession> CollapseSubagents(IReadOnlyList<AgentSession> sessions,
        IReadOnlyDictionary<string, string> subagentParents, long nowMs)
    {
        if (subagentParents.Count == 0) return sessions;
        var rolled = new Dictionary<string, AgentSession>();
        var order = new List<string>();
        foreach (var session in sessions)
        {
            // Only Cursor's own subagents: another provider sharing a uuid must not be re-keyed.
            var parent = session.Provider.ToLowerInvariant() == "cursor" && subagentParents.TryGetValue(session.ConversationId, out var p) ? p : null;
            var target = parent ?? session.ConversationId;
            var candidate = parent is null
                ? session
                : session with { Id = $"cursor-{parent}", ConversationId = parent, Turn = null };

            if (rolled.TryGetValue(target, out var existing))
            {
                var merged = PreferredMerged(existing, candidate, nowMs);
                rolled[target] = (merged with
                {
                    Id = existing.Id.StartsWith("cursor-", StringComparison.Ordinal) ? existing.Id : candidate.Id,
                    ConversationId = target,
                    ChatName = NonEmpty(existing.ChatName) ?? NonEmpty(candidate.ChatName),
                    ProjectName = NonEmpty(existing.ProjectName) ?? NonEmpty(candidate.ProjectName),
                    UpdatedAtMs = Math.Max(existing.UpdatedAtMs, candidate.UpdatedAtMs),
                    IsVisible = existing.IsVisible || candidate.IsVisible,
                    ExecutionStartedAtMs = merged.ExecutionStartedAtMs ?? existing.ExecutionStartedAtMs ?? candidate.ExecutionStartedAtMs,
                    Cwd = existing.Cwd ?? candidate.Cwd,
                    HostPid = existing.HostPid ?? candidate.HostPid,
                    HostName = existing.HostPid is null ? candidate.HostName : existing.HostName,
                    HostWindow = existing.HostWindow ?? candidate.HostWindow,
                }).CarryingExtras(existing).CarryingExtras(candidate);
            }
            else
            {
                rolled[target] = candidate;
                order.Add(target);
            }
        }
        return order.Select(id => rolled[id]).ToList();
    }

    /// <summary>
    /// A Cursor chat's name: the hook's, the composer's or Glass's title when reliable, else the
    /// transcript's first prompt (trusted directly — never compared against itself, REGRESSIONS entry 5).
    /// </summary>
    public static string? ResolveChatName(string sessionId, string? hookName, ChatTitleSources sources)
    {
        string?[] candidates = [hookName, sources.ComposerMeta.TryGetValue(sessionId, out var meta) ? meta.Name : null,
            sources.GlassTitles.TryGetValue(sessionId, out var glass) ? glass : null];
        foreach (var candidate in candidates)
        {
            if (NonEmpty(candidate) is { } name && !IsUnreliableTitle(name, sessionId, sources)) return name;
        }
        return sources.TranscriptTitles.TryGetValue(sessionId, out var title) && NonEmpty(title) is { } t && !AgentStateMachine.LooksLikeToolName(t)
            ? t
            : null;
    }

    private static bool IsUnreliableTitle(string candidate, string sessionId, ChatTitleSources sources)
    {
        if (AgentStateMachine.LooksLikeToolName(candidate) || sources.PlanRegistryTitles.Contains(candidate)) return true;
        if (sources.TranscriptTitles.TryGetValue(sessionId, out var prompt) && candidate == prompt.Trim()) return true;
        return sources.TranscriptAssistantSnippets.TryGetValue(sessionId, out var snippets)
               && snippets.Any(s => candidate == s || s.StartsWith(candidate, StringComparison.Ordinal) || candidate.StartsWith(s, StringComparison.Ordinal));
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
