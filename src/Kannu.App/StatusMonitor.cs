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
/// Watches the status directory and publishes the session list on the UI thread. Disk is read only
/// when the directory changes (debounced, off the UI thread). A one-second tick re-runs the pipeline on
/// the cached files so red collapses and stale lights dim on time; it runs only while there is
/// something on screen, so an idle Kannu does no periodic work at all.
/// </summary>
internal sealed class StatusMonitor : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly string _directory;
    private readonly Action<PipelineResult> _publish;
    private readonly AgentSessionPipeline _pipeline;
    private readonly FileSystemWatcher _watcher;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _tick;
    private readonly Dispatcher _dispatcher;

    private IReadOnlyList<HookFile> _files = [];
    private bool _loading;
    private bool _dirty;

    public StatusMonitor(string directory, Action<PipelineResult> publish)
    {
        _directory = directory;
        _publish = publish;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _pipeline = new AgentSessionPipeline(new AgentTimings(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        _debounce = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = Debounce };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Reload();
        };

        _tick = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = Tick };
        _tick.Tick += (_, _) => Publish();

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
                _files = await Task.Run(() => HookSessionReader.ReadFiles(_directory));
                Publish();
            } while (_dirty);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Publish()
    {
        var result = _pipeline.Update(_files, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (result.StaleFiles.Count > 0)
        {
            var stale = result.StaleFiles;
            _ = Task.Run(() =>
            {
                foreach (var file in stale) HookSessionReader.RemoveIfUnchanged(_directory, file);
            });
        }

        if (result.Sessions.Count > 0) _tick.Start();
        else _tick.Stop();
        _publish(result);
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _debounce.Stop();
        _tick.Stop();
    }
}
