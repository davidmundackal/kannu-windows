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

    /// <summary>
    /// The open notch's tabs. Agents is the only one on Windows so far; Usage and the other macOS tabs
    /// join as their features are ported (docs/PARITY.md).
    /// </summary>
    public IReadOnlyList<NotchTab> Tabs { get; } = [new("agents", "Agents", "\uE99A") { IsSelected = true }];

    public NotchTab SelectedTab => Tabs.FirstOrDefault(t => t.IsSelected) ?? Tabs[0];

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
            var row = new SessionRow(session, nowMs, Tokens.TryGetValue(session.ConversationId, out var t) ? t : null);
            Sessions.Add(row);
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

/// <summary>One card. Immutable: a status change rebuilds the row.</summary>
internal sealed class SessionRow
{

    public SessionRow(AgentSession session, long nowMs, TurnTokens? tokens)
    {
        Session = session;
        Icon = ProviderIcons.For(session.Provider);
        // The chat's title when it has a real one, else its project: never "Untitled chat" when
        // something better is known.
        Title = AgentStateMachine.HasReliableChatName(session.ChatName) ? session.DisplayChatName
            : session.ProjectName ?? session.DisplayChatName;
        Brush = KannuColors.BrushFor(session.DisplayState.Light());
        var parts = new List<string> { session.ProviderLabel, StateText(session) };
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
