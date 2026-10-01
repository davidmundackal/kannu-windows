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
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using Kannu.App.Interop;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// The notification-area icon: the most urgent light as a dot, and the menu for everything the notch
/// itself does not do.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Dictionary<TrafficLight, Icon> _icons = [];
    private readonly List<IntPtr> _iconHandles = [];

    public TrayIcon(NotchWindow notch, string statusDirectory, Action quit)
    {
        _icons[TrafficLight.Inactive] = MakeIcon(Color.FromArgb(0x8E, 0x8E, 0x93));
        _icons[TrafficLight.Green] = MakeIcon(Color.FromArgb(0x34, 0xC7, 0x59));
        _icons[TrafficLight.Yellow] = MakeIcon(Color.FromArgb(0xFF, 0xCC, 0x00));
        _icons[TrafficLight.Red] = MakeIcon(Color.FromArgb(0xFF, 0x3B, 0x30));

        var show = new ToolStripMenuItem("Show notch") { Checked = true, CheckOnClick = true };
        show.CheckedChanged += (_, _) =>
        {
            if (show.Checked) notch.Show();
            else notch.Hide();
        };

        var install = new ToolStripMenuItem("Install Claude Code hooks", null, (_, _) => RunSetup(HookSetup.Install));
        var remove = new ToolStripMenuItem("Remove Claude Code hooks", null, (_, _) => RunSetup(HookSetup.Remove));
        var openFolder = new ToolStripMenuItem("Open status folder", null, (_, _) =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{statusDirectory}\"") { UseShellExecute = true }));

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            show,
            new ToolStripSeparator(),
            install,
            remove,
            openFolder,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit Kannu", null, (_, _) => quit()),
        });
        menu.Opening += (_, _) =>
        {
            var installed = HookSetup.IsInstalled();
            install.Text = installed ? "Reinstall Claude Code hooks" : "Install Claude Code hooks";
            remove.Enabled = installed;
        };

        _icon = new NotifyIcon
        {
            Icon = _icons[TrafficLight.Inactive],
            Text = "Kannu",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    public void SetLight(TrafficLight light) => _icon.Icon = _icons[light];

    private void RunSetup(Func<string> action)
    {
        try
        {
            _icon.ShowBalloonTip(4000, "Kannu", action(), ToolTipIcon.Info);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            System.Windows.MessageBox.Show(e.Message, "Kannu", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private Icon MakeIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 6, 6, 20, 20);
        }
        // Icon.FromHandle does not own the handle; it is destroyed in Dispose.
        var handle = bitmap.GetHicon();
        _iconHandles.Add(handle);
        return Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
        foreach (var handle in _iconHandles) NativeMethods.DestroyIcon(handle);
    }
}
