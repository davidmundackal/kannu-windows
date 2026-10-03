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
using System.Windows.Threading;
using Kannu.Core;
using Microsoft.Win32;

namespace Kannu.App;

/// <summary>
/// The screen-capture indicator, best effort: Windows has no single "the screen is being recorded"
/// signal like macOS, but Windows 11 records apps that use its screen-capture API in the consent
/// store, as it does for camera and microphone. Read every 2 s, only while the option is on.
/// </summary>
internal sealed class ScreenCaptureMonitor : IDisposable
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private IReadOnlyList<string> _active = [];

    /// <summary>The apps capturing now (empty when none), on every change.</summary>
    public event Action<IReadOnlyList<string>>? Changed;

    public ScreenCaptureMonitor(SettingsStore settings)
    {
        _timer.Tick += (_, _) => Check();
        Apply(settings.Current);
        settings.Changed += Apply;
    }

    private void Apply(AppSettings settings)
    {
        if (settings.RecordingIndicator && !_timer.IsEnabled)
        {
            _timer.Start();
            Check();
        }
        else if (!settings.RecordingIndicator && _timer.IsEnabled)
        {
            _timer.Stop();
            Publish([]);
        }
    }

    private void Check() => Publish(ScreenCapture.ActiveApps(Read()));

    private void Publish(IReadOnlyList<string> active)
    {
        if (string.Join("|", active) == string.Join("|", _active)) return;
        _active = active;
        Changed?.Invoke(active);
    }

    private static List<ScreenCapture.Usage> Read()
    {
        var usages = new List<ScreenCapture.Usage>();
        foreach (var capability in ScreenCapture.Capabilities)
        {
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(ConsentStore + capability);
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var app = root.OpenSubKey(name);
                    if (app is null) continue;
                    if (name == "NonPackaged")
                    {
                        foreach (var path in app.GetSubKeyNames())
                        {
                            using var entry = app.OpenSubKey(path);
                            if (entry is not null) usages.Add(Usage(path, entry));
                        }
                    }
                    else
                    {
                        usages.Add(Usage(name, app));
                    }
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
            {
            }
        }
        return usages;
    }

    private static ScreenCapture.Usage Usage(string key, RegistryKey entry) => new(key,
        entry.GetValue("LastUsedTimeStart") is long start ? start : 0,
        entry.GetValue("LastUsedTimeStop") is long stop ? stop : 0);

    public void Dispose() => _timer.Stop();
}
