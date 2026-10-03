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

using System.Text.Json.Nodes;
using Kannu.Core;
using Xunit;

namespace Kannu.Core.Tests;

public class PushNotificationsTests
{
    private const long T0 = 1_790_000_000_000;
    private const long FiveMinutes = 5 * 60_000;

    private static AgentSession Session(string id, AgentLightState state, long updatedMs, string provider = "claude", bool visible = true) => new()
    {
        Id = id,
        Provider = provider,
        ConversationId = id,
        RawState = "awaiting_input",
        DisplayState = state,
        UpdatedAtMs = updatedMs,
        IsVisible = visible,
    };

    [Fact]
    public void TheSameStateIsNeverSentTwiceAndInactiveOnlyWhenAsked()
    {
        Assert.True(PushRules.ShouldSend(AgentLightState.AwaitingInput, null, false));
        Assert.False(PushRules.ShouldSend(AgentLightState.AwaitingInput, AgentLightState.AwaitingInput, false));
        Assert.True(PushRules.ShouldSend(AgentLightState.Stopped, AgentLightState.AwaitingInput, false));
        Assert.False(PushRules.ShouldSend(AgentLightState.Inactive, AgentLightState.Stopped, false));
        Assert.True(PushRules.ShouldSend(AgentLightState.Inactive, AgentLightState.Stopped, true));
    }

    [Fact]
    public void PayloadsMatchMacOS()
    {
        var yellow = PushPayload.For(AgentLightState.AwaitingInput);
        Assert.Equal("Agent Needs Input", yellow.Title);
        Assert.Equal(5, yellow.Priority);
        Assert.Equal(2, yellow.PushoverPriority);
        Assert.Equal(0, PushPayload.For(AgentLightState.Stopped).PushoverPriority);
        Assert.Equal(-1, PushPayload.For(AgentLightState.Inactive).PushoverPriority);
        Assert.Equal("Claude Code has waited 7 minutes for your answer.", PushPayload.StillWaiting("Claude Code", 7).Body);
    }

    [Theory]
    [InlineData("https://hooks.example.com/kannu", true)]
    [InlineData("http://hooks.example.com/kannu", false)]
    [InlineData("https://localhost/x", false)]
    [InlineData("https://127.0.0.1/x", false)]
    [InlineData("https://192.168.1.10/x", false)]
    [InlineData("https://172.20.0.1/x", false)]
    [InlineData("https://10.0.0.1/x", false)]
    [InlineData("not a url", false)]
    public void WebhooksMustBePublicHttps(string url, bool allowed) => Assert.Equal(allowed, PushRules.IsAllowedWebhookUrl(url));

    [Fact]
    public void NtfyUrls()
    {
        Assert.Equal("https://ntfy.sh/my-topic", PushRules.NtfyUrl("https://ntfy.sh/", " my-topic ")!.ToString());
        Assert.Null(PushRules.NtfyUrl("http://ntfy.sh", "t"));
        Assert.Null(PushRules.NtfyUrl("https://ntfy.sh", ""));
        Assert.Null(PushRules.NtfyUrl("https://ntfy.sh", "a/b"));
        Assert.Null(PushRules.NtfyUrl("https://192.168.0.2", "t"));
    }

    [Fact]
    public void WebhookBodyKeepsItsOwnKeys()
    {
        var json = JsonNode.Parse(PushRules.WebhookBody(PushPayload.For(AgentLightState.Stopped), DateTimeOffset.FromUnixTimeMilliseconds(T0),
            new Dictionary<string, string> { ["source"] = "evil", ["provider"] = "claude" }))!;
        Assert.Equal("stopped", (string?)json["state"]);
        Assert.Equal("Kannu", (string?)json["source"]);
        Assert.Equal("claude", (string?)json["provider"]);
        Assert.EndsWith("Z", (string?)json["timestamp"]);
    }

    [Fact]
    public void AReminderIsSentOncePerWait()
    {
        var reminder = new WaitReminder();
        var yellow = new[] { Session("a", AgentLightState.AwaitingInput, T0) };
        Assert.Empty(reminder.Update(yellow, T0 + 60_000, FiveMinutes, false));
        Assert.Equal(T0 + FiveMinutes, reminder.NextCheckMs(T0 + 60_000, FiveMinutes));

        var due = Assert.Single(reminder.Update(yellow, T0 + FiveMinutes, FiveMinutes, false));
        Assert.Equal(new WaitReminder.Due("a", "Claude", 5), due);
        Assert.Empty(reminder.Update(yellow, T0 + 2 * FiveMinutes, FiveMinutes, false));
        Assert.Null(reminder.NextCheckMs(T0 + 2 * FiveMinutes, FiveMinutes));
    }

    [Fact]
    public void AMovingTimestampIsStillOneWait()
    {
        var reminder = new WaitReminder();
        reminder.Update([Session("a", AgentLightState.AwaitingInput, T0)], T0, FiveMinutes, false);
        Assert.Single(reminder.Update([Session("a", AgentLightState.AwaitingInput, T0 + 4 * 60_000)], T0 + FiveMinutes, FiveMinutes, false));
    }

    [Fact]
    public void LeavingYellowStartsANewWait()
    {
        var reminder = new WaitReminder();
        reminder.Update([Session("a", AgentLightState.AwaitingInput, T0)], T0 + FiveMinutes, FiveMinutes, false);
        reminder.Update([Session("a", AgentLightState.Executing, T0 + FiveMinutes)], T0 + FiveMinutes + 1_000, FiveMinutes, false);
        var again = T0 + FiveMinutes + 2_000;
        reminder.Update([Session("a", AgentLightState.AwaitingInput, again)], again, FiveMinutes, false);
        Assert.Single(reminder.Update([Session("a", AgentLightState.AwaitingInput, again)], again + FiveMinutes, FiveMinutes, false));
    }

    [Fact]
    public void OverdueWaitsAtStartAreMarkedNotSent()
    {
        var reminder = new WaitReminder();
        var yellow = new[] { Session("a", AgentLightState.AwaitingInput, T0) };
        Assert.Empty(reminder.Update(yellow, T0 + 3 * FiveMinutes, FiveMinutes, suppressOverdue: true));
        Assert.Empty(reminder.Update(yellow, T0 + 4 * FiveMinutes, FiveMinutes, false));
    }

    [Fact]
    public void OffForgetsEverythingAndHiddenOrSimulatedSessionsDoNotCount()
    {
        var reminder = new WaitReminder();
        Assert.Empty(reminder.Update([Session("a", AgentLightState.AwaitingInput, T0)], T0 + FiveMinutes, null, false));
        Assert.Empty(reminder.Update([Session("a", AgentLightState.AwaitingInput, T0, visible: false)], T0 + FiveMinutes, FiveMinutes, false));
    }
}
