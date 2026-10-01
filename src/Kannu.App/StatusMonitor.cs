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
using System.Threading.Tasks;
using System.Windows.Threading;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// Watches the status directory and publishes resolved sessions on the UI thread. Disk is read only
/// when the directory changes (debounced, off the UI thread); the age tick re-resolves the cached
/// records so a stale light dims without rescanning anything.
/// </summary>
internal sealed class StatusMonitor : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan AgeTick = TimeSpan.FromSeconds(5);

    private readonly string _directory;
    private readonly Action<IReadOnlyList<AgentSession>> _publish;
    private readonly FileSystemWatcher _watcher;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _ageTick;
    private readonly Dispatcher _dispatcher;

    private IReadOnlyList<(string Key, StatusRecord Record)> _records = [];
    private bool _loading;
    private bool _dirty;

    public StatusMonitor(string directory, Action<IReadOnlyList<AgentSession>> publish)
    {
        _directory = directory;
        _publish = publish;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _debounce = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = Debounce };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Reload();
        };

        _ageTick = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = AgeTick };
        _ageTick.Tick += (_, _) => Publish();

        // No filter: a hook's atomic write is a rename from a .tmp name, which a "*.json" filter can miss.
        _watcher = new FileSystemWatcher(directory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;
        _watcher.Error += (_, _) => ScheduleReload();
    }

    public void Start()
    {
        _watcher.EnableRaisingEvents = true;
        _ageTick.Start();
        Reload();
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => ScheduleReload();

    /// <summary>Watcher events arrive on thread-pool threads; restart the debounce on the UI thread.</summary>
    private void ScheduleReload() => _dispatcher.BeginInvoke(() =>
    {
        _debounce.Stop();
        _debounce.Start();
    });

    private async void Reload()
    {
        if (_loading)
        {
            _dirty = true;
            return;
        }

        _loading = true;
        try
        {
            do
            {
                _dirty = false;
                _records = await Task.Run(() => StatusStore.ReadAll(_directory));
                Publish();
            } while (_dirty);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Publish() =>
        _publish(StatusStore.Resolve(_records, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _debounce.Stop();
        _ageTick.Stop();
    }
}
