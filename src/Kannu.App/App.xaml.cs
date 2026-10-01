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

using System.IO;
using System.Threading;
using System.Windows;
using Kannu.Core;

namespace Kannu.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private StatusMonitor? _monitor;
    private TrayIcon? _tray;
    private UpdateService? _updates;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\Kannu.Windows.SingleInstance", out var firstInstance);
        if (!firstInstance)
        {
            Shutdown();
            return;
        }

        var statusDirectory = StatusPaths.DefaultStatusDirectory();
        Directory.CreateDirectory(statusDirectory);

        var model = new NotchViewModel();
        var notch = new NotchWindow(model);
        _updates = new UpdateService();
        _tray = new TrayIcon(notch, statusDirectory, _updates, Shutdown);
        model.AggregateChanged += _tray.SetLight;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NotchViewModel.Summary)) _tray.SetSummary(model.Summary);
        };

        _monitor = new StatusMonitor(statusDirectory, model.Update, tokens => model.Tokens = tokens);
        notch.Show();
        _monitor.Start();
        // macOS starts its updater only after the Terms of Use are accepted; once Kannu for Windows has
        // that gate, this line moves behind it.
        _updates.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _monitor?.Dispose();
        _updates?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
