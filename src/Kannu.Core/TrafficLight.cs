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

/// <summary>What the notch shows for one session.</summary>
public enum TrafficLight
{
    /// <summary>Dim: the session is open but nothing is running, or its last state went stale.</summary>
    Inactive,

    /// <summary>The agent is working.</summary>
    Green,

    /// <summary>The agent needs the user.</summary>
    Yellow,

    /// <summary>The agent finished or stopped.</summary>
    Red,
}

public static class TrafficLightOrder
{
    /// <summary>
    /// Sort and aggregate order: what needs the user first, then what is working, then what finished.
    /// </summary>
    public static int Urgency(this TrafficLight light) => light switch
    {
        TrafficLight.Yellow => 3,
        TrafficLight.Green => 2,
        TrafficLight.Red => 1,
        _ => 0,
    };
}
