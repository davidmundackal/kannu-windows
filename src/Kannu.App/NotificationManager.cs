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
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// Phone notifications (ntfy, Pushover, a webhook), the "still waiting" reminder and Windows
/// notifications. Port of macOS <c>AgentStatusNotificationBridge</c>: the aggregate light is sent
/// once it has settled for 2 s, never the same state twice in a row. Everything is off by default.
/// Only fixed texts leave the PC: no chat names, prompts or paths.
/// </summary>
internal sealed class NotificationManager : IDisposable
{
    public static NotificationManager? Shared { get; private set; }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly SettingsStore _settings;
    private readonly Action<string, string> _toast;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(PushRules.DebounceMs) };
    private readonly DispatcherTimer _reminderTimer = new();
    private readonly WaitReminder _reminder = new();
    private IReadOnlyList<AgentSession> _sessions = [];
    private AgentLightState _pending = AgentLightState.Inactive;
    private AgentLightState? _lastPushed;
    private AgentLightState? _lastToasted;
    private bool _remindersOn;

    /// <summary>The last delivery's outcome, for Settings: null when it worked or nothing was sent.</summary>
    public string? LastError { get; private set; }

    public NotificationManager(SettingsStore settings, Action<string, string> toast)
    {
        _settings = settings;
        _toast = toast;
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Deliver(_pending);
        };
        _reminderTimer.Tick += (_, _) =>
        {
            _reminderTimer.Stop();
            CheckReminders();
        };
        settings.Changed += _ => CheckReminders();
        Shared = this;
    }

    /// <summary>The aggregate light changed: send it once it has held for 2 s.</summary>
    public void StateChanged(AgentLightState state)
    {
        _pending = state;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Every rescan's sessions, for the reminder.</summary>
    public void Update(IReadOnlyList<AgentSession> sessions)
    {
        _sessions = sessions;
        CheckReminders();
    }

    private void Deliver(AgentLightState state)
    {
        var s = _settings.Current;
        // Toasts: only what needs a look (yellow) or is news (red), each once per change.
        if (s.ToastsEnabled && state is AgentLightState.AwaitingInput or AgentLightState.Stopped && state != _lastToasted)
        {
            var payload = PushPayload.For(state);
            _toast(payload.Title, payload.Body);
        }
        _lastToasted = state;

        if (!s.PushEnabled || !PushRules.ShouldSend(state, _lastPushed, s.PushOnInactive)) return;
        _ = SendAsync(PushPayload.For(state), null, () => _lastPushed = state);
    }

    private void CheckReminders()
    {
        var s = _settings.Current;
        long? threshold = s.WaitReminderMinutes > 0 && (s.PushEnabled || s.ToastsEnabled) ? s.WaitReminderMinutes * 60_000L : null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Waits already overdue when reminders switch on (launch, or the setting just turned on) are not sent.
        var turningOn = threshold is not null && !_remindersOn;
        _remindersOn = threshold is not null;
        var due = _reminder.Update(_sessions, now, threshold, turningOn);

        _reminderTimer.Stop();
        if (threshold is { } t && _reminder.NextCheckMs(now, t) is { } next)
        {
            _reminderTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1_000, next - now + 500));
            _reminderTimer.Start();
        }

        foreach (var reminder in due)
        {
            var payload = PushPayload.StillWaiting(reminder.ProviderLabel, reminder.Minutes);
            if (s.ToastsEnabled) _toast(payload.Title, payload.Body);
            if (s.PushEnabled)
            {
                _ = SendAsync(payload, new Dictionary<string, string>
                {
                    ["provider"] = reminder.ProviderLabel,
                    ["minutes"] = reminder.Minutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }, null);
            }
        }
    }

    /// <summary>A one-off notice (a usage limit nearly reached): a Windows notification and a phone push, whichever is on.</summary>
    public void Alert(PushPayload payload)
    {
        var s = _settings.Current;
        if (s.ToastsEnabled) _toast(payload.Title, payload.Body);
        if (s.PushEnabled) _ = SendAsync(payload, null, null);
    }

    /// <summary>Settings' "Send test": whatever is configured, even while notifications are off.</summary>
    public Task<string?> SendTestAsync() => SendAsync(PushPayload.Test, null, null);

    private async Task<string?> SendAsync(PushPayload payload, IReadOnlyDictionary<string, string>? extra, Action? onSuccess)
    {
        var s = _settings.Current;
        try
        {
            using var request = Build(s, payload, extra);
            using var response = await Http.SendAsync(request).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            LastError = null;
            onSuccess?.Invoke();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Never the URL or the topic: they are secrets.
            LastError = e.Message;
            Diagnostics.Info($"notification to {s.PushProvider} failed: {e.GetType().Name}");
        }
        return LastError;
    }

    private static HttpRequestMessage Build(AppSettings s, PushPayload payload, IReadOnlyDictionary<string, string>? extra)
    {
        switch (s.PushProvider)
        {
            case PushProvider.Ntfy:
            {
                var url = PushRules.NtfyUrl(s.NtfyServer, SecretStore.Get(SecretStore.NtfyTopic))
                          ?? throw new InvalidOperationException("Enter an ntfy topic, and a server address that starts with https://.");
                var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload.Body, Encoding.UTF8, "text/plain") };
                // Header values must be ASCII: ntfy reads RFC 2047 for anything else, but the fixed titles are ASCII.
                request.Headers.Add("Title", payload.Title);
                request.Headers.Add("Priority", payload.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
                request.Headers.Add("Tags", payload.Tag);
                request.Headers.Add("X-Kannu", "kannu");
                return request;
            }
            case PushProvider.Pushover:
            {
                var user = SecretStore.Get(SecretStore.PushoverUserKey);
                var token = SecretStore.Get(SecretStore.PushoverAppToken);
                if (user.Length == 0 || token.Length == 0) throw new InvalidOperationException("Enter your Pushover user key and app token.");
                return new HttpRequestMessage(HttpMethod.Post, "https://api.pushover.net/1/messages.json")
                {
                    Content = new FormUrlEncodedContent(PushRules.PushoverForm(payload, token, user)),
                };
            }
            default:
            {
                var url = SecretStore.Get(SecretStore.WebhookUrl);
                if (!PushRules.IsAllowedWebhookUrl(url)) throw new InvalidOperationException("Enter a webhook address that starts with https:// and is not on your local network.");
                return new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(PushRules.WebhookBody(payload, DateTimeOffset.UtcNow, extra), Encoding.UTF8, "application/json"),
                };
            }
        }
    }

    public void Dispose()
    {
        _debounce.Stop();
        _reminderTimer.Stop();
        if (Shared == this) Shared = null;
    }
}
