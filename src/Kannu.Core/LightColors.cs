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

/// <summary>The closed notch's light: macOS Kannu's <c>AgentTrafficLightStyle</c>.</summary>
public enum LightStyle
{
    /// <summary>All three lights, with the inactive two dimmed.</summary>
    Classic,

    /// <summary>Only the light that is currently lit.</summary>
    Minimal,
}

/// <summary>macOS Kannu's curated traffic-light palette (<c>AgentTrafficLightPaletteColor</c>), same values.</summary>
public enum PaletteColor
{
    Green,
    Mint,
    Teal,
    Blue,
    Indigo,
    Purple,
    Pink,
    Red,
    Orange,
    Yellow,
}

public enum LightSlot
{
    Active,
    Awaiting,
    Stopped,
}

/// <summary>
/// The colour of each state. Two states can never share a colour, so the lights stay readable with
/// red-green colour blindness: a colour another state uses is not offered (macOS disables it too).
/// </summary>
public sealed record LightColors(PaletteColor Active, PaletteColor Awaiting, PaletteColor Stopped)
{
    public static LightColors Default { get; } = new(PaletteColor.Green, PaletteColor.Yellow, PaletteColor.Red);

    public static string Hex(PaletteColor color) => color switch
    {
        PaletteColor.Green => "#22C55E",
        PaletteColor.Mint => "#5EEAD4",
        PaletteColor.Teal => "#06B6D4",
        PaletteColor.Blue => "#3B82F6",
        PaletteColor.Indigo => "#818CF8",
        PaletteColor.Purple => "#A855F7",
        PaletteColor.Pink => "#EC4899",
        PaletteColor.Red => "#EF4444",
        PaletteColor.Orange => "#F97316",
        _ => "#FACC15",
    };

    public PaletteColor this[LightSlot slot] => slot switch
    {
        LightSlot.Active => Active,
        LightSlot.Awaiting => Awaiting,
        _ => Stopped,
    };

    /// <summary>Whether another state already uses the colour.</summary>
    public bool IsTakenByOther(LightSlot slot, PaletteColor color) =>
        Enum.GetValues<LightSlot>().Any(other => other != slot && this[other] == color);

    /// <summary>The colours with one slot changed; unchanged if another state already uses the colour.</summary>
    public LightColors With(LightSlot slot, PaletteColor color)
    {
        if (IsTakenByOther(slot, color)) return this;
        return slot switch
        {
            LightSlot.Active => this with { Active = color },
            LightSlot.Awaiting => this with { Awaiting = color },
            _ => this with { Stopped = color },
        };
    }

    /// <summary>A stored set that breaks the rule (hand-edited settings) falls back to the defaults.</summary>
    public LightColors Valid() => Active != Awaiting && Awaiting != Stopped && Active != Stopped ? this : Default;

    /// <summary>The lit colour for a light; null when inactive.</summary>
    public PaletteColor? For(TrafficLight light) => light switch
    {
        TrafficLight.Green => Active,
        TrafficLight.Yellow => Awaiting,
        TrafficLight.Red => Stopped,
        _ => null,
    };
}
