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

public class StateMergeTests
{
    private static StatusRecord Existing(RawState state, string hookEvent, long ts) =>
        new() { State = state.ToWire(), Ts = ts, Provider = "claude", HookEvent = hookEvent };

    [Fact]
    public void NoExistingFileTakesTheIncomingState()
    {
        Assert.Equal((RawState.Executing, 1000L), StateMerge.Apply(null, RawState.Executing, "PreToolUse", 1000));
    }

    [Fact]
    public void ParallelGroupCannotDowngradeYellowToGreen()
    {
        var existing = Existing(RawState.AwaitingInput, "PreToolUse", 10_000);
        Assert.Equal((RawState.AwaitingInput, 10_000L), StateMerge.Apply(existing, RawState.Executing, "PreToolUse", 10_500));
    }

    [Fact]
    public void PermissionRequestYellowIsCarriedAcrossEventsInTheWindow()
    {
        var existing = Existing(RawState.AwaitingInput, "PermissionRequest", 10_000);
        Assert.Equal(RawState.AwaitingInput, StateMerge.Apply(existing, RawState.Executing, "PreToolUse", 11_000).State);
    }

    [Fact]
    public void ConsecutiveDifferentEventsAreNotReArbitrated()
    {
        var existing = Existing(RawState.Stopped, "Stop", 10_000);
        Assert.Equal((RawState.Thinking, 10_100L), StateMerge.Apply(existing, RawState.Thinking, "UserPromptSubmit", 10_100));
    }

    [Fact]
    public void OutsideTheWindowTheIncomingStateWins()
    {
        var existing = Existing(RawState.AwaitingInput, "PermissionRequest", 10_000);
        Assert.Equal(RawState.Thinking, StateMerge.Apply(existing, RawState.Thinking, "PostToolUse", 12_001).State);
    }

    [Fact]
    public void KeptStateKeepsItsOriginalClockSoTheWindowCannotRenewItself()
    {
        var existing = Existing(RawState.AwaitingInput, "PreToolUse", 10_000);
        var (_, ts) = StateMerge.Apply(existing, RawState.Executing, "PreToolUse", 11_900);
        Assert.Equal(10_000L, ts);
        Assert.Equal(RawState.Executing, StateMerge.Apply(Existing(RawState.AwaitingInput, "PreToolUse", ts), RawState.Executing, "PreToolUse", 12_100).State);
    }

    [Fact]
    public void FutureTimestampEarnsNoCarry()
    {
        var existing = Existing(RawState.AwaitingInput, "PreToolUse", 99_999_999);
        Assert.Equal(RawState.Executing, StateMerge.Apply(existing, RawState.Executing, "PreToolUse", 1_000).State);
    }

    [Fact]
    public void GarbageExistingStateIsIgnored()
    {
        var existing = new StatusRecord { State = "💥", Ts = 1000, HookEvent = "PreToolUse" };
        Assert.Equal(RawState.Executing, StateMerge.Apply(existing, RawState.Executing, "PreToolUse", 1100).State);
    }
}

public class RawStateWireTests
{
    [Theory]
    [InlineData("stopped", RawState.Stopped)]
    [InlineData("AWAITING_INPUT", RawState.AwaitingInput)]
    [InlineData("awaiting", RawState.AwaitingInput)]
    [InlineData("completed", RawState.Stopped)]
    [InlineData("nonsense", null)]
    [InlineData(null, null)]
    public void WireParsingAcceptsMacOSSpellings(string? wire, RawState? expected)
    {
        Assert.Equal(expected, RawStateWire.Parse(wire));
    }

    [Fact]
    public void EveryStateRoundTripsThroughTheWire()
    {
        foreach (var state in Enum.GetValues<RawState>()) Assert.Equal(state, RawStateWire.Parse(state.ToWire()));
    }
}
