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
    private FreezeWatchdog? _watchdog;
    private CaffeinateManager? _caffeinate;
    private NotificationManager? _notifications;
    private ShortcutManager? _shortcuts;
    private UsageMonitor? _usage;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\Kannu.Windows.SingleInstance", out var firstInstance);
        if (!firstInstance)
        {
            Shutdown();
            return;
        }

        // Local diagnostics only (nothing is sent), so they run before the Terms gate, as on macOS.
        _watchdog = new FreezeWatchdog(Dispatcher);
        _watchdog.Start();

        ThemeManager.Initialize(this);
        var settings = new SettingsStore(AppSettings.DefaultPath());

        // Nothing starts before the Terms of Use are accepted: no watcher, no notch, no tray, no
        // updater (macOS Kannu's continueLaunch rule). New launch work goes in ContinueLaunch.
        if (TermsOfUse.NeedsAcceptance(settings.Current))
        {
            if (!TermsWindow.AskForAcceptance())
            {
                Shutdown();
                return;
            }
            settings.Update(s => TermsOfUse.Accepted(s, DateTimeOffset.UtcNow));
        }
        ContinueLaunch(settings);
    }

    private void ContinueLaunch(SettingsStore settings)
    {
        LaunchAtLoginManager.EnsureDefault(settings);
        KannuColors.Apply(settings.Current.LightColors);
        settings.Changed += s => KannuColors.Apply(s.LightColors);
        var statusDirectory = StatusPaths.DefaultStatusDirectory();
        Directory.CreateDirectory(statusDirectory);

        var model = new NotchViewModel();
        var notch = new NotchWindow(model, settings);
        _updates = new UpdateService();
        var updates = _updates;
        void OpenSettings() => SettingsWindow.Open(settings, updates, statusDirectory);
        void OpenSettingsAt(string page) => SettingsWindow.Open(settings, updates, statusDirectory, page);
        notch.SettingsRequested += OpenSettings;
        _caffeinate = new CaffeinateManager(settings);
        model.Updated += _caffeinate.Update;
        _caffeinate.HeldChanged += held => model.IsCaffeinated = held;
        notch.CaffeinateRequested += _caffeinate.ToggleManual;
        _tray = new TrayIcon(notch.ToggleFromTray, OpenSettings, statusDirectory, _updates, Shutdown);
        model.AggregateChanged += _tray.SetLight;
        var tray = _tray;
        _notifications = new NotificationManager(settings, tray.ShowToast);
        model.StateChanged += _notifications.StateChanged;
        model.Updated += _notifications.Update;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NotchViewModel.Summary)) _tray.SetSummary(model.Summary);
        };

        _shortcuts = new ShortcutManager(settings, notch.ToggleFromTray);

        _monitor = new StatusMonitor(statusDirectory, model.Update, tokens => model.Tokens = tokens);
        _usage = new UsageMonitor(statusDirectory, model);
        notch.Show();
        _monitor.Start();
        _usage.Start();
        _updates.Start();
        WelcomeWindow.ShowOnce(settings, OpenSettingsAt);

        // macOS offers the last crash a few seconds after launch, once everything is up.
        var offer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        offer.Tick += (_, _) =>
        {
            offer.Stop();
            ProblemReportWindow.OfferIfAny(settings);
        };
        offer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _monitor?.Dispose();
        _watchdog?.Dispose();
        _caffeinate?.Dispose();
        _notifications?.Dispose();
        _shortcuts?.Dispose();
        _usage?.Dispose();
        _updates?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
