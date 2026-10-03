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
/// <c>SecurityFindingsStore</c>, including its ADR part: Kannu-run ADR Discovery scans (daily, after MCP
/// changes, on Scan now) whose snapshot findings join the rest, and ADR Detection on one chat the user picks.
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

    // ADR
    private readonly AdrScanTrigger _adrTrigger = new(AdrScanTrigger.DailyMs, AdrScanTrigger.DebounceMs);
    private List<SecurityFinding> _baseFindings = [];
    private IReadOnlyList<SecurityFinding> _discoveryFindings = [];
    private (string Path, DateTime Mtime)? _loadedSnapshot;
    private string? _lastAdrToolDirectory;
    private string? _lastAdrCheckout;
    private bool _adrChecking;
    private readonly HashSet<string> _analyzing = [];

    /// <summary>Where ADR is installed, as of the last check; null before the first.</summary>
    public AdrRunner.ToolStatus? AdrTools { get; private set; }

    public bool AdrScanning { get; private set; }

    /// <summary>Why the last Kannu-run scan produced no snapshot.</summary>
    public string? AdrLastError { get; private set; }

    /// <summary>Why the newest snapshot in the folder could not be read.</summary>
    public string? AdrSnapshotError { get; private set; }

    public AdrScanRecord? AdrLastScan => _state.AdrLastScan;
    public long? AdrLastKannuScanMs => _state.AdrLastKannuScanMs;
    public int AdrScanFailures => _state.AdrScanFailures;

    /// <summary>When the next automatic scan is due; null with <see cref="AdrAutomatic"/> on means at the next check.</summary>
    public long? AdrNextScanMs { get; private set; }

    public bool AdrAutomatic { get; private set; }

    public IReadOnlyList<AdrAnalysis> AdrAnalyses => _state.AdrAnalyses;
    public bool IsAnalyzing => _analyzing.Count > 0;
    public string? AdrAnalysisError { get; private set; }

    public string AdrSnapshotDirectory => AdrDiscovery.SnapshotDirectory(_home);

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
            if (s.AdrToolDirectory != _lastAdrToolDirectory || s.AdrDetectionCheckout != _lastAdrCheckout) CheckAdr();
            RefreshAdrNext();
            ScanSoon();
        };
        Shared = this;
    }

    public void Start()
    {
        _timer.Start();
        CheckAdr();
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

    /// <summary>Settings' Scan now: Kannu's own checks, and an ADR Discovery scan when ADR is installed.</summary>
    public Task ScanNowAsync()
    {
        if (AdrTools?.DiscoveryExe is not null) _ = RunAdrScanAsync("manual");
        return ScanAsync();
    }

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
            var adrScans = settings.AdrRunScansEnabled && AdrTools?.DiscoveryExe is not null;
            var loaded = _loadedSnapshot;
            var snapshotDirectory = AdrSnapshotDirectory;
            var (sightings, reads, locations) = await Task.Run(() => Read(settings, sessions, adrScans)).ConfigureAwait(true);
            var reload = await Task.Run(() => ReloadSnapshot(snapshotDirectory, loaded)).ConfigureAwait(true);
            if (reload is { } r)
            {
                _loadedSnapshot = (r.Path, r.Mtime);
                if (r.Snapshot is { } snapshot)
                {
                    IngestSnapshot(snapshot, AdrScanRecord.OriginWatched, Path.GetFileName(r.Path), now);
                }
                else
                {
                    AdrSnapshotError = r.Error;
                    Diagnostics.Info($"adr snapshot unreadable: {r.Error}");
                }
            }

            _state.Keep(sightings);
            if (settings.WatchMcpServers)
            {
                var additions = McpWatch.Compare(_state.McpBaseline, reads, locations, now);
                _state.McpAdditions = McpWatch.Pruning(_state.McpAdditions.Concat(additions), reads);
                if (_state.McpAdditions.Count > 50) _state.McpAdditions = _state.McpAdditions.OrderByDescending(a => a.FirstSeenMs).Take(50).ToList();
            }

            var findings = _state.Kept.Values.ToList();
            _baseFindings = findings;
            // Bypassed permission checks are about a running chat: they go when the chat does.
            foreach (var session in sessions.Where(s => s.IsUnattended && s.IsVisible && !AgentStateMachine.IsSimulation(s)))
            {
                findings.Add(SecurityFindings.Unattended(session.ConversationId, session.Provider, session.DisplayChatName, session.ProjectName, session.UpdatedAtMs));
            }
            if (settings.WatchMcpServers) findings.AddRange(_state.McpAdditions.Select(a => a.Finding(_home)));

            // A Kannu-run ADR scan: daily, sooner after the declared MCP servers changed.
            if (adrScans && !AdrScanning)
            {
                var inventory = reads.Count == 0 ? null : reads.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value.Select(server => server.Key).Order(StringComparer.Ordinal).ToList(),
                    StringComparer.OrdinalIgnoreCase);
                if (_adrTrigger.Evaluate(now, _state.AdrLastKannuScanMs, _state.AdrScanFailures, inventory) is { } reason) _ = RunAdrScanAsync(reason);
            }
            RefreshAdrNext();
            Publish(settings, now);
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

    /// <summary>Every finding there is: Kannu's own, ADR Discovery's newest snapshot, ADR Detection's verdicts.</summary>
    private void Publish(AppSettings settings, long now)
    {
        var previousDetection = All.SelectMany(g => g.Members).Where(f => f.Source == AdrAnalysis.Source)
            .GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First().FirstSeenMs);
        var detection = _state.AdrAnalyses.Select(a => a.Finding()).OfType<SecurityFinding>()
            .Select(f => previousDetection.TryGetValue(f.Id, out var seen) ? f with { FirstSeenMs = seen } : f);
        All = SecurityFindings.Group(_baseFindings.Concat(_discoveryFindings).Concat(detection));
        Visible = All.Where(g => _state.IsVisible(g, now)).ToList();
        Push(settings);
        Save(now);
        Changed?.Invoke();
    }

    /// <summary>Off the UI thread: status files and MCP configs (also read for ADR's scan trigger).</summary>
    private (List<SecurityFinding> Sightings, Dictionary<string, IReadOnlyList<McpWatch.Server>> Reads, List<McpWatch.Location> Locations)
        Read(AppSettings settings, IReadOnlyList<AgentSession> sessions, bool forAdrScans)
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
        if (settings.WatchMcpServers || forAdrScans)
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

    // ---- ADR ----

    /// <summary>Looks for ADR's tools and the Detection checkout off the UI thread ("Check again").</summary>
    public void CheckAdr()
    {
        if (_adrChecking) return;
        _adrChecking = true;
        var settings = _settings.Current;
        _lastAdrToolDirectory = settings.AdrToolDirectory;
        _lastAdrCheckout = settings.AdrDetectionCheckout;
        Task.Run(() => AdrRunner.Check(settings)).ContinueWith(task =>
        {
            _adrChecking = false;
            if (task.IsCompletedSuccessfully)
            {
                AdrTools = task.Result;
                Diagnostics.Info($"ADR check: discovery={AdrTools.DiscoveryExe is not null} uv={AdrTools.UvExe is not null} detection={AdrTools.CheckoutState}");
            }
            RefreshAdrNext();
            Changed?.Invoke();
            if (_settings.Current.AdrToolDirectory != _lastAdrToolDirectory || _settings.Current.AdrDetectionCheckout != _lastAdrCheckout) CheckAdr();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void RefreshAdrNext()
    {
        AdrAutomatic = _settings.Current.AdrRunScansEnabled && AdrTools?.DiscoveryExe is not null;
        AdrNextScanMs = AdrAutomatic && !AdrScanning ? _adrTrigger.NextScanMs(_state.AdrLastKannuScanMs, _state.AdrScanFailures) : null;
    }

    private sealed record Reloaded(string Path, DateTime Mtime, AdrSnapshot? Snapshot, string? Error);

    /// <summary>The newest snapshot in the folder, when it is not the one already read (written by Kannu or by the user's own scheduler).</summary>
    private static Reloaded? ReloadSnapshot(string directory, (string Path, DateTime Mtime)? loaded)
    {
        if (AdrSnapshot.NewestSnapshotPath(directory) is not { } path) return null;
        DateTime mtime;
        try
        {
            mtime = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (loaded is { } known && string.Equals(known.Path, path, StringComparison.OrdinalIgnoreCase) && known.Mtime == mtime) return null;
        try
        {
            return new Reloaded(path, mtime, AdrSnapshot.Load(path), null);
        }
        catch (AdrSnapshotException e)
        {
            return new Reloaded(path, mtime, null, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Reloaded(path, mtime, null, e.GetType().Name);
        }
    }

    private void IngestSnapshot(AdrSnapshot snapshot, string origin, string fileName, long now)
    {
        _discoveryFindings = snapshot.ToFindings(_discoveryFindings, now);
        AdrSnapshotError = null;
        var record = AdrScanRecord.From(snapshot, origin, fileName, now);
        if (_state.AdrLastScan?.FileName != record.FileName || _state.AdrLastScan?.FindingCount != record.FindingCount) _state.AdrLastScan = record;
    }

    private sealed record ScanOutcome(int ExitCode, AdrSnapshot? Snapshot, string? Path, string? Error);

    /// <summary>
    /// Runs the installed <c>adr-discovery --json --dry-run</c>, reads the snapshot it prints, and writes it into
    /// the snapshot folder (ADR's own write fails on Windows; see <see cref="AdrDiscovery"/>). A scan that yields no
    /// snapshot counts as a failure and brings the next one forward (1, 2, 4 … hours).
    /// </summary>
    public async Task RunAdrScanAsync(string reason)
    {
        if (AdrScanning) return;
        _adrTrigger.ScanStarted();
        if (AdrTools?.DiscoveryExe is not { } exe)
        {
            AdrLastError = "ADR Discovery is not installed.";
            Changed?.Invoke();
            return;
        }
        var settings = _settings.Current;
        var arguments = AdrDiscovery.Arguments(settings.AdrPolicyFile);
        if (!AdrDiscovery.IsValidScan(arguments)) return;
        AdrScanning = true;
        AdrLastError = null;
        RefreshAdrNext();
        Changed?.Invoke();
        Diagnostics.Info($"adr-discovery scan starting ({reason})");
        var directory = AdrSnapshotDirectory;
        var outcome = await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new ScanOutcome(-1, null, null, $"Cannot create {directory}: {e.GetType().Name}");
            }
            var run = AdrRunner.Run(exe, arguments, directory, null, AdrDiscovery.Timeout, AdrDiscovery.StdoutCap, 16_384);
            var tail = AdrRunner.Tail(run.Stderr);
            if (run.StartError is { } start) return new ScanOutcome(run.ExitCode, null, null, start);
            if (run.TimedOut) return new ScanOutcome(run.ExitCode, null, null, $"adr-discovery did not finish within {(int)AdrDiscovery.Timeout.TotalSeconds} s and was stopped.");
            if (!AdrDiscovery.ProducedSnapshot(run.ExitCode)) return new ScanOutcome(run.ExitCode, null, null, $"adr-discovery exited {run.ExitCode}. {tail}".TrimEnd());
            // Exit 2 is also argparse's usage error: a status alone proves nothing.
            if (run.Stdout.Trim().Length == 0) return new ScanOutcome(run.ExitCode, null, null, $"ADR Discovery finished but printed no snapshot. {tail}".TrimEnd());
            try
            {
                var json = run.Stdout.Trim();
                var snapshot = AdrSnapshot.Decode(json);
                var path = AdrDiscovery.WriteSnapshot(directory, AdrDiscovery.SnapshotFileName(snapshot.Timestamp, DateTimeOffset.UtcNow), json + "\n");
                return new ScanOutcome(run.ExitCode, snapshot, path, null);
            }
            catch (AdrSnapshotException e)
            {
                return new ScanOutcome(run.ExitCode, null, null, e.Message);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new ScanOutcome(run.ExitCode, null, null, $"The snapshot could not be saved: {e.GetType().Name}");
            }
        }).ConfigureAwait(true);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        AdrScanning = false;
        _state.AdrLastKannuScanMs = now;
        if (outcome.Snapshot is { } snapshot && outcome.Path is { } written)
        {
            _state.AdrScanFailures = 0;
            AdrLastError = null;
            IngestSnapshot(snapshot, AdrScanRecord.OriginKannu, Path.GetFileName(written), now);
            try
            {
                _loadedSnapshot = (written, File.GetLastWriteTimeUtc(written));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _loadedSnapshot = null;
            }
        }
        else
        {
            _state.AdrScanFailures = Math.Min(_state.AdrScanFailures + 1, 10);
            AdrLastError = outcome.Error;
        }
        Diagnostics.Info($"adr-discovery scan finished status={outcome.ExitCode}");
        RefreshAdrNext();
        Publish(_settings.Current, now);
    }

    /// <summary>
    /// ADR Detection on one finished Claude Code chat, at the user's explicit request with the opt-in on. The
    /// transcript goes to Anthropic through the user's Claude login. A failure is an error, never a clean verdict.
    /// </summary>
    public async Task AnalyzeAsync(AdrDetection.Transcript transcript)
    {
        var settings = _settings.Current;
        AdrAnalysisError = null;
        if (!settings.AdrDetectionEnabled)
        {
            AdrAnalysisError = "Session analysis is off (Settings › Security › ADR).";
        }
        else if (AdrTools is not { } tools || tools.CheckoutState != AdrDetection.CheckoutState.Ready || tools.UvExe is null)
        {
            AdrAnalysisError = "ADR Detection checkout is not ready: " + (AdrTools?.CheckoutReason ?? "not checked yet");
        }
        else if (tools.Claude.Exe is null)
        {
            AdrAnalysisError = tools.Claude.CmdOnly
                ? "Only claude.cmd (npm) was found. ADR Detection starts claude without a shell, so Windows finds only claude.exe: install Claude Code with the native installer."
                : "claude.exe was not found on PATH. Install Claude Code with the native installer and sign in.";
        }
        if (AdrAnalysisError is not null || _analyzing.Contains(transcript.ConversationId))
        {
            Changed?.Invoke();
            return;
        }
        var id = transcript.ConversationId;
        if (id.Length == 0 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            AdrAnalysisError = "That transcript's name is not a Claude Code chat id.";
            Changed?.Invoke();
            return;
        }
        var uv = AdrTools!.UvExe!;
        var checkout = settings.AdrDetectionCheckout.Trim().Trim('"');
        var directory = AdrDetection.Directory(_home);
        var adapter = Path.Combine(directory, AdrAdapter.FileName);
        var report = Path.Combine(directory, id + ".json");
        var options = new AdrDetection.Options();
        var arguments = AdrDetection.Arguments(checkout, adapter, transcript.Path, report, options);
        if (!AdrDetection.IsValidAnalysis(arguments))
        {
            AdrAnalysisError = "invalid invocation";
            Changed?.Invoke();
            return;
        }
        var environment = AdrRunner.DetectionEnvironment(uv);
        var chatName = _sessions.FirstOrDefault(s => s.ConversationId == id)?.DisplayChatName;
        _analyzing.Add(id);
        Changed?.Invoke();
        Diagnostics.Info($"session analysis starting ({id[..Math.Min(8, id.Length)]})");

        var result = await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(adapter, AdrAdapter.Source, new System.Text.UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return (Analysis: (AdrAnalysis?)null, Error: (string?)e.Message);
            }
            var run = AdrRunner.Run(uv, arguments, checkout, environment, AdrDetection.ProcessTimeout(options.TimeoutSeconds), 1_000_000, 64_000);
            if (run.StartError is { } start) return (null, start);
            if (run.TimedOut) return (null, $"Analysis did not finish within {(int)AdrDetection.ProcessTimeout(options.TimeoutSeconds).TotalSeconds} s and was stopped.");
            // The verdict is the last JSON line on stdout; upstream may print progress above it.
            var last = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
            try
            {
                return (AdrAnalysis.Parse(last, id, chatName, report, transcript.Bytes, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), null);
            }
            catch (AdrVerdictException e) when (e.FromAdapter)
            {
                // WinError 206: the transcript did not fit Windows' 32,767-character command line.
                var hint = e.Message.Contains("WinError 206", StringComparison.Ordinal) || e.Message.Contains("too long", StringComparison.OrdinalIgnoreCase)
                    ? " This chat is too long for Windows' command-line limit, even trimmed to the newest " + AdrDetection.WindowsMaxCharacters.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " characters."
                    : "";
                return (null, e.Message + hint);
            }
            catch (AdrVerdictException)
            {
                return (null, $"adr adapter exited {run.ExitCode} without a verdict. {AdrRunner.Tail(run.Stderr, 300)}".TrimEnd());
            }
        }).ConfigureAwait(true);

        _analyzing.Remove(id);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (result.Analysis is { } analysis)
        {
            _state.AdrAnalyses = new[] { analysis }.Concat(_state.AdrAnalyses.Where(a => a.ConversationId != id)).Take(AdrAnalysis.Cap).ToList();
            Diagnostics.Info($"session analysis finished malicious={analysis.IsMalicious} confidence={analysis.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
        }
        else
        {
            AdrAnalysisError = result.Error;
            Diagnostics.Info("session analysis failed");
        }
        Publish(_settings.Current, now);
    }

    public void ForgetAnalysis(string conversationId)
    {
        _state.AdrAnalyses = _state.AdrAnalyses.Where(a => a.ConversationId != conversationId).ToList();
        Publish(_settings.Current, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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
