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

public class HookSessionReaderTests
{
    private const long Now = 1_790_000_000_000;
    private static readonly AgentTimings Timings = new();

    private static HookFile File(string provider, string id, string state, long ageMs, string? parent = null, string? name = null,
        long? turnStart = null, long? turnEnd = null) =>
        new($"/s/{provider}-{id}.json", $"{provider}-{id}",
            new StatusRecord { State = state, Ts = Now - ageMs, Provider = provider, ParentId = parent, Name = name, TurnStartedMs = turnStart, TurnEndedMs = turnEnd },
            DateTime.UnixEpoch);

    private static HookSessionResult Build(params HookFile[] files) => HookSessionReader.Build(files, Timings, Now, "/home/u");

    [Fact]
    public void AFreshFileBecomesASession()
    {
        var session = Assert.Single(Build(File("codex", "abc", "executing", 1_000)).Sessions);
        Assert.Equal("abc", session.ConversationId);
        Assert.Equal("codex-abc", session.Id);
        Assert.Equal(AgentLightState.Executing, session.DisplayState);
    }

    [Fact]
    public void AFileOlderThanTheStaleCapIsDeletedNotShown()
    {
        var result = Build(File("codex", "abc", "executing", Timings.StaleMs + 1));
        Assert.Empty(result.Sessions);
        Assert.Single(result.StaleFiles);
    }

    [Fact]
    public void ACorroboratedClaudePromptOutlivesTheStaleCap()
    {
        var file = File("claude", "c", "awaiting_input", 3_600_000);
        var result = HookSessionReader.Build([file], Timings, Now, "/home/u",
            new Dictionary<string, ClaudeTailState> { ["c"] = ClaudeTailState.ToolInFlight });
        var session = Assert.Single(result.Sessions);
        Assert.Equal(AgentLightState.AwaitingInput, session.DisplayState);
        Assert.Empty(result.StaleFiles);
    }

    [Fact]
    public void AHookOnlyYellowHoldsUntilTheStaleCap()
    {
        Assert.Equal(AgentLightState.AwaitingInput, Build(File("codex", "x", "awaiting_input", 600_000)).Sessions[0].DisplayState);
        Assert.Equal(AgentLightState.Inactive, Build(File("claude", "y", "awaiting_input", 600_000)).Sessions[0].DisplayState);
    }

    [Fact]
    public void ASilentChatNamedByAFreshSubagentIsKept()
    {
        var parent = File("claude", "parent", "executing", Timings.StaleMs + 60_000);
        var sub = File("claude", "sub", "executing", 1_000, parent: "parent");
        var result = Build(parent, sub);
        Assert.Empty(result.StaleFiles);
        Assert.Equal(["parent"], result.Sessions.Select(s => s.ConversationId));
        Assert.Equal(["sub"], result.FoldedSubagentIds);
    }

    [Fact]
    public void SimulationIdsAreDeleted() => Assert.Single(Build(File("claude", "default", "thinking", 1)).StaleFiles);

    [Fact]
    public void AToolNameIsNotAChatName() => Assert.Null(Build(File("cursor", "c", "executing", 1, name: "Shell")).Sessions[0].ChatName);

    [Fact]
    public void ATurnIsReadAndValidated()
    {
        var session = Build(File("claude", "c", "thinking", 1, turnStart: Now - 60_000, turnEnd: Now - 70_000)).Sessions[0];
        Assert.Equal(Now - 60_000, session.Turn!.StartedMs);
        Assert.Null(session.Turn.EndedMs);
    }
}
