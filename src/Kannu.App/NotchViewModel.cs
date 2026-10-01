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
    private const int MaxDots = 6;

    public ObservableCollection<SessionRow> Sessions { get; } = [];

    /// <summary>The collapsed pill's dots: the most urgent sessions first.</summary>
    public ObservableCollection<SessionRow> Dots { get; } = [];

    public string Summary { get; private set; } = Text.NoAgents;

    public bool HasSessions => Sessions.Count > 0;

    public TrafficLight Aggregate { get; private set; } = TrafficLight.Inactive;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The most urgent light changed (for the tray icon).</summary>
    public event Action<TrafficLight>? AggregateChanged;

    /// <summary>The session list was rebuilt (the expanded notch may need a new height).</summary>
    public event Action? SessionsChanged;

    /// <summary>Token totals per chat from the last reader pass (Claude only).</summary>
    public IReadOnlyDictionary<string, TurnTokens> Tokens { get; set; } = new Dictionary<string, TurnTokens>();

    public void Update(PipelineResult result)
    {
        var sessions = result.Sessions;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var countChanged = sessions.Count != Sessions.Count;

        Sessions.Clear();
        Dots.Clear();
        foreach (var session in sessions)
        {
            var row = new SessionRow(session, nowMs, Tokens.TryGetValue(session.ConversationId, out var t) ? t : null);
            Sessions.Add(row);
            if (Dots.Count < MaxDots) Dots.Add(row);
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

/// <summary>One card. Immutable: a status change rebuilds the row.</summary>
internal sealed class SessionRow
{
    private static readonly Brush Green = KannuColors.Brush(KannuColors.Green);
    private static readonly Brush Yellow = KannuColors.Brush(KannuColors.Yellow);
    private static readonly Brush Red = KannuColors.Brush(KannuColors.Red);
    private static readonly Brush Dim = KannuColors.Brush(KannuColors.Dim);

    public SessionRow(AgentSession session, long nowMs, TurnTokens? tokens)
    {
        Icon = ProviderIcons.For(session.Provider);
        // The chat's title when it has a real one, else its project: never "Untitled chat" when
        // something better is known.
        Title = AgentStateMachine.HasReliableChatName(session.ChatName) ? session.DisplayChatName
            : session.ProjectName ?? session.DisplayChatName;
        Brush = session.DisplayState.Light() switch
        {
            TrafficLight.Green => Green,
            TrafficLight.Yellow => Yellow,
            TrafficLight.Red => Red,
            _ => Dim,
        };
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
