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

namespace Kannu.App;

/// <summary>
/// Agent Security on Windows: turns what the hook recorded (hidden text, secrets, sensitive files,
/// policy matches, bypassed permission checks) and new MCP servers into findings, keeps what the user
/// acknowledged or snoozed, tells the hook which checks are on (marker files in the status folder),
/// and sends one notification per high finding. Everything runs locally. Port of macOS
/// <c>SecurityFindingsStore</c> without ADR (D5: no Windows build of it to run).
/// </summary>
internal sealed class SecurityMonitor : IDisposable
{
    public static SecurityMonitor? Shared { get; private set; }

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly string _statusDirectory;
    private readonly SettingsStore _settings;
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kannu", "security.json");
    private readonly string _home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string _appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _soon;
    private readonly SecurityState _state;
    private readonly Dictionary<string, (DateTime Mtime, long Size, IReadOnlyList<McpWatch.Server>? Servers)> _mcpCache =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AgentSession> _sessions = [];
    private bool _busy;

    /// <summary>Every finding group, visible or not, newest scan.</summary>
    public IReadOnlyList<FindingGroup> All { get; private set; } = [];

    /// <summary>What the user has not acknowledged or snoozed.</summary>
    public IReadOnlyList<FindingGroup> Visible { get; private set; } = [];

    public event Action? Changed;

    public SecurityMonitor(string statusDirectory, SettingsStore settings)
    {
        _statusDirectory = statusDirectory;
        _settings = settings;
        _state = SecurityState.Load(_statePath);
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => _ = ScanAsync();
        _soon = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _soon.Tick += (_, _) =>
        {
            _soon.Stop();
            _ = ScanAsync();
        };
        SyncMarkers(settings.Current);
        settings.Changed += s =>
        {
            SyncMarkers(s);
            ScanSoon();
        };
        Shared = this;
    }

    public void Start()
    {
        _timer.Start();
        _ = ScanAsync();
    }

    /// <summary>Every rescan's sessions: their status files may carry new sightings.</summary>
    public void Update(IReadOnlyList<AgentSession> sessions)
    {
        _sessions = sessions;
        ScanSoon();
    }

    public void ScanSoon()
    {
        _soon.Stop();
        _soon.Start();
    }

    public Task ScanNowAsync() => ScanAsync();

    // ---- The hook's switches ----

    /// <summary>Marker files the hook reads (the macOS contract): a check is off when its marker exists.</summary>
    private void SyncMarkers(AppSettings s)
    {
        void Marker(string name, bool present)
        {
            var path = Path.Combine(_statusDirectory, name);
            try
            {
                if (present && !File.Exists(path))
                {
                    Directory.CreateDirectory(_statusDirectory);
                    File.WriteAllText(path, "");
                }
                else if (!present && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Diagnostics.Info($"security marker {name}: {e.GetType().Name}");
            }
        }
        Marker(HookSecurity.HiddenTextOffMarker, !s.DetectHiddenText);
        Marker(HookSecurity.HiddenTextWarnMarker, s.DetectHiddenText && s.WarnAgentAboutHiddenText);
        Marker(HookSecurity.SecretsOffMarker, !s.DetectSecrets);
        Marker(HookSecurity.SensitivePathsOffMarker, !s.DetectSensitivePaths);
        Marker(HookSecurity.PolicyEnforceMarker, s.EnforceAgentPolicy);

        // Turning a check off forgets what it saw (macOS does the same).
        if (!s.DetectHiddenText) _state.Forget("hidden_text");
        if (!s.DetectSecrets) _state.Forget("secrets");
        if (!s.DetectSensitivePaths) _state.Forget("sensitive_paths");
        if (!s.WatchMcpServers) _state.McpAdditions.Clear();
    }

    // ---- Scanning ----

    private async Task ScanAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var settings = _settings.Current;
            var sessions = _sessions;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var (sightings, reads, locations) = await Task.Run(() => Read(settings, sessions)).ConfigureAwait(true);

            _state.Keep(sightings);
            if (settings.WatchMcpServers)
            {
                var additions = McpWatch.Compare(_state.McpBaseline, reads, locations, now);
                _state.McpAdditions = McpWatch.Pruning(_state.McpAdditions.Concat(additions), reads);
                if (_state.McpAdditions.Count > 50) _state.McpAdditions = _state.McpAdditions.OrderByDescending(a => a.FirstSeenMs).Take(50).ToList();
            }

            var findings = _state.Kept.Values.ToList();
            // Bypassed permission checks are about a running chat: they go when the chat does.
            foreach (var session in sessions.Where(s => s.IsUnattended && s.IsVisible && !AgentStateMachine.IsSimulation(s)))
            {
                findings.Add(SecurityFindings.Unattended(session.ConversationId, session.Provider, session.DisplayChatName, session.ProjectName, session.UpdatedAtMs));
            }
            if (settings.WatchMcpServers) findings.AddRange(_state.McpAdditions.Select(a => a.Finding(_home)));

            All = SecurityFindings.Group(findings);
            Visible = All.Where(g => _state.IsVisible(g, now)).ToList();
            Push(settings);
            Save(now);
            Changed?.Invoke();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Diagnostics.Info($"security scan: {e.GetType().Name}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Off the UI thread: status files and MCP configs.</summary>
    private (List<SecurityFinding> Sightings, Dictionary<string, IReadOnlyList<McpWatch.Server>> Reads, List<McpWatch.Location> Locations)
        Read(AppSettings settings, IReadOnlyList<AgentSession> sessions)
    {
        var sightings = new List<SecurityFinding>();
        foreach (var file in HookSessionReader.ReadFiles(_statusDirectory))
        {
            var r = file.Record;
            var conversation = file.Key;
            var provider = r.Provider.Length > 0 ? r.Provider : file.Key.Split('-')[0];
            var chat = sessions.FirstOrDefault(s => s.Id == file.Key)?.DisplayChatName ?? r.Name ?? r.Project ?? "a chat";
            if (settings.DetectHiddenText)
            {
                foreach (var e in r.HiddenText ?? [])
                {
                    sightings.Add(SecurityFindings.HiddenText(conversation, provider, chat, e.Kind, e.Where, NullIfEmpty(e.Tool), e.Chars, e.Preview ?? "", e.FirstTs, e.LastTs, e.Events));
                }
            }
            if (settings.DetectSecrets)
            {
                foreach (var e in r.Secrets ?? [])
                {
                    sightings.Add(SecurityFindings.Secret(conversation, provider, chat, e.Kind, e.Where, NullIfEmpty(e.Tool), e.Prefix ?? "", e.Length, e.Fp ?? "", e.FirstTs, e.LastTs, e.Events));
                }
            }
            if (settings.DetectSensitivePaths)
            {
                foreach (var e in r.SensitivePaths ?? [])
                {
                    sightings.Add(SecurityFindings.SensitivePath(conversation, provider, chat, e.Category, e.Access, e.Path, NullIfEmpty(e.Tool), e.Failed, e.FirstTs, e.LastTs, e.Events));
                }
            }
            foreach (var e in r.Policy ?? [])
            {
                sightings.Add(SecurityFindings.Policy(conversation, provider, chat, e.Kind, e.Matched, NullIfEmpty(e.Tool), e.Blocked, e.FirstTs, e.LastTs, e.Events));
            }
        }

        var reads = new Dictionary<string, IReadOnlyList<McpWatch.Server>>(StringComparer.OrdinalIgnoreCase);
        var locations = new List<McpWatch.Location>();
        if (settings.WatchMcpServers)
        {
            locations.AddRange(McpWatch.GlobalLocations(_home, _appData));
            foreach (var root in McpWatch.ProjectRoots(sessions.Select(s => s.Cwd), _home)) locations.AddRange(McpWatch.ProjectLocations(root));
            foreach (var location in locations)
            {
                if (ReadMcp(location) is { } servers) reads[location.Path] = servers;
            }
        }
        return (sightings, reads, locations);
    }

    /// <summary>A missing file declares no servers; an unreadable one is left out, so what Kannu knew about it stays.</summary>
    private IReadOnlyList<McpWatch.Server>? ReadMcp(McpWatch.Location location)
    {
        try
        {
            var info = new FileInfo(location.Path);
            if (!info.Exists) return [];
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > 8 * 1024 * 1024) return null;
            if (_mcpCache.TryGetValue(location.Path, out var cached) && cached.Mtime == info.LastWriteTimeUtc && cached.Size == info.Length)
            {
                return cached.Servers;
            }
            using var stream = new FileStream(location.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var servers = McpWatch.Servers(reader.ReadToEnd(), location.Format, location.Lenient);
            _mcpCache[location.Path] = (info.LastWriteTimeUtc, info.Length, servers);
            return servers;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- Notifications ----

    /// <summary>Once per group (macOS: adrPushedFindingIDs, trimmed to what is still eligible, so a group that returns is sent again).</summary>
    private void Push(AppSettings s)
    {
        var eligible = Visible.Where(g => g.Severity == FindingSeverity.High && s.PushHighFindings
                                          || g.Severity == FindingSeverity.Medium && s.PushMediumFindings).ToList();
        _state.Pushed.IntersectWith(eligible.Select(g => g.Id));
        foreach (var group in eligible)
        {
            if (!_state.Pushed.Add(group.Id)) continue;
            var finding = group.Representative;
            NotificationManager.Shared?.Alert(new PushPayload(
                "Security finding: " + finding.Title,
                finding.PushBody,
                group.Severity == FindingSeverity.High ? 5 : 4,
                "security-finding",
                "security_finding"));
        }
    }

    // ---- User actions ----

    public void Acknowledge(FindingGroup group) => Act(() => _state.Acknowledge(group, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    public void Snooze(FindingGroup group) => Act(() => _state.Snooze(group, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    public void Unhide() => Act(_state.Unhide);

    public bool IsHidden(FindingGroup group) => !_state.IsVisible(group, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private void Act(Action change)
    {
        change();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Visible = All.Where(g => _state.IsVisible(g, now)).ToList();
        Save(now);
        Changed?.Invoke();
    }

    private void Save(long now)
    {
        try
        {
            _state.Save(_statePath, now);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Info($"security state not saved: {e.GetType().Name}");
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    public void Dispose()
    {
        _timer.Stop();
        _soon.Stop();
        if (Shared == this) Shared = null;
    }
}
