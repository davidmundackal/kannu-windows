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

public class ProblemReportsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 2, 11, 55, 0, TimeSpan.Zero);

    [Fact]
    public void FileNamesRoundTrip()
    {
        var name = ProblemReports.FileName(ProblemKind.Freeze, At);
        Assert.Equal("freeze-20261002T115500Z.txt", name);
        Assert.Equal(new ProblemReportFile(ProblemKind.Freeze, At, name), ProblemReports.Parse(name));
        Assert.Null(ProblemReports.Parse("crash-yesterday.txt"));
        Assert.Null(ProblemReports.Parse("kannu.log"));
    }

    [Fact]
    public void TheNewestUnofferedReportIsOfferedOnce()
    {
        string[] files = ["crash-20261001T100000Z.txt", "crash-20261002T090000Z.txt", "kannu.log", "freeze-20261002T090000Z.txt"];
        Assert.Equal("freeze-20261002T090000Z.txt", ProblemReports.ToOffer(files, null)?.FileName);
        Assert.Null(ProblemReports.ToOffer(files, "freeze-20261002T090000Z.txt"));
        Assert.Equal("freeze-20261002T090000Z.txt", ProblemReports.ToOffer(files, "crash-20261001T100000Z.txt")?.FileName);
        Assert.Equal("crash-20261002T090000Z.txt", ProblemReports.ToOffer(files[..3], "crash-20261001T100000Z.txt")?.FileName);
    }

    [Fact]
    public void ScrubTakesOutTheProfileUserAndPc()
    {
        var text = @"at C:\Users\Asha\src\app.cs; user ASHA on DESKTOP-42X; Ashantown stays";
        var scrubbed = ProblemReports.Scrub(text, @"C:\Users\Asha\", "Asha", "DESKTOP-42X");
        Assert.Equal(@"at %USERPROFILE%\src\app.cs; user <user> on <pc>; Ashantown stays", scrubbed);
        Assert.Equal("al and Al", ProblemReports.Scrub("al and Al", null, "Al", null));
    }

    [Fact]
    public void CrashAndFreezeTextsSayWhatHappened()
    {
        var crash = ProblemReports.CrashText("System.InvalidOperationException: boom\n   at X.Y()", "0.1.0", "Windows 11 24H2", At);
        Assert.Contains("Kannu for Windows 0.1.0 crashed.", crash);
        Assert.Contains("2026-10-02 11:55:00 UTC", crash);
        Assert.Contains("InvalidOperationException: boom", crash);
        var freeze = ProblemReports.FreezeText(6_200, "freeze-20261002T115500Z.dmp", "0.1.0", "Windows 11", At);
        Assert.Contains("at least 6 seconds", freeze);
        Assert.Contains("do not attach it to a public issue", freeze);
    }

    [Fact]
    public void IssueUrlsAreEncodedAndFitGitHubsLimit()
    {
        var url = ProblemReports.IssueUrl("https://github.com/o/r", "Crash: a+b", "line 1\nC++ & more");
        Assert.Equal("https://github.com/o/r/issues/new?title=Crash%3A%20a%2Bb&body=line%201%0AC%2B%2B%20%26%20more", url);
        var long_ = ProblemReports.IssueUrl("https://github.com/o/r", "t", new string('x', 20_000) + "✓");
        Assert.True(long_.Length <= ProblemReports.MaxIssueUrlLength);
        Assert.Contains(Uri.EscapeDataString("cut short"), long_);
    }
}

public class FreezeDetectorTests
{
    [Fact]
    public void AnAnsweredPingIsNeverAFreeze()
    {
        var d = new FreezeDetector(thresholdMs: 5_000, intervalMs: 1_000);
        for (long t = 1_000; t < 30_000; t += 1_000)
        {
            var action = d.Tick(t);
            Assert.NotEqual(FreezeAction.Freeze, action);
            if (action == FreezeAction.Ping) d.Answered();
        }
    }

    [Fact]
    public void AnUnansweredPingBecomesOneFreeze()
    {
        var d = new FreezeDetector(5_000, 1_000);
        Assert.Equal(FreezeAction.Ping, d.Tick(1_000));
        for (long t = 2_000; t < 6_000; t += 1_000) Assert.Equal(FreezeAction.None, d.Tick(t));
        Assert.Equal(FreezeAction.Freeze, d.Tick(6_000));
        Assert.Equal(5_000, d.StuckMs);
        Assert.Equal(FreezeAction.None, d.Tick(7_000));
        d.Answered();
        Assert.Equal(FreezeAction.Ping, d.Tick(8_000));
    }

    [Fact]
    public void ASleepIsNotAFreeze()
    {
        var d = new FreezeDetector(5_000, 1_000);
        Assert.Equal(FreezeAction.Ping, d.Tick(1_000));
        Assert.Equal(FreezeAction.None, d.Tick(2_000));
        // The PC slept for an hour with the ping outstanding.
        Assert.Equal(FreezeAction.None, d.Tick(3_602_000));
        for (long t = 3_603_000; t < 3_607_000; t += 1_000) Assert.Equal(FreezeAction.None, d.Tick(t));
        Assert.Equal(FreezeAction.Freeze, d.Tick(3_607_000));
    }
}

public class LaunchAtLoginTests
{
    [Fact]
    public void AnInstalledKannuStartsFromVelopacksStableLauncher()
    {
        var current = Path.Combine("L", "Kannu", "current") + Path.DirectorySeparatorChar;
        var stub = Path.Combine("L", "Kannu", "Kannu.exe");
        Assert.Equal(stub, LaunchAtLogin.StablePath(current, p => p == stub));
        Assert.Null(LaunchAtLogin.StablePath(current, _ => false));
        Assert.Null(LaunchAtLogin.StablePath(Path.Combine("dev", "out", "win-x64"), _ => true));
        Assert.Equal("\"C:\\Users\\A B\\AppData\\Local\\Kannu\\Kannu.exe\"", LaunchAtLogin.Command(@"C:\Users\A B\AppData\Local\Kannu\Kannu.exe"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(new byte[] { 2, 0, 0 }, false)]
    [InlineData(new byte[] { 3, 0, 0 }, true)]
    public void TaskManagerCanTurnItOff(byte[]? approved, bool disabled) =>
        Assert.Equal(disabled, LaunchAtLogin.DisabledInTaskManager(approved));
}
