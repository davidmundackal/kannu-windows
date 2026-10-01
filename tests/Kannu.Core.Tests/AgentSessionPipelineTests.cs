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

public class AgentSessionPipelineTests
{
    private const long T0 = 1_790_000_000_000;

    private static HookFile File(string id, string state, long ts, string provider = "claude", string? parent = null) =>
        new($"/s/{provider}-{id}.json", $"{provider}-{id}", new StatusRecord { State = state, Ts = ts, Provider = provider, ParentId = parent }, DateTime.UnixEpoch);

    [Fact]
    public void RedFlashesThenDimsThenStaysListedForTheRetentionWindow()
    {
        var pipeline = new AgentSessionPipeline(new AgentTimings(), "/home/u");
        var stopped = File("c", "stopped", T0);

        Assert.Equal(AgentLightState.Stopped, pipeline.Update([stopped], T0 + 1_000).Sessions.Single().DisplayState);
        Assert.Equal(AgentLightState.Stopped, pipeline.Update([stopped], T0 + 9_000).Light);

        // Past collapse + inactive the file resolves invisible; the ended chat stays listed, dim.
        var dimmed = pipeline.Update([stopped], T0 + 11_000);
        Assert.Equal(AgentLightState.Inactive, dimmed.Sessions.Single().DisplayState);
        Assert.Equal(AgentLightState.Inactive, dimmed.Light);

        Assert.Empty(pipeline.Update([stopped], T0 + 11_000 + AgentStateMachine.EndedChatRetentionMs).Sessions);
    }

    [Fact]
    public void SessionEndKeepsTheRedChatListedDim()
    {
        var pipeline = new AgentSessionPipeline(new AgentTimings(), "/home/u");
        pipeline.Update([File("c", "stopped", T0)], T0 + 1_000);
        var afterEnd = pipeline.Update([], T0 + 2_000);
        Assert.Equal(AgentLightState.Inactive, afterEnd.Sessions.Single().DisplayState);
    }

    [Fact]
    public void ARunWithoutATurnTicksFromWhenKannuSawItStart()
    {
        var pipeline = new AgentSessionPipeline(new AgentTimings(), "/home/u");
        pipeline.Update([File("c", "idle", T0)], T0);
        var running = pipeline.Update([File("c", "executing", T0 + 5_000)], T0 + 5_000);
        Assert.Equal(T0 + 5_000, running.Sessions.Single().ExecutionStartedAtMs);
        var later = pipeline.Update([File("c", "thinking", T0 + 30_000)], T0 + 30_000);
        Assert.Equal(T0 + 5_000, later.Sessions.Single().ExecutionStartedAtMs);
    }

    [Fact]
    public void CardsAreOrderedAsOnMacOS()
    {
        var pipeline = new AgentSessionPipeline(new AgentTimings(), "/home/u");
        var result = pipeline.Update([
            File("a", "executing", T0),
            File("b", "awaiting_input", T0, "codex"),
            File("c", "stopped", T0, "cursor"),
        ], T0 + 1_000);
        Assert.Equal(["c", "b", "a"], result.Sessions.Select(s => s.ConversationId));
        Assert.Equal(AgentLightState.Stopped, result.Light);
    }

    [Fact]
    public void AFoldedSubagentIsNeverListed()
    {
        var pipeline = new AgentSessionPipeline(new AgentTimings(), "/home/u");
        var result = pipeline.Update([File("parent", "thinking", T0), File("sub", "awaiting_input", T0, parent: "parent")], T0 + 1_000);
        var card = Assert.Single(result.Sessions);
        Assert.Equal("parent", card.ConversationId);
        Assert.Equal(AgentLightState.AwaitingInput, card.DisplayState);
    }
}
