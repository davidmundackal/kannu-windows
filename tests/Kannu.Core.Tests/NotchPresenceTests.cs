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

public class NotchPresenceTests
{
    private const long T = 1_000_000;
    private const long Hold = NotchPresence.RevealHoldMs;

    private static NotchPresence Hidden() => new();

    private static NotchPresence AlwaysVisible()
    {
        var presence = new NotchPresence();
        presence.Configure(hideUntilActivity: false, openOnHover: true, T);
        return presence;
    }

    [Fact]
    public void StartsHiddenAndStaysHiddenWithNothingHappening()
    {
        var p = Hidden();
        p.Tick(T + 60_000);
        Assert.Equal(NotchPresenceState.Hidden, p.State);
        Assert.Null(p.NextDeadlineMs);
    }

    [Fact]
    public void ActivityRevealsForSevenSecondsThenHides()
    {
        var p = Hidden();
        p.AgentActivity(T);
        Assert.Equal(NotchPresenceState.Closed, p.State);
        Assert.Equal(T + Hold, p.NextDeadlineMs);
        p.Tick(T + Hold - 1);
        Assert.Equal(NotchPresenceState.Closed, p.State);
        p.Tick(T + Hold);
        Assert.Equal(NotchPresenceState.Hidden, p.State);
        Assert.Null(p.NextDeadlineMs);
    }

    [Fact]
    public void RepeatActivityExtendsTheWindow()
    {
        var p = Hidden();
        p.AgentActivity(T);
        p.AgentActivity(T + 5_000);
        p.Tick(T + Hold);
        Assert.Equal(NotchPresenceState.Closed, p.State);
        p.Tick(T + 5_000 + Hold);
        Assert.Equal(NotchPresenceState.Hidden, p.State);
    }

    [Fact]
    public void HoverOpensAndTheWindowNeverHidesItUnderThePointer()
    {
        var p = Hidden();
        p.AgentActivity(T);
        p.PointerEntered(T + 1_000);
        Assert.Equal(NotchPresenceState.Open, p.State);
        p.Tick(T + Hold);
        Assert.Equal(NotchPresenceState.Open, p.State);
        Assert.Equal(T + 2 * Hold, p.NextDeadlineMs);
    }

    [Fact]
    public void LeavingCollapsesAfterTheGraceThenLingersThenHides()
    {
        var p = Hidden();
        p.AgentActivity(T);
        p.PointerEntered(T + 1_000);
        p.PointerLeft(T + 2_000);
        Assert.Equal(NotchPresenceState.Open, p.State);
        p.Tick(T + 2_000 + NotchPresence.CollapseGraceMs);
        Assert.Equal(NotchPresenceState.Closed, p.State);
        // Closing restarts the reveal window.
        p.Tick(T + 2_000 + NotchPresence.CollapseGraceMs + Hold - 1);
        Assert.Equal(NotchPresenceState.Closed, p.State);
        p.Tick(T + 2_000 + NotchPresence.CollapseGraceMs + Hold);
        Assert.Equal(NotchPresenceState.Hidden, p.State);
    }

    [Fact]
    public void ComingBackWithinTheGraceKeepsItOpen()
    {
        var p = Hidden();
        p.AgentActivity(T);
        p.PointerEntered(T + 1_000);
        p.PointerLeft(T + 2_000);
        p.PointerEntered(T + 2_100);
        p.Tick(T + 2_000 + NotchPresence.CollapseGraceMs);
        Assert.Equal(NotchPresenceState.Open, p.State);
    }

    [Fact]
    public void AHiddenNotchCannotBeEntered()
    {
        var p = Hidden();
        p.PointerEntered(T);
        Assert.Equal(NotchPresenceState.Hidden, p.State);
    }

    [Fact]
    public void TrayOpensAndClosesAfterThreeSecondsWhenNotHovered()
    {
        var p = Hidden();
        p.ToggleFromTray(T);
        Assert.Equal(NotchPresenceState.Open, p.State);
        Assert.Equal(T + NotchPresence.TrayAutoCloseMs, p.NextDeadlineMs);
        p.Tick(T + NotchPresence.TrayAutoCloseMs);
        // No reveal window was running, so it goes straight back out of view.
        Assert.Equal(NotchPresenceState.Hidden, p.State);
    }

    [Fact]
    public void HoveringCancelsTheTrayAutoClose()
    {
        var p = Hidden();
        p.ToggleFromTray(T);
        p.PointerEntered(T + 1_000);
        p.Tick(T + NotchPresence.TrayAutoCloseMs);
        Assert.Equal(NotchPresenceState.Open, p.State);
    }

    [Fact]
    public void AClickOutsideClosesAtOnceEvenWhileHovered()
    {
        var p = Hidden();
        p.ToggleFromTray(T);
        p.PointerEntered(T + 500);
        p.ClickedOutside(T + 600);
        Assert.Equal(NotchPresenceState.Hidden, p.State);

        var closed = Hidden();
        closed.ClickedOutside(T); // nothing open: nothing happens
        Assert.Equal(NotchPresenceState.Hidden, closed.State);
    }

    [Fact]
    public void ASecondTrayClickCloses()
    {
        var p = Hidden();
        p.ToggleFromTray(T);
        p.ToggleFromTray(T + 500);
        Assert.Equal(NotchPresenceState.Hidden, p.State);
        Assert.Null(p.NextDeadlineMs);
    }

    [Fact]
    public void TopEdgeDwellRevealsAndOpens()
    {
        var p = Hidden();
        p.TopEdgeDwell(T);
        Assert.Equal(NotchPresenceState.Open, p.State);
        p.PointerLeft(T + 1_000);
        p.Tick(T + 1_000 + NotchPresence.CollapseGraceMs);
        Assert.Equal(NotchPresenceState.Closed, p.State);
    }

    [Fact]
    public void WithoutOpenOnHoverHoverOnlyKeepsItOut()
    {
        var p = new NotchPresence();
        p.Configure(hideUntilActivity: true, openOnHover: false, T);
        p.AgentActivity(T);
        p.PointerEntered(T + 1_000);
        Assert.Equal(NotchPresenceState.Closed, p.State);
        p.Tick(T + Hold);
        Assert.Equal(NotchPresenceState.Closed, p.State);
    }

    [Fact]
    public void AlwaysVisibleIsNeverHidden()
    {
        var p = AlwaysVisible();
        Assert.Equal(NotchPresenceState.Closed, p.State);
        p.AgentActivity(T);
        Assert.Null(p.NextDeadlineMs);
        p.ToggleFromTray(T);
        p.Tick(T + NotchPresence.TrayAutoCloseMs);
        Assert.Equal(NotchPresenceState.Closed, p.State);
        p.PointerEntered(T + 10_000);
        p.PointerLeft(T + 11_000);
        p.Tick(T + 11_000 + NotchPresence.CollapseGraceMs + 60_000);
        Assert.Equal(NotchPresenceState.Closed, p.State);
    }

    [Fact]
    public void SwitchingToHiddenDropsItOutOfView()
    {
        var p = AlwaysVisible();
        p.Configure(hideUntilActivity: true, openOnHover: true, T + 1);
        Assert.Equal(NotchPresenceState.Hidden, p.State);
    }
}

public class AgentActivityTests
{
    private static AgentSession S(string id, AgentLightState state, int tools = 0) =>
        Sessions.Make(conversation: id, display: state, turn: tools == 0 ? null : new HookTurn(1, null, tools, null, null));

    [Fact]
    public void ANewLitChatReveals() =>
        Assert.True(AgentActivity.IsRevealWorthy([], [S("a", AgentLightState.Thinking)]));

    [Theory]
    [InlineData(AgentLightState.Executing, AgentLightState.AwaitingInput)]
    [InlineData(AgentLightState.AwaitingInput, AgentLightState.Executing)]
    [InlineData(AgentLightState.Executing, AgentLightState.Stopped)]
    [InlineData(AgentLightState.Inactive, AgentLightState.Thinking)]
    public void ALightChangeReveals(AgentLightState from, AgentLightState to) =>
        Assert.True(AgentActivity.IsRevealWorthy([S("a", from)], [S("a", to)]));

    [Theory]
    [InlineData(AgentLightState.Thinking, AgentLightState.Executing)]
    [InlineData(AgentLightState.Stopped, AgentLightState.Inactive)]
    [InlineData(AgentLightState.Executing, AgentLightState.Inactive)]
    public void SameColourOrDimmingDoesNot(AgentLightState from, AgentLightState to) =>
        Assert.False(AgentActivity.IsRevealWorthy([S("a", from)], [S("a", to)]));

    [Fact]
    public void ToolCallsAndChatsGoingAwayDoNot()
    {
        Assert.False(AgentActivity.IsRevealWorthy([S("a", AgentLightState.Executing, tools: 1)], [S("a", AgentLightState.Executing, tools: 9)]));
        Assert.False(AgentActivity.IsRevealWorthy([S("a", AgentLightState.Executing), S("b", AgentLightState.Stopped)], [S("a", AgentLightState.Executing)]));
        Assert.False(AgentActivity.IsRevealWorthy([], [S("a", AgentLightState.Inactive)]));
    }
}

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kannu-settings-" + Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void AMissingFileReadsAsTheDefaults()
    {
        var settings = AppSettings.Load(File);
        Assert.Equal(new AppSettings(), settings);
        Assert.Equal(NotchStyle.Notch, settings.NotchStyle);
        Assert.True(settings.HideUntilActivity);
        Assert.False(settings.RevealOnTopEdge);
        Assert.True(settings.OpenOnHover);
        Assert.True(settings.HideInFullscreen);
        Assert.False(settings.ShortcutsEnabled);
        Assert.Equal(NotchDisplay.Primary, settings.Display);
        Assert.True(TermsOfUse.NeedsAcceptance(settings));
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var settings = TermsOfUse.Accepted(new AppSettings
        {
            NotchStyle = NotchStyle.FloatingPill,
            HideUntilActivity = false,
            RevealOnTopEdge = true,
            OpenOnHover = false,
            LaunchAtLoginInitialized = true,
            CaffeinateSmart = true,
            LightStyle = LightStyle.Minimal,
            LightColors = new LightColors(PaletteColor.Blue, PaletteColor.Orange, PaletteColor.Pink),
            SkinPath = @"C:\skins\a.png",
            SkinScrim = 0.4,
            CaffeinateManual = true,
            LastOfferedReport = "crash-20261002T080000Z.txt",
            PushEnabled = true,
            PushProvider = PushProvider.Webhook,
            NtfyServer = "https://ntfy.example.com",
            PushOnInactive = true,
            WaitReminderMinutes = 10,
            ToastsEnabled = true,
            Display = NotchDisplay.Chosen,
            DisplayDevice = @"\\.\DISPLAY2",
            HideFromCapture = true,
            HideInFullscreen = false,
            ShortcutsEnabled = true,
            OnboardingDone = true,
            DetectHiddenText = false,
            WarnAgentAboutHiddenText = true,
            DetectSecrets = false,
            DetectSensitivePaths = false,
            WatchMcpServers = false,
            EnforceAgentPolicy = true,
            PushHighFindings = false,
            PushMediumFindings = true,
        }, new DateTimeOffset(2026, 10, 2, 8, 30, 0, TimeSpan.Zero));
        settings.Save(File);
        var loaded = AppSettings.Load(File);
        Assert.Equal(settings, loaded);
        Assert.Equal("2026-10-02T08:30:00Z", loaded.TermsAcceptedAt);
        Assert.False(TermsOfUse.NeedsAcceptance(loaded));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2]")]
    [InlineData("")]
    public void ACorruptFileReadsAsTheDefaults(string text)
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, text);
        Assert.Equal(new AppSettings(), AppSettings.Load(File));
    }

    [Fact]
    public void UnknownOrMistypedValuesFallBackOneByOne()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, """{"notchStyle":"triangle","hideUntilActivity":"yes","revealOnTopEdge":true,"termsAcceptedVersion":"1"}""");
        var loaded = AppSettings.Load(File);
        Assert.Equal(NotchStyle.Notch, loaded.NotchStyle);
        Assert.True(loaded.HideUntilActivity);
        Assert.True(loaded.RevealOnTopEdge);
        Assert.Null(loaded.TermsAcceptedVersion);
    }
}

public class TermsOfUseTests
{
    private static string RepoFile(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Directory.Build.props"))) dir = Path.GetDirectoryName(dir)!;
        return Path.Combine(dir, name);
    }

    [Fact]
    public void TheBundledTermsStateTheCurrentVersion() =>
        Assert.Contains($"Version {TermsOfUse.CurrentVersion} ·", File.ReadAllText(RepoFile("TERMS.md")));

    [Fact]
    public void AnOlderAcceptanceAsksAgain() =>
        Assert.True(TermsOfUse.NeedsAcceptance(new AppSettings { TermsAcceptedVersion = TermsOfUse.CurrentVersion - 1 }));

    [Fact]
    public void MarkdownBlocksAndBold()
    {
        var blocks = MarkdownLite.Parse("# Title\n\nFirst line\nsecond line with **bold** text.\n\n**1. Heading**\n\n- one\n- **two**\n");
        Assert.Collection(blocks,
            b =>
            {
                var h = Assert.IsType<MarkdownLite.Heading>(b);
                Assert.Equal(1, h.Level);
                Assert.Equal([new MarkdownLite.Span("Title", false)], h.Spans);
            },
            b =>
            {
                var p = Assert.IsType<MarkdownLite.Paragraph>(b);
                Assert.Equal(["First line second line with ", "bold", " text."], p.Spans.Select(s => s.Text));
                Assert.True(p.Spans[1].Bold);
            },
            b => Assert.True(Assert.IsType<MarkdownLite.Paragraph>(b).Spans.Single().Bold),
            b => Assert.Equal("one", Assert.IsType<MarkdownLite.Bullet>(b).Spans.Single().Text),
            b => Assert.True(Assert.IsType<MarkdownLite.Bullet>(b).Spans.Single().Bold));
    }

    [Fact]
    public void AnUnmatchedMarkerStaysText() =>
        Assert.Equal([new MarkdownLite.Span("a **b", false)], MarkdownLite.Spans("a **b"));

    [Fact]
    public void TheWholeTermsFileParsesWithNothingLost()
    {
        var text = File.ReadAllText(RepoFile("TERMS.md"));
        var blocks = MarkdownLite.Parse(text);
        Assert.IsType<MarkdownLite.Heading>(blocks[0]);
        Assert.Contains(blocks, b => b is MarkdownLite.Bullet);
        Assert.DoesNotContain(blocks.SelectMany(b => b.Spans), s => s.Text.Contains("**"));
    }
}
