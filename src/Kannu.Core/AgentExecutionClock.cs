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
/// When a card's run clock starts, for sessions with no hook turn to read it from (passive sources,
/// old hook files). Port of macOS <c>AgentExecutionClock</c> (REGRESSIONS entry 15): a demotion by
/// the staleness ladder — display dipped, raw state still active — is not an end, so the clock is
/// kept; only a raw state that is no longer active clears it.
/// </summary>
public static class AgentExecutionClock
{
    /// <param name="IsActiveRun">The display state is an active run, after the staleness ladder.</param>
    /// <param name="HasActiveRawState">What the source reported, before the ladder.</param>
    /// <param name="PreviousWasActiveRun">The previously published state for this conversation was an active run.</param>
    public sealed record Input(string ConversationId, bool IsActiveRun, bool HasActiveRawState, long UpdatedAtMs, bool PreviousWasActiveRun);

    /// <param name="StartByConversationId">The clock to carry into the next cycle.</param>
    /// <param name="DisplayedStartByConversationId">What each card shows; absent for anything not an active run.</param>
    public sealed record Resolution(Dictionary<string, long> StartByConversationId, Dictionary<string, long> DisplayedStartByConversationId);

    public static Resolution Resolve(IEnumerable<Input> inputs, IReadOnlyDictionary<string, long> existing, long nowMs)
    {
        var list = inputs.ToList();
        var live = list.Select(i => i.ConversationId).ToHashSet();
        // Drop conversations no longer listed at all, so the map cannot grow forever.
        var carried = existing.Where(e => live.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value);
        var displayed = new Dictionary<string, long>();
        foreach (var input in list)
        {
            if (!input.IsActiveRun)
            {
                // A demotion keeps the clock; a genuine end clears it.
                if (!input.HasActiveRawState) carried.Remove(input.ConversationId);
                continue;
            }
            long start;
            if (input.PreviousWasActiveRun)
            {
                // Mid-run: updatedAt dates only a run already in progress when Kannu started watching.
                start = carried.TryGetValue(input.ConversationId, out var c) ? c : input.UpdatedAtMs;
            }
            else
            {
                // Becoming active again: carried exists exactly when the dip was a demotion.
                start = carried.TryGetValue(input.ConversationId, out var c) ? c : nowMs;
            }
            carried[input.ConversationId] = start;
            displayed[input.ConversationId] = start;
        }
        return new Resolution(carried, displayed);
    }
}

/// <summary>The run time a card shows for its request. Port of macOS <c>AgentTurnDisplay</c>.</summary>
public abstract record TurnDisplay
{
    /// <summary>Still running: the view ticks from this time.</summary>
    public sealed record Live(long SinceMs) : TurnDisplay;

    /// <summary>Ended: "Ran …" for this long.</summary>
    public sealed record Ended(long DurationMs) : TurnDisplay;

    /// <summary>Writes after the end that still report work mean the request went on without a new prompt.</summary>
    private const long SupersededAfterMs = 2_000;

    /// <summary>
    /// An ended turn shows how long it ran; an open turn ticks from the prompt while running or
    /// waiting; a card that stopped without a Stop shows no time rather than a wrong one. Without a
    /// turn, the in-memory run start is shown only while running.
    /// </summary>
    public static TurnDisplay? For(HookTurn? turn, long? executionStartedAtMs, AgentLightState state, bool hookReportsWork, long updatedAtMs)
    {
        if (turn is null) return state.IsActiveRun() && executionStartedAtMs is { } since ? new Live(since) : null;
        if (turn.EndedMs is { } end && !(hookReportsWork && updatedAtMs - end > SupersededAfterMs))
        {
            return new Ended(Math.Max(0, end - turn.StartedMs));
        }
        return state.IsActiveRun() ? new Live(turn.StartedMs) : null;
    }

    /// <summary>"42s", "3m 24s", "1h 53m 54s".</summary>
    public static string Format(long durationMs)
    {
        var total = Math.Max(0, durationMs / 1000);
        long hours = total / 3600, minutes = total % 3600 / 60, seconds = total % 60;
        if (hours > 0) return $"{hours}h {minutes}m {seconds}s";
        return minutes > 0 ? $"{minutes}m {seconds}s" : $"{seconds}s";
    }

    /// <summary>Without seconds once there are minutes, for a narrow column: "1h 53m".</summary>
    public static string FormatShort(long durationMs)
    {
        var total = Math.Max(0, durationMs / 1000);
        long hours = total / 3600, minutes = total % 3600 / 60;
        if (hours > 0) return $"{hours}h {minutes}m";
        return minutes > 0 ? $"{minutes}m" : Format(durationMs);
    }

    /// <summary>"1 tool", "212 tools"; null for none.</summary>
    public static string? Tools(int count) => count <= 0 ? null : count == 1 ? "1 tool" : $"{count} tools";
}
