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

    public void Update(IReadOnlyList<AgentSession> sessions)
    {
        var countChanged = sessions.Count != Sessions.Count;

        Sessions.Clear();
        Dots.Clear();
        foreach (var session in sessions)
        {
            var row = new SessionRow(session);
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

        // Sessions arrive most urgent first, so the first one is the aggregate.
        var aggregate = sessions.Count > 0 ? sessions[0].Light : TrafficLight.Inactive;
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
        var needs = sessions.Count(s => s.Light == TrafficLight.Yellow);
        var working = sessions.Count(s => s.Light == TrafficLight.Green);
        var done = sessions.Count(s => s.Light == TrafficLight.Red);
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

/// <summary>One session row. Immutable: a status change rebuilds the row.</summary>
internal sealed class SessionRow
{
    private static readonly Brush Green = Frozen(0x34, 0xC7, 0x59);
    private static readonly Brush Yellow = Frozen(0xFF, 0xCC, 0x00);
    private static readonly Brush Red = Frozen(0xFF, 0x3B, 0x30);
    private static readonly Brush Dim = Frozen(0x6E, 0x6E, 0x73);

    public SessionRow(AgentSession session)
    {
        Title = session.Title;
        Brush = session.Light switch
        {
            TrafficLight.Green => Green,
            TrafficLight.Yellow => Yellow,
            TrafficLight.Red => Red,
            _ => Dim,
        };
        Detail = $"{ProviderName(session.Record.Provider)} · {StateText(session)} · {AgeText(session.AgeMs)}";
    }

    public string Title { get; }
    public string Detail { get; }
    public Brush Brush { get; }

    private static string StateText(AgentSession session) => session.Light switch
    {
        TrafficLight.Yellow => "Needs you",
        TrafficLight.Green => RawStateWire.Parse(session.Record.State) == RawState.Executing ? "Running" : "Thinking",
        TrafficLight.Red => RawStateWire.Parse(session.Record.State) == RawState.QuotaExceeded ? "Quota hit" : "Done",
        _ => "Idle",
    };

    private static string ProviderName(string provider) => provider switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        "cursor" => "Cursor",
        "" => "Agent",
        _ => provider,
    };

    private static string AgeText(long ageMs) => ageMs switch
    {
        < 10_000 => "now",
        < 60_000 => $"{ageMs / 1000}s",
        < 3_600_000 => $"{ageMs / 60_000}m",
        _ => $"{ageMs / 3_600_000}h",
    };

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
