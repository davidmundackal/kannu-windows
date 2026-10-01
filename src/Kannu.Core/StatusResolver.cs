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

public readonly record struct ResolvedLight(TrafficLight Light, bool Visible);

/// <summary>
/// Raw state plus age to what the notch shows. A hook only fires on events, so a crashed or killed
/// agent leaves its last state on disk; the age ladder is what stops it glowing forever.
/// </summary>
public static class StatusResolver
{
    /// <summary>Green with no event for this long is treated as a dead session.</summary>
    public const long ActiveStaleMs = 360_000;

    /// <summary>Yellow nothing can corroborate gives up after this long.</summary>
    public const long AwaitingInputStaleMs = 300_000;

    /// <summary>How long a finished, stale or idle session stays on the notch before it drops off.</summary>
    public const long KeepVisibleMs = 600_000;

    public static ResolvedLight Resolve(RawState? state, long ageMs)
    {
        // A timestamp in the future (clock skew, hostile file) is read as "just now".
        if (ageMs < 0) ageMs = 0;

        switch (state)
        {
            case RawState.Thinking or RawState.Executing when ageMs <= ActiveStaleMs:
                return new(TrafficLight.Green, true);
            case RawState.AwaitingInput when ageMs <= AwaitingInputStaleMs:
                return new(TrafficLight.Yellow, true);
            case RawState.Stopped or RawState.QuotaExceeded when ageMs <= KeepVisibleMs:
                return new(TrafficLight.Red, true);
            default:
                // Idle (session open, nothing running yet), a stale state, or one this build does not
                // know: a dim card, no lit light, until it ages off.
                return new(TrafficLight.Inactive, ageMs <= KeepVisibleMs);
        }
    }
}
