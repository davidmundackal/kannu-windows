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

/// <summary>Ports of macOS CaffeinateDecisionTests: the decision and transition tables, row for row.</summary>
public class CaffeinateTests
{
    [Theory]
    // feature, smart, manual, active session -> hold
    [InlineData(false, true, true, true, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, false, true, false, true)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, false, false, true, false)]
    [InlineData(true, false, false, false, false)]
    public void DecisionTable(bool feature, bool smart, bool manual, bool active, bool hold) =>
        Assert.Equal(hold, Caffeinate.ShouldKeepAwake(smart, manual, feature, active));

    [Theory]
    [InlineData(false, null, true, true, CaffeinateTransition.Create)]
    [InlineData(false, null, true, false, CaffeinateTransition.Create)]
    [InlineData(true, true, false, true, CaffeinateTransition.Release)]
    [InlineData(true, false, false, false, CaffeinateTransition.Release)]
    [InlineData(true, true, true, true, CaffeinateTransition.None)]
    [InlineData(true, false, true, false, CaffeinateTransition.None)]
    [InlineData(true, true, true, false, CaffeinateTransition.Refresh)]
    [InlineData(true, false, true, true, CaffeinateTransition.Refresh)]
    [InlineData(false, null, false, true, CaffeinateTransition.None)]
    public void TransitionTable(bool held, bool? heldSmart, bool shouldHold, bool smartNow, CaffeinateTransition expected) =>
        Assert.Equal(expected, Caffeinate.Transition(held, heldSmart, shouldHold, smartNow));

    private const long Now = 10_000_000;

    [Theory]
    [InlineData(AgentLightState.Executing, Now - 3_600_000, true, true)]
    [InlineData(AgentLightState.Thinking, Now, true, true)]
    [InlineData(AgentLightState.AwaitingInput, Now - 299_000, true, true)]
    [InlineData(AgentLightState.AwaitingInput, Now - 301_000, true, false)]
    [InlineData(AgentLightState.Stopped, Now, true, false)]
    [InlineData(AgentLightState.Inactive, Now, true, false)]
    [InlineData(AgentLightState.Executing, Now, false, false)]
    public void WhatCountsAsWork(AgentLightState state, long updated, bool visible, bool worthy) =>
        Assert.Equal(worthy, Caffeinate.HasWorthySession([Sessions.Make(display: state, updatedAt: updated, visible: visible)], Now));
}
