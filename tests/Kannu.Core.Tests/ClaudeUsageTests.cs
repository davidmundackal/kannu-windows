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

using System.Text.Json.Nodes;
using Kannu.Core;
using Xunit;

namespace Kannu.Core.Tests;

public sealed class ClaudeUsageTests : IDisposable
{
    private const long Now = 1_790_000_000_000;
    private static readonly long ResetS = Now / 1000 + 3600;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "kannu-usage-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static string StatuslineInput() => $$$"""
        {"session_id":"s1","model":{"display_name":"Opus"},
         "rate_limits":{"five_hour":{"used_percentage":32.4,"resets_at":{{{ResetS}}}},
                        "seven_day":{"used_percentage":41,"resets_at":{{{ResetS + 86400}}},"severity":"warning"},
                        "model_scoped":[{"utilization":68,"display_name":"Fable","resets_at":"2026-09-30T10:00:00Z"}]}}
        """;

    [Fact]
    public void TheStatuslineSavesEveryWindowAndPrintsASummary()
    {
        var (json, line) = ClaudeUsage.FromStatusline(StatuslineInput(), Now, TimeZoneInfo.Utc)!.Value;
        Assert.StartsWith("Opus | 5h 32%@", line);
        Assert.Contains("| 7d 41%@", line);

        var windows = ClaudeUsage.ParseStatuslineFile(json);
        Assert.Equal(3, windows.Count);
        var five = windows.Single(w => w.Key == "five_hour");
        Assert.Equal(32.4, five.Percent);
        Assert.Equal(ResetS * 1000, five.ResetsAtMs);
        Assert.Equal(Now, five.ObservedAtMs);
        Assert.Equal("warning", windows.Single(w => w.Key == "seven_day").Severity);
        var fable = windows.Single(w => w.Key == "model_scoped:Fable");
        Assert.Equal("Weekly · Fable", fable.Title);
        Assert.Equal("Fable weekly", fable.AlertLabel);
    }

    [Theory]
    [InlineData("""{"model":{"display_name":"Opus"}}""")]
    [InlineData("not json")]
    [InlineData("""{"rate_limits":{}}""")]
    public void NoLimitsWritesNothing(string input) => Assert.Null(ClaudeUsage.FromStatusline(input, Now, TimeZoneInfo.Utc));

    [Fact]
    public void MacOSLegacyFlatKeysStillRead()
    {
        var windows = ClaudeUsage.ParseStatuslineFile($$$"""{"ts":{{{Now}}},"five_hour_pct":12,"five_hour_resets_at":{{{ResetS}}}}""");
        Assert.Equal(12, Assert.Single(windows).Percent);
    }

    private static string Cache(string account = "a1") => $$$"""
        {"oauthAccount":{"accountUuid":"a1"},
         "cachedUsageUtilization":{"fetchedAtMs":{{{Now - 60_000}}},"accountUuid":"{{{account}}}",
           "utilization":{"five_hour":{"utilization":50,"resets_at":"2026-09-21T20:00:00.123456Z"},
                          "seven_day":{"utilization":20},
                          "limits":[{"kind":"session","severity":"critical"},
                                    {"kind":"weekly_scoped","percent":70,"scope":{"model":{"display_name":"Fable"} } }]} } }
        """;

    [Fact]
    public void TheCacheIsReadWithSeverityBorrowedFromLimits()
    {
        var windows = ClaudeUsage.ParseCache(Cache());
        Assert.Equal(3, windows.Count);
        var five = windows.Single(w => w.Key == "five_hour");
        Assert.Equal("critical", five.Severity);
        Assert.Equal(DateTimeOffset.Parse("2026-09-21T20:00:00.123456Z").ToUnixTimeMilliseconds(), five.ResetsAtMs);
        Assert.Equal(70, windows.Single(w => w.Key == "model_scoped:Fable").Percent);
    }

    [Fact]
    public void AnotherLoginsCacheIsIgnored() => Assert.Empty(ClaudeUsage.ParseCache(Cache("someone-else")));

    [Fact]
    public void TheNewestReadingWinsAndExpiredWindowsGo()
    {
        var old = new UsageWindow("five_hour", null, 10, Now + 1000, null, Now - 5000);
        var fresh = new UsageWindow("five_hour", null, 20, Now + 1000, null, Now - 1000);
        var expired = new UsageWindow("seven_day", null, 90, Now - 1, null, Now);
        var scoped = new UsageWindow("model_scoped:Fable", "Fable", 5, null, null, Now);
        var merged = ClaudeUsage.Merge(Now, [old, expired], [scoped, fresh]);
        Assert.Equal(["five_hour", "model_scoped:Fable"], merged.Select(w => w.Key));
        Assert.Equal(20, merged[0].Percent);
    }

    [Fact]
    public void ForecastNeedsSpreadOutReadings()
    {
        var window = new UsageWindow("five_hour", null, 40, Now + 3 * 3600_000, null, Now);
        IReadOnlyList<UsageForecast.Sample> samples = [];
        samples = UsageForecast.Admitting(new(Now - 30 * 60_000, 10, window.ResetsAtMs), samples);
        samples = UsageForecast.Admitting(new(Now - 30 * 60_000 + 60_000, 11, window.ResetsAtMs), samples); // too soon: skipped
        Assert.Single(samples);
        Assert.IsType<UsageForecast.Outlook.InsufficientData>(UsageForecast.For(samples, window, Now));

        samples = UsageForecast.Admitting(new(Now - 15 * 60_000, 25, window.ResetsAtMs), samples);
        samples = UsageForecast.Admitting(new(Now, 40, window.ResetsAtMs), samples);
        // 60 %/h: full in one hour, before the reset in three.
        var outlook = Assert.IsType<UsageForecast.Outlook.HitsLimit>(UsageForecast.For(samples, window, Now));
        Assert.InRange(outlook.AtMs, Now + 59 * 60_000, Now + 61 * 60_000);
        Assert.Equal(("At this pace: full by X", true), UsageForecast.Caption(outlook, window.ResetsAtMs, _ => "X"));
    }

    [Fact]
    public void ARolloverStartsTheSeriesAgain()
    {
        IReadOnlyList<UsageForecast.Sample> samples = [new(Now - 600_000, 80, Now + 1000)];
        Assert.Single(UsageForecast.Admitting(new(Now, 2, Now + 1000), samples));
        Assert.Single(UsageForecast.Admitting(new(Now, 81, Now + 5_000_000), samples));
    }

    [Fact]
    public void AtTheLimitAndCountdowns()
    {
        var full = new UsageWindow("seven_day", null, 99.6, Now + 3600_000, null, Now);
        Assert.IsType<UsageForecast.Outlook.AtLimit>(UsageForecast.For([], full, Now));
        Assert.Equal("2d 22h", UsageForecast.Countdown((2 * 24 + 22) * 3600_000L + 37 * 60_000));
        Assert.Equal("4h 56m", UsageForecast.Countdown(4 * 3600_000L + 56 * 60_000));
        Assert.Equal("1m", UsageForecast.Countdown(10_000));
    }

    [Fact]
    public void NearLimitOncePerWindowInstance()
    {
        var near = new UsageWindow("five_hour", null, 96, Now + 1000, null, Now);
        Assert.True(UsageAlerts.IsNearLimit(near, Now));
        Assert.True(UsageAlerts.IsNearLimit(near with { Percent = 50, Severity = "critical" }, Now));
        Assert.False(UsageAlerts.IsNearLimit(near with { Percent = 94 }, Now));
        Assert.NotEqual(UsageAlerts.Key(near), UsageAlerts.Key(near with { ResetsAtMs = Now + 5 * 3600_000 }));
        Assert.Equal("Claude 5-hour limit at 96%", UsageAlerts.Payload(near, _ => "X").Title);
    }

    // ---- Install ----

    private AgentHookInstaller Installer()
    {
        var hook = Path.Combine(_root, "bin", "kannu-hook.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, "");
        return new AgentHookInstaller(new AgentHookLayout(Path.Combine(_root, "home")), hook);
    }

    [Fact]
    public void TheUsersStatuslineIsChainedAndRestored()
    {
        var installer = Installer();
        var settings = installer.Layout.ClaudeSettings;
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, """{"statusLine":{"type":"command","command":"my-status --fancy"},"model":"opus"}""");

        installer.InstallUsageStatusline();
        Assert.True(installer.IsUsageStatuslineInstalled());
        var root = JsonNode.Parse(File.ReadAllText(settings))!;
        Assert.EndsWith("kannu-hook.exe statusline", (string?)root["statusLine"]!["command"]);
        Assert.Equal("opus", (string?)root["model"]);
        Assert.Equal("my-status --fancy", File.ReadAllText(installer.StatuslineChainPath).Trim());

        installer.InstallUsageStatusline(); // again: the chain is not overwritten with Kannu's own command
        Assert.Equal("my-status --fancy", File.ReadAllText(installer.StatuslineChainPath).Trim());

        installer.UninstallUsageStatusline();
        Assert.Equal("my-status --fancy", (string?)JsonNode.Parse(File.ReadAllText(settings))!["statusLine"]!["command"]);
        Assert.False(File.Exists(installer.StatuslineChainPath));
        Assert.False(installer.IsUsageStatuslineInstalled());
    }

    [Fact]
    public void WithoutAStatuslineBeforeTheKeyIsRemoved()
    {
        var installer = Installer();
        installer.InstallUsageStatusline();
        installer.UninstallUsageStatusline();
        Assert.Null(JsonNode.Parse(File.ReadAllText(installer.Layout.ClaudeSettings))!["statusLine"]);
    }

    [Fact]
    public void TheRunnerWritesTheFileAndPrintsTheSummary()
    {
        var dir = Path.Combine(_root, "status");
        var line = StatuslineRunner.Run(StatuslineInput(), dir, Path.Combine(_root, "no-chain.txt"), Now, TimeZoneInfo.Utc);
        Assert.StartsWith("Opus | 5h 32%", line);
        Assert.Equal(3, ClaudeUsage.ParseStatuslineFile(File.ReadAllText(Path.Combine(dir, ClaudeUsage.FileName))).Count);
        // No "state": the session reader never mistakes it for a chat.
        Assert.Equal("", StatusStore.Read(Path.Combine(dir, ClaudeUsage.FileName))?.State ?? "");
    }
}
