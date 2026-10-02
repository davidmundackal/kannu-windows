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
using System.Security;
using Microsoft.Win32;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// "Start Kannu when you sign in": the per-user Run key, pointing at Velopack's stable launcher
/// (<see cref="LaunchAtLogin.StablePath"/>). Like macOS Kannu it is switched on once for a fresh
/// install and repaired when it points somewhere stale; Task Manager's Startup tab can still turn it
/// off, and Settings shows that.
/// </summary>
internal static class LaunchAtLoginManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>Null when this copy of Kannu was not installed by its installer (developer or portable build).</summary>
    public static string? StablePath => LaunchAtLogin.StablePath(AppContext.BaseDirectory);

    public static bool IsEnabled => ReadCommand() is not null && !DisabledInTaskManager;

    public static bool DisabledInTaskManager
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                return LaunchAtLogin.DisabledInTaskManager(key?.GetValue(LaunchAtLogin.RunValueName) as byte[]);
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && StablePath is { } path)
            {
                run.SetValue(LaunchAtLogin.RunValueName, LaunchAtLogin.Command(path));
                // Turning it on in Kannu also clears an earlier "off" from Task Manager.
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                approved?.DeleteValue(LaunchAtLogin.RunValueName, throwOnMissingValue: false);
            }
            else
            {
                run.DeleteValue(LaunchAtLogin.RunValueName, throwOnMissingValue: false);
            }
            Diagnostics.Info($"Start at sign-in {(enabled ? "on" : "off")}");
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            Diagnostics.Error("Could not change start at sign-in", e);
        }
    }

    /// <summary>At launch: on once for a fresh install, and pointed at the current launcher if it moved.</summary>
    public static void EnsureDefault(SettingsStore settings)
    {
        if (StablePath is not { } path) return;
        if (!settings.Current.LaunchAtLoginInitialized)
        {
            Set(true);
            settings.Update(s => s with { LaunchAtLoginInitialized = true });
        }
        else if (ReadCommand() is { } command && command != LaunchAtLogin.Command(path))
        {
            Set(true);
        }
    }

    /// <summary>Before uninstalling: nothing left behind in the Run key.</summary>
    public static void Remove()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(LaunchAtLogin.RunValueName, throwOnMissingValue: false);
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(LaunchAtLogin.RunValueName, throwOnMissingValue: false);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
        }
    }

    private static string? ReadCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(LaunchAtLogin.RunValueName) as string;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
