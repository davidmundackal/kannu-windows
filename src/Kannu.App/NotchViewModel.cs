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

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Media;
using Kannu.Core;

namespace Kannu.App;

/// <summary>What the notch shows. Fed by <see cref="StatusMonitor"/>; the view never touches disk.</summary>
internal sealed class NotchViewModel : INotifyPropertyChanged
{
    public ObservableCollection<SessionRow> Sessions { get; } = [];

    public string Summary { get; private set; } = Text.NoAgents;

    public bool HasSessions => Sessions.Count > 0;

    public TrafficLight Aggregate { get; private set; } = TrafficLight.Inactive;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The most urgent light changed (for the tray icon).</summary>
    public event Action<TrafficLight>? AggregateChanged;

    /// <summary>The aggregate light's state changed, thinking and executing told apart (for notifications).</summary>
    public event Action<AgentLightState>? StateChanged;

    private AgentLightState _state = AgentLightState.Inactive;

    /// <summary>The session list was rebuilt (the expanded notch may need a new height).</summary>
    public event Action? SessionsChanged;

    /// <summary>Every rescan's sessions, for consumers that decide on them (caffeinate).</summary>
    public event Action<IReadOnlyList<AgentSession>>? Updated;

    private bool _isCaffeinated;

    /// <summary>The PC is being kept awake: the open notch's sun button lights up.</summary>
    public bool IsCaffeinated
    {
        get => _isCaffeinated;
        set
        {
            if (_isCaffeinated == value) return;
            _isCaffeinated = value;
            PropertyChanged?.Invoke(this, new(nameof(IsCaffeinated)));
        }
    }

    /// <summary>An agent's light said something new (<see cref="AgentActivity.IsRevealWorthy"/>): reveal a hidden notch.</summary>
    public event Action? Activity;

    /// <summary>The open notch's tabs: Agents and Usage, as on macOS.</summary>
    public IReadOnlyList<NotchTab> Tabs { get; } =
    [
        new("agents", "Agents", "\uE99A") { IsSelected = true },
        new("usage", "Usage", "\uE9D2"),
    ];

    public NotchTab SelectedTab => Tabs.FirstOrDefault(t => t.IsSelected) ?? Tabs[0];

    public bool IsUsageTab => SelectedTab.Id == "usage";

    /// <summary>The selected tab changed (the open notch may need a new height).</summary>
    public event Action? TabChanged;

    public NotchViewModel()
    {
        foreach (var tab in Tabs)
        {
            tab.PropertyChanged += (_, _) =>
            {
                if (!tab.IsSelected) return;
                PropertyChanged?.Invoke(this, new(nameof(SelectedTab)));
                PropertyChanged?.Invoke(this, new(nameof(IsUsageTab)));
                TabChanged?.Invoke();
            };
        }
    }

    /// <summary>A high security finding nobody has acknowledged: the shield on the closed notch, the card on the open one.</summary>
    public bool HasSecurityAlert => SecurityAlert is not null;

    public FindingGroup? SecurityAlert { get; private set; }

    public string SecurityAlertTitle => SecurityAlert?.Representative.Title ?? "";

    public string SecurityAlertDetail => SecurityAlert is not { } g ? ""
        : g.Representative.Summary + (VisibleFindings > 1 ? $"  ·  {VisibleFindings - 1} more in Settings" : "");

    public int VisibleFindings { get; private set; }

    public void UpdateSecurity(IReadOnlyList<FindingGroup> visible)
    {
        var alert = visible.FirstOrDefault(g => g.Severity == FindingSeverity.High);
        var changed = alert?.Id != SecurityAlert?.Id || visible.Count != VisibleFindings;
        SecurityAlert = alert;
        VisibleFindings = visible.Count;
        PropertyChanged?.Invoke(this, new(nameof(HasSecurityAlert)));
        PropertyChanged?.Invoke(this, new(nameof(SecurityAlertTitle)));
        PropertyChanged?.Invoke(this, new(nameof(SecurityAlertDetail)));
        if (changed && !IsUsageTab) TabChanged?.Invoke();
    }

    /// <summary>The Usage tab's bars, Claude's plan limits.</summary>
    public ObservableCollection<UsageBar> UsageBars { get; } = [];

    public bool HasUsage => UsageBars.Count > 0;

    /// <summary>What the Usage tab says when it has no bars.</summary>
    public string UsageHint { get; private set; } = "";

    /// <summary>Claude's live limits, for "resumes at" on a card that stopped on its quota.</summary>
    public IReadOnlyList<UsageWindow> ClaudeWindows { get; private set; } = [];

    /// <summary>The Usage tab's line for agents Kannu has no limits for (Antigravity: sessions and last activity).</summary>
    public string OtherUsage { get; private set; } = "";

    public bool HasOtherUsage => OtherUsage.Length > 0;

    public void UpdateUsage(IReadOnlyList<UsageBar> bars, string hint, IReadOnlyList<UsageWindow> windows)
    {
        ClaudeWindows = windows;
        var countChanged = bars.Count != UsageBars.Count;
        UsageBars.Clear();
        foreach (var bar in bars) UsageBars.Add(bar);
        UsageHint = hint;
        PropertyChanged?.Invoke(this, new(nameof(UsageHint)));
        PropertyChanged?.Invoke(this, new(nameof(HasUsage)));
        if (countChanged && IsUsageTab) TabChanged?.Invoke();
    }

    private IReadOnlyList<AgentSession> _previous = [];

    /// <summary>Token totals per chat from the last reader pass (Claude only).</summary>
    public IReadOnlyDictionary<string, TurnTokens> Tokens { get; set; } = new Dictionary<string, TurnTokens>();

    public void Update(PipelineResult result)
    {
        var sessions = result.Sessions;
        var reveal = AgentActivity.IsRevealWorthy(_previous, sessions);
        _previous = sessions;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var countChanged = sessions.Count != Sessions.Count;

        Sessions.Clear();
        foreach (var session in sessions)
        {
            var resume = UsageAlerts.ResumeAtMs(session.Provider, session.RawState, ClaudeWindows, nowMs);
            var row = new SessionRow(session, nowMs, Tokens.TryGetValue(session.ConversationId, out var t) ? t : null,
                resume is { } at ? UsageMonitor.Clock(at) : null);
            Sessions.Add(row);
        }

        // macOS's Antigravity card: how many sessions, and when one was last active (no limits to read).
        var antigravity = sessions.Where(s => s.Provider == "antigravity").ToList();
        var other = antigravity.Count == 0 ? ""
            : $"Antigravity · {antigravity.Count} {(antigravity.Count == 1 ? "session" : "sessions")} · last active {TurnDisplay.FormatShort(nowMs - antigravity.Max(s => s.UpdatedAtMs))} ago";
        if (other != OtherUsage)
        {
            OtherUsage = other;
            PropertyChanged?.Invoke(this, new(nameof(OtherUsage)));
            PropertyChanged?.Invoke(this, new(nameof(HasOtherUsage)));
        }

        var summary = Summarise(sessions);
        if (summary != Summary)
        {
            Summary = summary;
            PropertyChanged?.Invoke(this, new(nameof(Summary)));
        }
        if (countChanged) PropertyChanged?.Invoke(this, new(nameof(HasSessions)));

        var aggregate = result.Light.Light();
        if (aggregate != Aggregate)
        {
            Aggregate = aggregate;
            AggregateChanged?.Invoke(aggregate);
        }

        if (result.Light != _state)
        {
            _state = result.Light;
            StateChanged?.Invoke(_state);
        }

        if (countChanged) SessionsChanged?.Invoke();
        Updated?.Invoke(sessions);
        if (reveal) Activity?.Invoke();
    }

    private static string Summarise(IReadOnlyList<AgentSession> sessions)
    {
        if (sessions.Count == 0) return Text.NoAgents;
        var parts = new List<string>(3);
        var needs = sessions.Count(s => s.DisplayState == AgentLightState.AwaitingInput);
        var working = sessions.Count(s => s.DisplayState is AgentLightState.Executing or AgentLightState.Thinking);
        var done = sessions.Count(s => s.DisplayState == AgentLightState.Stopped);
        if (needs > 0) parts.Add($"{needs} needs you");
        if (working > 0) parts.Add($"{working} working");
        if (done > 0) parts.Add($"{done} done");
        return parts.Count > 0 ? string.Join(" · ", parts) : $"{sessions.Count} idle";
    }

    private static class Text
    {
        public const string NoAgents = "No agents";
    }
}

/// <summary>A tab of the open notch: an icon-only button, its name in the tooltip and the header.</summary>
internal sealed class NotchTab(string id, string label, string glyph) : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Id { get; } = id;
    public string Label { get; } = label;
    public string Glyph { get; } = glyph;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One bar on the Usage tab. Immutable: every reading rebuilds it.</summary>
internal sealed class UsageBar
{
    public const double Height = 54;

    public UsageBar(UsageWindow window, string? reset, (string Text, bool IsWarning)? caption)
    {
        Title = window.Title;
        PercentText = $"{Math.Round(window.Percent):0}%";
        var fraction = Math.Clamp(window.Percent / 100, 0, 1);
        // Never an empty-looking bar for a few percent; the label carries the exact number.
        Filled = new System.Windows.GridLength(Math.Max(fraction, 0.02), System.Windows.GridUnitType.Star);
        Empty = new System.Windows.GridLength(1 - Math.Max(fraction, 0.02), System.Windows.GridUnitType.Star);
        // Claude's own severity first, else the macOS thresholds.
        var color = window.Severity switch
        {
            "critical" => "#FFEF4444",
            "warning" => "#FFF59E0B",
            _ => fraction > 0.95 ? "#FFEF4444" : fraction > 0.9 ? "#FFF59E0B" : "#FF60A5FA",
        };
        Brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        Brush.Freeze();
        Reset = reset is null ? "" : "resets in " + reset;
        Caption = caption?.Text ?? "";
        CaptionBrush = caption is { IsWarning: true } ? Brushes.Orange : new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    }

    public string Title { get; }
    public string PercentText { get; }
    public System.Windows.GridLength Filled { get; }
    public System.Windows.GridLength Empty { get; }
    public Brush Brush { get; }
    public string Reset { get; }
    public string Caption { get; }
    public Brush CaptionBrush { get; }
}

/// <summary>One card. Immutable: a status change rebuilds the row.</summary>
internal sealed class SessionRow
{

    public SessionRow(AgentSession session, long nowMs, TurnTokens? tokens, string? resumesAt = null)
    {
        Session = session;
        Icon = ProviderIcons.For(session.Provider);
        // The chat's title when it has a real one, else its project: never "Untitled chat" when
        // something better is known.
        Title = AgentStateMachine.HasReliableChatName(session.ChatName) ? session.DisplayChatName
            : session.ProjectName ?? session.DisplayChatName;
        Brush = KannuColors.BrushFor(session.DisplayState.Light());
        var parts = new List<string> { session.ProviderLabel, StateText(session) };
        if (resumesAt is not null) parts.Add("resumes " + resumesAt);
        switch (TurnDisplay.For(session.Turn, session.ExecutionStartedAtMs, session.DisplayState, session.HasActiveRawState, session.UpdatedAtMs))
        {
            case TurnDisplay.Live live:
                parts.Add(TurnDisplay.FormatShort(nowMs - live.SinceMs));
                break;
            case TurnDisplay.Ended ended:
                parts.Add("Ran " + TurnDisplay.FormatShort(ended.DurationMs));
                break;
        }
        if (TurnDisplay.Tools(session.Turn?.ToolCalls ?? 0) is { } tools) parts.Add(tools);
        // Tokens only when they belong to this card's own request.
        if (tokens is not null && session.Turn is { } turn && tokens.StartedMs == turn.StartedMs
            && tokens.StartOffset == turn.TranscriptOffset) parts.Add(tokens.Text);
        Detail = string.Join(" · ", parts);
    }

    public AgentSession Session { get; }
    public string Title { get; }
    public string Detail { get; }
    public Brush Brush { get; }
    public ImageSource Icon { get; }

    private static string StateText(AgentSession session) => session.DisplayState switch
    {
        AgentLightState.AwaitingInput => "Needs you",
        AgentLightState.Executing => "Running",
        AgentLightState.Thinking => "Thinking",
        AgentLightState.Stopped => (session.RawState == "quota_exceeded" ? "Quota exceeded" : "Stopped") + session.RunOutcomeSuffix,
        _ => "Idle" + session.RunOutcomeSuffix,
    };
}
