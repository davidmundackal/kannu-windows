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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// Claude Code's plan limits for the Usage tab: Kannu's statusline file and Claude Code's own cache
/// in <c>~/.claude.json</c>, each re-read only when it changed, on a background thread every 30 s.
/// Keeps the readings for the forecast (in memory: a restart starts the forecast again) and sends
/// one near-limit notification per window. Port of macOS <c>LLMUsageManager</c>'s Claude part and
/// <c>UsageAlertManager</c>.
/// </summary>
internal sealed class UsageMonitor : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly string _statusFile;
    private readonly string _claudeJson = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
    private readonly NotchViewModel _model;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, IReadOnlyList<UsageForecast.Sample>> _samples = [];
    private readonly HashSet<string> _alerted = [];
    private (DateTime Statusline, DateTime Cache) _mtimes;
    private IReadOnlyList<UsageWindow> _statusline = [];
    private IReadOnlyList<UsageWindow> _cache = [];
    private bool _first = true;
    private bool _busy;

    public UsageMonitor(string statusDirectory, NotchViewModel model)
    {
        _statusFile = Path.Combine(statusDirectory, ClaudeUsage.FileName);
        _model = model;
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await Task.Run(Read).ConfigureAwait(true);
            Publish();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Off the UI thread: ~/.claude.json can be megabytes.</summary>
    private void Read()
    {
        var statusMtime = Mtime(_statusFile);
        if (statusMtime != _mtimes.Statusline)
        {
            _statusline = statusMtime == default ? [] : ClaudeUsage.ParseStatuslineFile(ReadText(_statusFile));
            _mtimes.Statusline = statusMtime;
        }
        var cacheMtime = Mtime(_claudeJson);
        if (cacheMtime != _mtimes.Cache)
        {
            _cache = cacheMtime == default ? [] : ClaudeUsage.ParseCache(ReadText(_claudeJson));
            _mtimes.Cache = cacheMtime;
        }
    }

    private void Publish()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windows = ClaudeUsage.Merge(now, _statusline, _cache);
        var bars = new List<UsageBar>();
        foreach (var window in windows)
        {
            var samples = _samples.TryGetValue(window.Key, out var held) ? held : [];
            samples = UsageForecast.Admitting(new UsageForecast.Sample(window.ObservedAtMs, window.Percent, window.ResetsAtMs), samples);
            _samples[window.Key] = samples;
            var outlook = UsageForecast.For(samples, window, now);
            bars.Add(new UsageBar(window,
                window.ResetsAtMs is { } reset ? UsageForecast.Countdown(reset - now) : null,
                UsageForecast.Caption(outlook, window.ResetsAtMs, Clock)));

            if (UsageAlerts.IsNearLimit(window, now) && _alerted.Add(UsageAlerts.Key(window)) && !_first)
            {
                NotificationManager.Shared?.Alert(UsageAlerts.Payload(window, Clock));
            }
        }
        _first = false;

        var hint = windows.Count > 0 ? ""
            : HookSetup.UsageStatuslineInstalled
                ? "Limits appear after Claude Code's next reply. They need a Claude subscription login (/login in Claude Code)."
                : "Turn on Claude Code plan limits in Settings › Agents to see your 5-hour and weekly limits here.";
        _model.UpdateUsage(bars, hint, windows);
    }

    /// <summary>"15:40" today, "Tue 15:40" on another day, in the user's own time format.</summary>
    internal static string Clock(long ms)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        var time = at.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        return at.Date == DateTime.Today ? time : at.ToString("ddd", System.Globalization.CultureInfo.CurrentCulture) + " " + time;
    }

    private static DateTime Mtime(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static string ReadText(string path)
    {
        try
        {
            // Claude Code rewrites ~/.claude.json while running: share it rather than lock it.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    public void Dispose() => _timer.Stop();
}
