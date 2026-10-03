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
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Kannu.App.Interop;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// The brightness HUD and fine steps. A laptop's brightness keys are handled by its firmware and the
/// maker's driver, so no app can take them over (and Windows' own flyout still shows); what Kannu can
/// do is notice every change (WMI's WmiMonitorBrightnessEvent, the built-in panel) and show it on the
/// notch, and step brightness itself in 1/64ths from its own shortcuts: the built-in panel through
/// WMI, an external monitor through DDC/CI. Nothing runs while both options are off.
/// </summary>
internal sealed class BrightnessManager : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private ManagementEventWatcher? _watcher;
    private (int Level, long AtMs) _lastShown;

    /// <summary>A change to show: 0 to 1.</summary>
    public event Action<double>? Changed;

    public BrightnessManager(SettingsStore settings, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Apply(settings.Current);
        settings.Changed += Apply;
    }

    private void Apply(AppSettings settings)
    {
        if (settings.BrightnessHud && _watcher is null) StartWatching();
        else if (!settings.BrightnessHud && _watcher is not null) StopWatching();
    }

    private void StartWatching()
    {
        try
        {
            _watcher = new ManagementEventWatcher(new ManagementScope(@"root\WMI"),
                new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _watcher.EventArrived += (_, e) =>
            {
                if (e.NewEvent["Brightness"] is byte level) _dispatcher.BeginInvoke(() => Show(level, 0, 100));
            };
            _watcher.Start();
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException)
        {
            // Desktops without a built-in panel have no such event: the HUD then shows only Kannu's own steps.
            Diagnostics.Info($"brightness events unavailable: {e.GetType().Name}");
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void StopWatching()
    {
        try
        {
            _watcher?.Stop();
        }
        catch (Exception e) when (e is ManagementException or COMException)
        {
        }
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>One fine step on the display under the pointer: DDC/CI first (external), WMI for a built-in panel.</summary>
    public void Step(int direction)
    {
        if (StepDdc(direction) is var (ddc, min, max)) Show(ddc, min, max);
        else if (StepWmi(direction) is { } wmi) Show(wmi, 0, 100);
    }

    private void Show(int level, int min, int max)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Kannu's own WMI step comes back as an event too: one HUD per change.
        if (_lastShown.Level == level && now - _lastShown.AtMs < 400) return;
        _lastShown = (level, now);
        Changed?.Invoke(BrightnessSteps.Fraction(level, min, max));
    }

    private static int? StepWmi(int direction)
    {
        try
        {
            var scope = new ManagementScope(@"root\WMI");
            using var current = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"));
            var level = current.Get().Cast<ManagementObject>().Select(o => (byte)o["CurrentBrightness"]).FirstOrDefault();
            var next = BrightnessSteps.Next(level, 0, 100, direction);
            using var methods = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods"));
            foreach (ManagementObject method in methods.Get())
            {
                method.InvokeMethod("WmiSetBrightness", [1u, (byte)next]);
                return next;
            }
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException)
        {
            Diagnostics.Info($"brightness (WMI) unavailable: {e.GetType().Name}");
        }
        return null;
    }

    private static (int Level, int Min, int Max)? StepDdc(int direction)
    {
        if (!NativeMethods.GetCursorPos(out var point)) return null;
        var monitor = MonitorFromPoint(point, 2 /* NEAREST */);
        if (monitor == IntPtr.Zero || !GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0) return null;
        var physical = new PHYSICAL_MONITOR[count];
        if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical)) return null;
        try
        {
            foreach (var p in physical)
            {
                if (!GetMonitorBrightness(p.Handle, out var min, out var current, out var max)) continue;
                var next = BrightnessSteps.Next((int)current, (int)min, (int)max, direction);
                if (SetMonitorBrightness(p.Handle, (uint)next)) return (next, (int)min, (int)max);
            }
            return null;
        }
        finally
        {
            DestroyPhysicalMonitors(count, physical);
        }
    }

    public void Dispose() => StopWatching();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativeMethods.POINT point, uint flags);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint brightness);
}
