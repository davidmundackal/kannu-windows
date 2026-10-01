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
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;
using Kannu.App.Interop;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// The notification-area icon: Kannu's eye (the same eye macOS Kannu shows in the menu bar), tinted
/// with the most urgent light, plus the menu for everything the notch itself does not do.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly Dictionary<TrafficLight, Icon> _icons = [];
    private readonly List<IntPtr> _iconHandles = [];
    private TrafficLight _light = TrafficLight.Inactive;

    public TrayIcon(NotchWindow notch, string statusDirectory, Action quit)
    {
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        BuildIcons();

        var show = new ToolStripMenuItem("Show notch") { Checked = true, CheckOnClick = true };
        show.CheckedChanged += (_, _) =>
        {
            if (show.Checked) notch.Show();
            else notch.Hide();
        };

        // One item per agent: checked when Kannu's hook is installed; clicking installs or removes it.
        var agents = new ToolStripMenuItem("Agent hooks");
        foreach (var provider in Enum.GetValues<AgentProvider>())
        {
            var item = new ToolStripMenuItem(provider.DisplayName()) { Tag = provider };
            item.Click += (_, _) => RunSetup(() => HookSetup.IsInstalled(provider)
                ? HookSetup.Uninstall(provider)
                : HookSetup.Install(provider));
            agents.DropDownItems.Add(item);
        }
        agents.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem item in agents.DropDownItems)
            {
                var provider = (AgentProvider)item.Tag!;
                var installed = HookSetup.IsInstalled(provider);
                item.Checked = installed;
                // Installing for a CLI that never ran here would create its folder.
                item.Enabled = installed || HookSetup.ToolIsPresent(provider);
            }
        };

        var openFolder = new ToolStripMenuItem("Open status folder", null, (_, _) =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{statusDirectory}\"") { UseShellExecute = true }));

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            show,
            new ToolStripSeparator(),
            agents,
            openFolder,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit Kannu", null, (_, _) => quit()),
        });

        _icon = new NotifyIcon
        {
            Icon = _icons[_light],
            Text = "Kannu",
            ContextMenuStrip = menu,
            Visible = true,
        };

        // Taskbar theme or display scale changed: the idle eye's colour or the icon size is stale.
        SystemEvents.UserPreferenceChanged += OnSystemChanged;
        SystemEvents.DisplaySettingsChanged += OnSystemChanged;
    }

    public void SetLight(TrafficLight light)
    {
        _light = light;
        _icon.Icon = _icons[light];
    }

    /// <summary>The hover tooltip; Windows caps it at 63 characters.</summary>
    public void SetSummary(string summary)
    {
        var text = $"Kannu · {summary}";
        _icon.Text = text.Length > 63 ? text[..62] + "…" : text;
    }

    // SystemEvents raise on their own thread.
    private void OnSystemChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        var old = _iconHandles.ToList();
        var oldIcons = _icons.Values.ToList();
        _icons.Clear();
        _iconHandles.Clear();
        BuildIcons();
        _icon.Icon = _icons[_light];
        foreach (var icon in oldIcons) icon.Dispose();
        foreach (var handle in old) NativeMethods.DestroyIcon(handle);
    });

    private void RunSetup(Func<string> action)
    {
        try
        {
            _icon.ShowBalloonTip(4000, "Kannu", action(), ToolTipIcon.Info);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HookInstallException)
        {
            System.Windows.MessageBox.Show(e.Message, "Kannu", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private void BuildIcons()
    {
        var size = SystemInformation.SmallIconSize.Width;
        var idle = TaskbarIsLight() ? Color.FromArgb(0x1F, 0x1F, 0x1F) : Color.White;
        _icons[TrafficLight.Inactive] = MakeIcon(idle, size);
        _icons[TrafficLight.Green] = MakeIcon(KannuColors.Green, size);
        _icons[TrafficLight.Yellow] = MakeIcon(KannuColors.Yellow, size);
        _icons[TrafficLight.Red] = MakeIcon(KannuColors.Red, size);
    }

    private Icon MakeIcon(Color color, int size)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            // Grayscale antialiasing: ClearType fringes on a transparent bitmap.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var font = new Font(EyeGlyph.FontFamily, size * 0.9f, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString(EyeGlyph.Text, font, brush, new RectangleF(0, 0, size, size), format);
        }
        // Icon.FromHandle does not own the handle; it is destroyed when the icon is replaced or disposed.
        var handle = bitmap.GetHicon();
        _iconHandles.Add(handle);
        return Icon.FromHandle(handle);
    }

    private static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnSystemChanged;
        SystemEvents.DisplaySettingsChanged -= OnSystemChanged;
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
        foreach (var handle in _iconHandles) NativeMethods.DestroyIcon(handle);
    }
}
