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

public enum CaffeinateTransition
{
    /// <summary>The power request already matches the intent.</summary>
    None,

    /// <summary>Not held but should be: create and set a power request.</summary>
    Create,

    /// <summary>Held but should not be: clear and close it.</summary>
    Release,

    /// <summary>Held under the other mode's reason string: release then create, so <c>powercfg /requests</c> names the mode in force.</summary>
    Refresh,
}

/// <summary>
/// Keeping the PC awake while agents work: a port of macOS Kannu's caffeinate decision and
/// transition tables (<c>AgentTrafficLightMapper.shouldKeepAwake</c> / <c>caffeinateTransition</c>,
/// docs/CAFFEINATE.md), pinned row for row by the same tests. Smart mode holds while any agent is in
/// an active run; manual mode holds until switched off; smart wins when both are on. Only system
/// sleep is prevented: the display may still turn off, and the lid and power button still sleep.
/// </summary>
public static class Caffeinate
{
    /// <summary>A prompt nobody answers must not hold the PC awake all night: yellow counts only for its first 5 minutes.</summary>
    public const long AwaitingInputWorthyMs = AgentStateMachine.AwaitingInputStaleMs;

    public static bool ShouldKeepAwake(bool smartEnabled, bool manualEnabled, bool featureEnabled, bool hasActiveVisibleSession)
    {
        if (!featureEnabled) return false;
        return smartEnabled ? hasActiveVisibleSession : manualEnabled;
    }

    public static CaffeinateTransition Transition(bool isHeld, bool? heldModeIsSmart, bool shouldHold, bool smartNow) =>
        (isHeld, shouldHold) switch
        {
            (false, true) => CaffeinateTransition.Create,
            (true, false) => CaffeinateTransition.Release,
            (false, false) => CaffeinateTransition.None,
            _ => heldModeIsSmart == smartNow ? CaffeinateTransition.None : CaffeinateTransition.Refresh,
        };

    /// <summary>Visible, not a simulation, in an active run; a wait on the user counts only for its first 5 minutes.</summary>
    public static bool HasWorthySession(IEnumerable<AgentSession> sessions, long nowMs) => sessions.Any(s =>
        s.IsVisible && !AgentStateMachine.IsSimulation(s) && s.DisplayState.IsActiveRun()
        && (s.DisplayState != AgentLightState.AwaitingInput || nowMs - s.UpdatedAtMs <= AwaitingInputWorthyMs));

    /// <summary>The reason Windows shows in <c>powercfg /requests</c>.</summary>
    public static string Reason(bool smart) =>
        smart ? "Kannu: keeping the PC awake while an AI agent works" : "Kannu: keeping the PC awake (turned on by you)";
}
