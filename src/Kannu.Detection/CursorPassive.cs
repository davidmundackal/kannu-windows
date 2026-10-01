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

using System.Diagnostics;
using Kannu.Core;

namespace Kannu.Detection;

/// <param name="Sessions">One card per recent Cursor transcript.</param>
/// <param name="Analysis">Each transcript's tail analysis, for enriching Cursor hook cards.</param>
/// <param name="SubagentParents">Cursor Task/subagent transcripts to their parent chat.</param>
/// <param name="TitleSources">The title sources, for naming Cursor hook cards too.</param>
public sealed record CursorScan(
    IReadOnlyList<AgentSession> Sessions,
    IReadOnlyDictionary<string, TranscriptAnalysis> Analysis,
    IReadOnlyDictionary<string, string> SubagentParents,
    ChatTitleSources? TitleSources)
{
    public static readonly CursorScan None = new([], new Dictionary<string, TranscriptAnalysis>(), new Dictionary<string, string>(), null);

    /// <summary>Chats with an approval card the transcript says is open: corroborates a Cursor hook's yellow.</summary>
    public IReadOnlySet<string> PendingApprovalIds => Analysis.Where(a => a.Value.HasPendingToolApproval).Select(a => a.Key).ToHashSet();
}

/// <summary>
/// Cursor sessions without hooks, from its agent transcripts and composer database. Port of the macOS
/// monitor's <c>buildTranscriptSessions</c>. Runs only while Cursor is running.
/// </summary>
public sealed class CursorPassive(CursorTranscripts transcripts, CursorDatabase database)
{
    public static bool IsCursorRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("Cursor");
            var running = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            return running;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public CursorScan Scan(AgentTimings timings, long nowMs, IReadOnlySet<string> hookCursorIds)
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime;
        var paths = transcripts.RecentTranscriptPaths(timings.StaleMinutes, nowUtc);
        var analysis = paths.ToDictionary(CursorTranscripts.SessionId, CursorTranscripts.Analyze);
        var subagentParents = transcripts.SubagentParents(timings.StaleMinutes, nowUtc);

        var ids = paths.Select(CursorTranscripts.SessionId).Concat(hookCursorIds).ToHashSet();
        if (ids.Count == 0) return new CursorScan([], analysis, subagentParents, null);

        var meta = database.ComposerMeta(ids, nowUtc);
        var sources = new ChatTitleSources(
            meta,
            database.GlassTitles(ids, nowUtc),
            paths.Select(p => (Id: CursorTranscripts.SessionId(p), Title: transcripts.DisplayChatName(p)))
                .Where(t => t.Title is not null).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First().Title!),
            paths.Select(p => (Id: CursorTranscripts.SessionId(p), Snippets: transcripts.AssistantSnippets(p)))
                .Where(s => s.Snippets.Count > 0).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Snippets),
            database.PlanRegistryNames(nowUtc));

        var sessions = new List<AgentSession>();
        foreach (var path in paths)
        {
            var id = CursorTranscripts.SessionId(path);
            var a = analysis[id];
            var snapshot = new AgentSessionSnapshot(id, a.MtimeMs, null, a.IsDone, a.HasActiveToolUse, a.HasPendingToolApproval,
                a.IsUserPromptAwaitingResponse, a.MtimeMs);
            if (meta.TryGetValue(id, out var m))
            {
                snapshot = snapshot with
                {
                    LastActivityMs = Math.Max(snapshot.LastActivityMs, Math.Max(m.UpdatedMs, m.CheckpointMs)),
                    ComposerStatus = m.Status,
                    TranscriptMtimeMs = Math.Max(snapshot.TranscriptMtimeMs, m.CheckpointMs),
                };
            }
            var (state, visible) = CursorSessions.Map(snapshot, nowMs, timings);
            sessions.Add(new AgentSession
            {
                Id = $"cursor-{id}",
                Provider = "cursor",
                ConversationId = id,
                ChatName = CursorSessions.ResolveChatName(id, null, sources),
                ProjectName = transcripts.DisplayProjectName(transcripts.ProjectSlug(path)),
                RawState = snapshot.ComposerStatus ?? "transcript",
                DisplayState = state,
                UpdatedAtMs = snapshot.LastActivityMs,
                IsVisible = visible,
            });
        }
        return new CursorScan(sessions, analysis, subagentParents, sources);
    }
}
