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
using System.Runtime.InteropServices;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// Holds one Windows power request (<c>PowerSetRequest(PowerRequestSystemRequired)</c>) while
/// <see cref="Caffeinate"/> says to, the counterpart of macOS Kannu's single IOPM assertion. Every
/// event runs the same pass: decision, transition, one command. The display may still turn off, and
/// the lid and power button still sleep the PC. UI thread only.
/// </summary>
internal sealed class CaffeinateManager : IDisposable
{
    private readonly SettingsStore _settings;
    private IntPtr _request;
    private bool? _heldModeIsSmart;
    private IReadOnlyList<AgentSession> _sessions = [];

    /// <summary>The PC is being kept awake (the notch's sun lights up).</summary>
    public event Action<bool>? HeldChanged;

    public CaffeinateManager(SettingsStore settings)
    {
        _settings = settings;
        settings.Changed += _ => Reconcile();
    }

    public bool IsHeld => _request != IntPtr.Zero;

    public void Update(IReadOnlyList<AgentSession> sessions)
    {
        _sessions = sessions;
        Reconcile();
    }

    /// <summary>The notch's sun button: manual hold on or off (smart mode keeps deciding while it is on).</summary>
    public void ToggleManual() => _settings.Update(s => s with { CaffeinateManual = !s.CaffeinateManual });

    private void Reconcile()
    {
        var s = _settings.Current;
        var shouldHold = Caffeinate.ShouldKeepAwake(s.CaffeinateSmart, s.CaffeinateManual, featureEnabled: true,
            Caffeinate.HasWorthySession(_sessions, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var was = IsHeld;
        switch (Caffeinate.Transition(IsHeld, _heldModeIsSmart, shouldHold, s.CaffeinateSmart))
        {
            case CaffeinateTransition.Create:
                Create(s.CaffeinateSmart);
                break;
            case CaffeinateTransition.Release:
                Release();
                break;
            case CaffeinateTransition.Refresh:
                Release();
                Create(s.CaffeinateSmart);
                break;
        }
        if (was != IsHeld) HeldChanged?.Invoke(IsHeld);
    }

    private void Create(bool smart)
    {
        var context = new ReasonContext
        {
            Version = 0,
            Flags = 1, // POWER_REQUEST_CONTEXT_SIMPLE_STRING
            SimpleReasonString = Caffeinate.Reason(smart),
        };
        var request = PowerCreateRequest(ref context);
        if (request == IntPtr.Zero || request == new IntPtr(-1)) return;
        if (!PowerSetRequest(request, SystemRequired))
        {
            CloseHandle(request);
            Diagnostics.Error($"Could not keep the PC awake (error {Marshal.GetLastWin32Error()})");
            return;
        }
        _request = request;
        _heldModeIsSmart = smart;
        Diagnostics.Info($"Keeping the PC awake ({(smart ? "smart" : "manual")})");
    }

    private void Release()
    {
        if (_request == IntPtr.Zero) return;
        PowerClearRequest(_request, SystemRequired);
        CloseHandle(_request);
        _request = IntPtr.Zero;
        _heldModeIsSmart = null;
        Diagnostics.Info("Stopped keeping the PC awake");
    }

    public void Dispose() => Release();

    private const int SystemRequired = 1; // PowerRequestSystemRequired

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(IntPtr request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(IntPtr request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
