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

public enum NotchPresenceState
{
    /// <summary>Slid out of view above the top edge; never blocks a click.</summary>
    Hidden,

    /// <summary>On screen and collapsed: the lights and the one-line summary.</summary>
    Closed,

    /// <summary>On screen and open: the tabs and the session list.</summary>
    Open,
}

/// <summary>
/// When the notch is on screen. Windows has no physical notch to hide a closed island behind, so by
/// default the notch is out of view and comes out the way macOS Kannu's hide-until-hover island does
/// (<c>ContentView.noteAgentActivityPulse</c> / <c>armRevealCountdown</c>): for 7 seconds after an
/// agent's light changes, for as long as the pointer is on it, and for 7 seconds after the pointer
/// leaves. The tray eye opens it (macOS: the menu-bar eye, closing after 3 seconds); unlike macOS, the
/// 3-second close is cancelled once the pointer is on the notch, so hover keeps working.
///
/// Pure and clock-free: every input carries the time, and <see cref="NextDeadlineMs"/> says when
/// <see cref="Tick"/> is next due, so the view arms one timer instead of polling.
/// </summary>
public sealed class NotchPresence
{
    /// <summary>macOS <c>notchRevealHoldSeconds</c>.</summary>
    public const long RevealHoldMs = 7_000;

    /// <summary>macOS <c>toggleNotch</c>'s close after a menu-bar click.</summary>
    public const long TrayAutoCloseMs = 3_000;

    /// <summary>A fast diagonal past the edge must not slam the open notch shut.</summary>
    public const long CollapseGraceMs = 250;

    /// <summary>macOS <c>minimumHoverDuration</c>: how long the pointer rests at the top edge.</summary>
    public const long EdgeDwellMs = 1_000;

    private bool _hideUntilActivity = true;
    private bool _openOnHover = true;
    private bool _open;
    private bool _pointerInside;
    private long? _holdUntil;
    private long? _autoCloseAt;
    private long? _collapseAt;

    public NotchPresenceState State { get; private set; } = NotchPresenceState.Hidden;

    /// <summary>When <see cref="Tick"/> must run next; null when nothing is pending.</summary>
    public long? NextDeadlineMs => Min(Min(_holdUntil, _autoCloseAt), _collapseAt);

    /// <param name="hideUntilActivity">Off: the notch is always on screen, as before.</param>
    /// <param name="openOnHover">Off: hovering keeps the notch out but does not open it.</param>
    public void Configure(bool hideUntilActivity, bool openOnHover, long nowMs)
    {
        if (_hideUntilActivity != hideUntilActivity) _holdUntil = null;
        _hideUntilActivity = hideUntilActivity;
        _openOnHover = openOnHover;
        Evaluate(nowMs);
    }

    /// <summary>An agent's light changed (<see cref="AgentActivity.IsRevealWorthy"/>): come out for 7 seconds.</summary>
    public void AgentActivity(long nowMs)
    {
        if (_hideUntilActivity) Hold(nowMs);
        Evaluate(nowMs);
    }

    public void PointerEntered(long nowMs)
    {
        if (State == NotchPresenceState.Hidden) return; // Nothing on screen to enter.
        Enter(nowMs);
    }

    /// <summary>The pointer rested at the top edge for <see cref="EdgeDwellMs"/> while the notch was hidden.</summary>
    public void TopEdgeDwell(long nowMs) => Enter(nowMs);

    public void PointerLeft(long nowMs)
    {
        if (!_pointerInside) return;
        _pointerInside = false;
        if (_open) _collapseAt = nowMs + CollapseGraceMs;
        if (_hideUntilActivity) Hold(nowMs);
        Evaluate(nowMs);
    }

    /// <summary>A click on the tray eye: open the notch, or close it when it is open.</summary>
    public void ToggleFromTray(long nowMs)
    {
        if (_open)
        {
            Close(nowMs);
        }
        else
        {
            _open = true;
            _collapseAt = null;
            _autoCloseAt = _pointerInside ? null : nowMs + TrayAutoCloseMs;
        }
        Evaluate(nowMs);
    }

    public void Tick(long nowMs)
    {
        if (!_pointerInside && (_collapseAt <= nowMs || _autoCloseAt <= nowMs)) Close(nowMs);
        if (_holdUntil <= nowMs)
        {
            // Still engaged when the window runs out: keep it out another window rather than hide
            // it under the pointer (macOS re-arms instead of collapsing).
            _holdUntil = _pointerInside || _open ? nowMs + RevealHoldMs : null;
        }
        Evaluate(nowMs);
    }

    private void Enter(long nowMs)
    {
        _pointerInside = true;
        _collapseAt = null;
        _autoCloseAt = null;
        if (_openOnHover) _open = true;
        Evaluate(nowMs);
    }

    private void Close(long nowMs)
    {
        _open = false;
        _collapseAt = null;
        _autoCloseAt = null;
        // Closing restarts a reveal window that was running (macOS): the notch lingers, then goes.
        if (_hideUntilActivity && _holdUntil is not null) Hold(nowMs);
    }

    private void Hold(long nowMs)
    {
        var until = nowMs + RevealHoldMs;
        if (_holdUntil is null || _holdUntil < until) _holdUntil = until;
    }

    private void Evaluate(long nowMs)
    {
        State = _open ? NotchPresenceState.Open
            : !_hideUntilActivity || _pointerInside || _holdUntil > nowMs ? NotchPresenceState.Closed
            : NotchPresenceState.Hidden;
    }

    private static long? Min(long? a, long? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}

/// <summary>
/// What brings the hidden notch out. macOS reveals on every session change, which keeps the island out
/// for a whole run; Windows reveals only when the light says something new: a chat appears lit, or a
/// chat's light turns green, yellow or red. Thinking and executing are both green, so moving between
/// them, a tool call, a light dimming and a chat going away do not reveal.
/// </summary>
public static class AgentActivity
{
    public static bool IsRevealWorthy(IReadOnlyList<AgentSession> previous, IReadOnlyList<AgentSession> current)
    {
        var before = new Dictionary<string, TrafficLight>();
        foreach (var session in previous) before.TryAdd(session.Id, session.DisplayState.Light());
        foreach (var session in current)
        {
            var light = session.DisplayState.Light();
            if (light == TrafficLight.Inactive) continue;
            if (!before.TryGetValue(session.Id, out var old) || old != light) return true;
        }
        return false;
    }
}
