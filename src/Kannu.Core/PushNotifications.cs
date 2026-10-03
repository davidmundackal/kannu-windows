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

namespace Kannu.Core;

/// <summary>Where phone notifications go. Port of macOS <c>AgentStatusNotificationProvider</c>.</summary>
public enum PushProvider
{
    Ntfy,
    Pushover,
    Webhook,
}

/// <summary>One notification: the same text whether it goes to a phone, a webhook or a Windows toast.</summary>
/// <param name="Priority">ntfy's 1 (min) to 5 (urgent); Pushover gets it shifted to -2..2.</param>
/// <param name="WebhookState">The webhook body's <c>state</c> key.</param>
public sealed record PushPayload(string Title, string Body, int Priority, string Tag, string WebhookState)
{
    /// <summary>
    /// The aggregate light's notification, word for word as macOS sends it. No chat names, prompts,
    /// paths or project names: a phone push leaves the PC, and an ntfy topic is readable by anyone who
    /// knows it.
    /// </summary>
    public static PushPayload For(AgentLightState state) => state switch
    {
        AgentLightState.Thinking => new("Agent Thinking", "Your AI agent is reasoning or composing a response.", 3, "agent-thinking", "thinking"),
        AgentLightState.Executing => new("Agent Executing", "Your AI agent is running tools and doing work.", 4, "agent-executing", "executing"),
        AgentLightState.AwaitingInput => new("Agent Needs Input", "Your AI agent is waiting for your approval or response.", 5, "agent-awaiting-input", "awaiting_input"),
        AgentLightState.Stopped => new("Agent Stopped", "Your AI agent has finished or was aborted.", 2, "agent-stopped", "stopped"),
        _ => new("Agent Inactive", "No active AI agent detected.", 1, "agent-inactive", "inactive"),
    };

    public static PushPayload Test { get; } =
        new("Kannu Test", "Mobile notifications are configured correctly.", 3, "kannu-test", "thinking");

    public static PushPayload StillWaiting(string providerLabel, int minutes) =>
        new("Still waiting on you", $"{providerLabel} has waited {minutes} minutes for your answer.", 5, "agent-still-waiting", "still_waiting");

    /// <summary>Pushover's priority: -2 (lowest) to 2 (emergency), from ntfy's 1 to 5.</summary>
    public int PushoverPriority => Math.Clamp(Priority - 2, -2, 2);
}

/// <summary>Which aggregate changes are sent, and where a URL may point. Port of macOS's bridge and <c>SecurityURLPolicy</c>.</summary>
public static class PushRules
{
    /// <summary>macOS waits this long for the light to settle and sends only the state it settled on.</summary>
    public const int DebounceMs = 2_000;

    /// <summary>
    /// Send this state? Never the same state twice in a row, and "inactive" only when asked for.
    /// </summary>
    public static bool ShouldSend(AgentLightState state, AgentLightState? lastSent, bool notifyOnInactive) =>
        state != lastSent && (state != AgentLightState.Inactive || notifyOnInactive);

    /// <summary>A webhook must be https and not loopback or a private IPv4 address: no reaching into the LAN.</summary>
    public static bool IsAllowedWebhookUrl(string raw) =>
        Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
        && url.Host.Length > 0 && !IsLoopback(url.Host) && !IsPrivateIPv4(url.Host);

    /// <summary>An ntfy server must be https and not on a private IPv4 address.</summary>
    public static bool IsAllowedNtfyServer(string raw) =>
        Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
        && url.Host.Length > 0 && !IsPrivateIPv4(url.Host);

    /// <summary>The topic URL, or null when the server or topic is unusable.</summary>
    public static Uri? NtfyUrl(string server, string topic)
    {
        topic = topic.Trim();
        if (topic.Length == 0 || topic.Contains('/') || !IsAllowedNtfyServer(server)) return null;
        return new Uri(server.Trim().TrimEnd('/') + "/" + Uri.EscapeDataString(topic));
    }

    public static IReadOnlyList<KeyValuePair<string, string>> PushoverForm(PushPayload payload, string appToken, string userKey) =>
    [
        new("token", appToken.Trim()),
        new("user", userKey.Trim()),
        new("title", payload.Title),
        new("message", payload.Body),
        new("priority", payload.PushoverPriority.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    ];

    public static string WebhookBody(PushPayload payload, DateTimeOffset now, IReadOnlyDictionary<string, string>? extra = null)
    {
        var body = new JsonObject
        {
            ["state"] = payload.WebhookState,
            ["title"] = payload.Title,
            ["body"] = payload.Body,
            ["timestamp"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["source"] = "Kannu",
        };
        if (extra is not null)
        {
            foreach (var (key, value) in extra) body.TryAdd(key, value);
        }
        return body.ToJsonString();
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "::1" or "[::1]";

    private static bool IsPrivateIPv4(string host)
    {
        var parts = host.Split('.');
        if (parts.Length != 4 || !int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b)) return false;
        return a == 10 || a == 127 || (a == 172 && b is >= 16 and <= 31) || (a == 192 && b == 168);
    }
}

/// <summary>
/// "Still waiting on you": which yellow sessions have waited past the threshold and have not been
/// reminded about this wait yet. Port of macOS <c>AgentWaitReminder</c>.
///
/// A wait starts when a session is first seen yellow and keeps that start until the session leaves
/// yellow, so a yellow whose timestamp moves with activity is still one wait, one reminder. Leaving
/// yellow and coming back is a new wait. Waits already overdue on the first look after launch (or
/// after reminders are switched on) are marked, not sent, so a restart never sends a burst.
/// </summary>
public sealed class WaitReminder
{
    public sealed record Due(string SessionId, string ProviderLabel, int Minutes);

    private Dictionary<string, (long SinceMs, string Label)> _waits = [];
    private readonly HashSet<string> _reminded = [];

    /// <param name="thresholdMs">Null or 0: reminders are off and everything is forgotten.</param>
    public IReadOnlyList<Due> Update(IReadOnlyList<AgentSession> sessions, long nowMs, long? thresholdMs, bool suppressOverdue)
    {
        if (thresholdMs is not > 0)
        {
            _waits = [];
            _reminded.Clear();
            return [];
        }
        var current = new Dictionary<string, (long SinceMs, string Label)>();
        foreach (var session in sessions)
        {
            if (!session.IsVisible || session.DisplayState != AgentLightState.AwaitingInput || AgentStateMachine.IsSimulation(session)) continue;
            current[session.Id] = (_waits.TryGetValue(session.Id, out var wait) ? wait.SinceMs : Math.Min(session.UpdatedAtMs, nowMs), session.ProviderLabel);
        }
        _waits = current;
        _reminded.IntersectWith(current.Select(w => Key(w.Key, w.Value.SinceMs)));

        var due = new List<Due>();
        foreach (var (id, (since, label)) in current)
        {
            if (nowMs - since < thresholdMs) continue;
            if (!_reminded.Add(Key(id, since)) || suppressOverdue) continue;
            due.Add(new Due(id, label, (int)((nowMs - since) / 60_000)));
        }
        return due.OrderBy(d => d.SessionId, StringComparer.Ordinal).ToList();
    }

    /// <summary>When the next pending reminder falls due; null when none is pending.</summary>
    public long? NextCheckMs(long nowMs, long thresholdMs)
    {
        long? next = null;
        foreach (var (id, (since, _)) in _waits)
        {
            if (_reminded.Contains(Key(id, since))) continue;
            var at = since + thresholdMs;
            if (at > nowMs && (next is null || at < next)) next = at;
        }
        return next;
    }

    private static string Key(string id, long sinceMs) => id + "|" + (sinceMs / 1000);
}
