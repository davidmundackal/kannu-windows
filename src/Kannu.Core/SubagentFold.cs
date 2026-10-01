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

/// <summary>
/// A subagent is part of its chat, never a card of its own. Port of macOS
/// <c>foldSubagentHookSessions</c>:
/// <list type="bullet">
/// <item>While the parent's turn is open, the more urgent light shows on the parent's card — a
/// subagent waiting on permission turns it yellow.</item>
/// <item>Once the parent's turn has ended, a leftover subagent file changes nothing.</item>
/// <item>With no parent file, an inactive, invisible stand-in carries the parent's id, for passive
/// evidence to promote when the parent is provably still running.</item>
/// </list>
/// The turn is the parent's with the subagent's tool calls added.
/// </summary>
public static class SubagentFold
{
    /// <param name="parentByKey">"provider|conversationId" of each subagent to its parent's id.</param>
    /// <returns>The folded list and the subagent conversation ids folded away.</returns>
    public static (IReadOnlyList<AgentSession> Sessions, HashSet<string> Folded) Fold(
        IReadOnlyList<AgentSession> sessions, IReadOnlyDictionary<string, string> parentByKey)
    {
        if (parentByKey.Count == 0) return (sessions, []);
        var output = new List<AgentSession>();
        var indexByKey = new Dictionary<string, int>();
        var subagents = new List<AgentSession>();
        foreach (var session in sessions)
        {
            if (parentByKey.ContainsKey(Key(session)))
            {
                subagents.Add(session);
            }
            else
            {
                indexByKey[Key(session)] = output.Count;
                output.Add(session);
            }
        }

        var folded = new HashSet<string>();
        foreach (var sub in subagents.OrderBy(s => s.UpdatedAtMs).ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            var parentId = parentByKey[Key(sub)];
            folded.Add(sub.ConversationId);
            var parentKey = sub.Provider.ToLowerInvariant() + "|" + parentId;
            if (indexByKey.TryGetValue(parentKey, out var index))
            {
                output[index] = Folding(sub, output[index]);
            }
            else
            {
                // Not the subagent's light: a leftover file after the chat ended must not show it as
                // running. The raw state stays, so live passive evidence can still promote it.
                var standIn = new AgentSession
                {
                    Id = $"{sub.Provider}-{parentId}",
                    Provider = sub.Provider,
                    ConversationId = parentId,
                    ProjectName = sub.ProjectName,
                    RawState = sub.RawState,
                    DisplayState = AgentLightState.Inactive,
                    UpdatedAtMs = sub.UpdatedAtMs,
                    IsVisible = false,
                    ExecutionStartedAtMs = sub.ExecutionStartedAtMs,
                    Cwd = sub.Cwd,
                }.CarryingExtras(sub) with { Turn = null };
                indexByKey[parentKey] = output.Count;
                output.Add(standIn);
            }
        }
        return (output, folded);
    }

    private static string Key(AgentSession s) => s.Provider.ToLowerInvariant() + "|" + s.ConversationId;

    /// <summary>Inside an open turn only a prompt outranks work, and work outranks thinking.</summary>
    private static int TurnUrgency(AgentLightState state) => state switch
    {
        AgentLightState.AwaitingInput => 3,
        AgentLightState.Executing => 2,
        AgentLightState.Thinking => 1,
        _ => 0,
    };

    private static AgentSession Folding(AgentSession sub, AgentSession parent)
    {
        var turnOpen = parent.HasActiveRawState || AgentStateMachine.IsAwaitingInputRawState(parent.RawState);
        var subWins = turnOpen && sub.IsVisible
                      && (!parent.IsVisible || TurnUrgency(sub.DisplayState) > TurnUrgency(parent.DisplayState));
        var turn = HookTurn.Folding(sub.Turn, parent.Turn);
        if (!subWins) return parent.CarryingExtras(sub) with { Turn = turn };

        return (parent with
        {
            ProjectName = parent.ProjectName ?? sub.ProjectName,
            RawState = sub.RawState,
            DisplayState = sub.DisplayState,
            UpdatedAtMs = sub.UpdatedAtMs,
            IsVisible = true,
            ExecutionStartedAtMs = parent.ExecutionStartedAtMs ?? sub.ExecutionStartedAtMs,
            Cwd = parent.Cwd ?? sub.Cwd,
        }).CarryingExtras(sub) with { Turn = turn };
    }
}
