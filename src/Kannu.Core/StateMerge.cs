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
/// Claude runs every hook group matching one event as separate processes, in parallel, in no order.
/// Without a merge, whichever process writes last wins, and a "running" write can bury the "needs you"
/// one written a millisecond earlier. This keeps the more urgent state inside a short window.
/// </summary>
public static class StateMerge
{
    public const long WindowMs = 2_000;

    /// <returns>The state and timestamp to write.</returns>
    public static (RawState State, long Ts) Apply(StatusRecord? existing, RawState incoming, string hookEvent, long nowMs)
    {
        if (existing is null || RawStateWire.Parse(existing.State) is not { } existingState) return (incoming, nowMs);

        // Scoped to one event's parallel group, so consecutive events are not re-arbitrated. One
        // cross-event case is carried: parallel tool calls can land a PermissionRequest (yellow) for
        // one tool and a PreToolUse (green) for its sibling in the same window.
        var sameGroup = existing.HookEvent == hookEvent;
        var urgentCarry = existingState == RawState.AwaitingInput && existing.HookEvent == "PermissionRequest";
        if (!sameGroup && !urgentCarry) return (incoming, nowMs);

        // A negative age is a clock that went backwards or a hostile file; neither earns a carry.
        var age = nowMs - existing.Ts;
        if (age < 0 || age > WindowMs) return (incoming, nowMs);

        // Keep the original timestamp so chained events cannot make the window renew itself forever.
        return existingState.Priority() > incoming.Priority() ? (existingState, existing.Ts) : (incoming, nowMs);
    }
}
