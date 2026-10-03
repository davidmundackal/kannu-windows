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

using Kannu.Core;

namespace Kannu.Core.Tests;

public class LightColorsTests
{
    [Fact]
    public void DefaultsAreGreenYellowRed()
    {
        Assert.Equal(PaletteColor.Green, LightColors.Default.For(TrafficLight.Green));
        Assert.Equal(PaletteColor.Yellow, LightColors.Default.For(TrafficLight.Yellow));
        Assert.Equal(PaletteColor.Red, LightColors.Default.For(TrafficLight.Red));
        Assert.Null(LightColors.Default.For(TrafficLight.Inactive));
        Assert.Equal("#22C55E", LightColors.Hex(PaletteColor.Green));
    }

    [Fact]
    public void TwoStatesNeverShareAColour()
    {
        var colors = LightColors.Default;
        Assert.True(colors.IsTakenByOther(LightSlot.Active, PaletteColor.Red));
        Assert.False(colors.IsTakenByOther(LightSlot.Active, PaletteColor.Green));
        Assert.Same(colors, colors.With(LightSlot.Active, PaletteColor.Yellow));
        Assert.Equal(PaletteColor.Blue, colors.With(LightSlot.Active, PaletteColor.Blue).Active);
    }

    [Fact]
    public void ABrokenStoredSetFallsBackToTheDefaults() =>
        Assert.Equal(LightColors.Default, new LightColors(PaletteColor.Red, PaletteColor.Red, PaletteColor.Blue).Valid());

    [Fact]
    public void ColoursAndStyleLoadAndRejectNonsense()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kannu-colors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "s.json");
            File.WriteAllText(file, """{"lightStyle":"minimal","activeColor":"teal","awaitingColor":"teal","stoppedColor":"red","skinScrim":5}""");
            var s = AppSettings.Load(file);
            Assert.Equal(LightStyle.Minimal, s.LightStyle);
            Assert.Equal(LightColors.Default, s.LightColors);
            Assert.Equal(0.9, s.SkinScrim);
            File.WriteAllText(file, """{"activeColor":"42","awaitingColor":"beige"}""");
            Assert.Equal(LightColors.Default, AppSettings.Load(file).LightColors);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
