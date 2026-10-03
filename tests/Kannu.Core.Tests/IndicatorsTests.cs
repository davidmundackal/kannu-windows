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
using Xunit;

namespace Kannu.Core.Tests;

public class IndicatorsTests
{
    [Fact]
    public void BrightnessMovesInSixtyFourthsAndStaysInRange()
    {
        Assert.Equal(52, BrightnessSteps.Next(50, 0, 100, +1));
        Assert.Equal(48, BrightnessSteps.Next(50, 0, 100, -1));
        Assert.Equal(100, BrightnessSteps.Next(99, 0, 100, +1));
        Assert.Equal(0, BrightnessSteps.Next(1, 0, 100, -1));
        Assert.Equal(11, BrightnessSteps.Next(10, 0, 20, +1)); // never less than one unit
        Assert.Equal(0.5, BrightnessSteps.Fraction(50, 0, 100));
    }

    [Fact]
    public void ScreenCaptureIsActiveUntilItStops()
    {
        var usages = new ScreenCapture.Usage[]
        {
            new(@"C:#Program Files#obs-studio#bin#64bit#obs64.exe", 200, 0),
            new("Microsoft.ScreenSketch_8wekyb3d8bbwe", 300, 100),
            new("Microsoft.Teams_8wekyb3d8bbwe", 300, 400),
            new(@"C:#Users#me#AppData#Local#Kannu#current#Kannu.exe", 500, 0),
            new("Never.Used_x", 0, 0),
        };
        Assert.Equal(["obs64", "ScreenSketch"], ScreenCapture.ActiveApps(usages));
        Assert.Equal("Teams", ScreenCapture.AppName("Microsoft.Teams_8wekyb3d8bbwe"));
    }
}
