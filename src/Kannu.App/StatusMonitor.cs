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
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Kannu.Core;
using Kannu.Detection;

namespace Kannu.App;

/// <summary>
/// Watches the status directory and publishes the session list on the UI thread. Hook files are read
/// only when the directory changes (debounced, off the UI thread). A tick re-runs the pipeline with
/// fresh passive evidence (Claude's session records and transcript tails, read off the UI thread and
/// cached against mtime and size): every second while a card is listed, so red collapses and stale
/// lights dim on time, and every five seconds otherwise, to notice a Claude session with no hooks.
/// </summary>
internal sealed class StatusMonitor : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan BusyTick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleTick = TimeSpan.FromSeconds(5);

    private readonly string _directory;
    private readonly Action<PipelineResult> _publish;
    private readonly Action<IReadOnlyDictionary<string, TurnTokens>> _publishTokens;
    private readonly TurnTokenReader _tokens;
    private readonly string _home;
    private bool _readingTokens;
    private readonly AgentSessionPipeline _pipeline;
    private readonly SessionLogParser _logs;
    private readonly CursorPassive _cursor;
    private readonly WarpStore _warp = new(WarpStore.DefaultCandidates());
    private readonly ClaudeDesktopAgentStore _desktop = new(ClaudeDesktopAgentStore.DefaultRoot());
    private readonly FileSystemWatcher _watcher;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _tick;
    private readonly Dispatcher _dispatcher;

    private IReadOnlyList<HookFile> _files = [];
    private bool _loading;
    private bool _dirty;
    private bool _refreshing;
    private bool _refreshAgain;

    public StatusMonitor(string directory, Action<PipelineResult> publish, Action<IReadOnlyDictionary<string, TurnTokens>> publishTokens)
    {
        _directory = directory;
        _publish = publish;
        _publishTokens = publishTokens;
        _dispatcher = Dispatcher.CurrentDispatcher;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _home = home;
        _tokens = new TurnTokenReader(Path.Combine(home, ".claude", "projects"));
        _pipeline = new AgentSessionPipeline(new AgentTimings(), home);
        _logs = new SessionLogParser(home);
        _cursor = new CursorPassive(new CursorTranscripts(home), new CursorDatabase(CursorDatabase.DefaultUserDirectory()));

        _debounce = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = Debounce };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Reload();
        };

        _tick = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = IdleTick };
        _tick.Tick += (_, _) => Refresh();

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
        _tick.Start();
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
                Refresh();
            } while (_dirty);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Passive evidence off the UI thread, then one pipeline pass on it. Never two at once.</summary>
    private async void Refresh()
    {
        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }

        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                var timings = _pipeline.Timings;
                var hookCursorIds = _files.Where(f => f.Record.Provider == "cursor")
                    .Select(f => f.Key["cursor-".Length..]).ToHashSet();
                var (claude, cursor, names, others) = await Task.Run(() =>
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var claudeScan = ClaudePassiveScanner.Scan(_logs, timings, now, ProcessProbe.Of);
                    // Cursor's transcripts and database are read only while Cursor runs.
                    var cursorScan = CursorPassive.IsCursorRunning() ? _cursor.Scan(timings, now, hookCursorIds) : CursorScan.None;
                    // Agents with no hooks at all: Warp's agent mode and Claude Desktop's agent mode.
                    IReadOnlyList<AgentSession> others =
                    [
                        .. cursorScan.Sessions,
                        .. _warp.Sessions(timings, now, WarpStore.IsWarpRunning()),
                        .. _desktop.Sessions(timings, now),
                    ];
                    return (claudeScan, cursorScan, CodexNames(timings, now), others);
                });
                Publish(new PassiveEvidence(claude.Sessions, claude.DeadPidConversationIds, claude.LiveTails,
                    cursor.PendingApprovalIds, others, cursor.Analysis, cursor.SubagentParents, cursor.TitleSources, names));
            } while (_refreshAgain);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Codex hook files carry no title: name them from Codex's own session logs.</summary>
    private Dictionary<string, (string? Name, string? Project)> CodexNames(AgentTimings timings, long nowMs)
    {
        var names = new Dictionary<string, (string?, string?)>();
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime;
        foreach (var path in _logs.RecentSessionPaths(SessionLogProvider.Codex, timings.StaleMinutes, nowUtc))
        {
            names["codex|" + SessionLogParser.SessionId(path, SessionLogProvider.Codex)] =
                (_logs.DisplayChatName(path, SessionLogProvider.Codex), _logs.ProjectName(path, SessionLogProvider.Codex));
        }
        return names;
    }

    private void Publish(PassiveEvidence evidence)
    {
        var result = _pipeline.Update(_files, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), evidence);
        if (result.StaleFiles.Count > 0)
        {
            var stale = result.StaleFiles;
            _ = Task.Run(() =>
            {
                foreach (var file in stale) HookSessionReader.RemoveIfUnchanged(_directory, file);
            });
        }

        _tick.Interval = result.Sessions.Count > 0 ? BusyTick : IdleTick;
        _publish(result);
        ReadTokens(result.Sessions);
    }

    /// <summary>The cards' token totals, read off the UI thread, one pass at a time (macOS REGRESSIONS entry 11).</summary>
    private async void ReadTokens(IReadOnlyList<AgentSession> sessions)
    {
        var requests = TurnTokenRequest.From(sessions, _home);
        if (_readingTokens || requests.Count == 0) return;
        _readingTokens = true;
        try
        {
            var pass = await Task.Run(() => _tokens.Read(requests, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            _publishTokens(pass.Tokens);
        }
        finally
        {
            _readingTokens = false;
        }
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _debounce.Stop();
        _tick.Stop();
    }
}
