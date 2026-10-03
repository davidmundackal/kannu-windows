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

using System.Globalization;
using System.Text;

namespace Kannu.Core;

/// <summary>
/// How Kannu for Windows invokes the user's <c>adr-discovery</c>, held as data so tests pin it (port of macOS
/// <c>ADRDiscoveryCommand</c>, with one deliberate difference).
/// <para>
/// macOS runs <c>--json --output-dir &lt;folder&gt;</c> and lets ADR write the snapshot. On Windows ADR's own
/// write fails: <c>cli.py</c> <c>_write_private</c> opens the output folder with <c>os.open</c>, which Windows'
/// Python refuses for a directory. The snapshot is printed to stdout before that write, so Kannu runs
/// <c>--json --dry-run</c> (prints, writes nothing), reads stdout, checks it is a snapshot, and writes
/// <c>snapshot-&lt;timestamp&gt;.json</c> into its folder itself, under the name ADR would have used.
/// </para>
/// Never <c>--root</c> (scans a fixture tree, not this PC), <c>--diff</c> or <c>--explain</c> (which prints
/// prose ahead of the JSON).
/// </summary>
public static class AdrDiscovery
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitPartial = 2;

    public const string ToolName = "adr-discovery";

    /// <summary>A hung scan must not pin a worker forever.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    /// <summary>A real snapshot is a few MB (macOS measured ~7 MB). Anything past this is not read.</summary>
    public const int StdoutCap = 64 * 1024 * 1024;

    public const string InstallCommand = "uv tool install \"adr-discovery @ git+https://github.com/uber/ADR#subdirectory=Discovery\"";
    public const string PipxCommand = "pipx install \"git+https://github.com/uber/ADR#subdirectory=Discovery\"";

    public static readonly IReadOnlySet<string> ForbiddenArguments = new HashSet<string> { "--root", "--diff", "--explain", "--output-dir" };

    public static IReadOnlyList<string> Arguments(string? policyFile)
    {
        var args = new List<string> { "--json", "--dry-run" };
        if (!string.IsNullOrWhiteSpace(policyFile))
        {
            args.Add("--policy");
            args.Add(policyFile.Trim());
        }
        return args;
    }

    /// <summary>True when the arguments print a real snapshot of this machine to stdout.</summary>
    public static bool IsValidScan(IReadOnlyList<string> arguments) =>
        arguments.Contains("--json") && arguments.Contains("--dry-run") && !arguments.Any(ForbiddenArguments.Contains);

    /// <summary>0 complete, 2 partial coverage (still a snapshot), 1 error. Exit 2 is also argparse's usage error, so stdout must parse too.</summary>
    public static bool ProducedSnapshot(int exitCode) => exitCode is ExitOk or ExitPartial;

    public static string SnapshotDirectory(string home) => Path.Combine(home, ".kannu", "adr", "discovery");

    /// <summary>ADR's own name, <c>snapshot-{timestamp without ':'}.json</c>; the current UTC time when the snapshot gave none.</summary>
    public static string SnapshotFileName(string timestamp, DateTimeOffset nowUtc)
    {
        var stamp = timestamp.Replace(":", "", StringComparison.Ordinal);
        if (stamp.Length == 0 || stamp.Length > 64 || stamp.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '+' or '.')))
        {
            stamp = nowUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HHmmss'+0000'", CultureInfo.InvariantCulture);
        }
        return $"snapshot-{stamp}.json";
    }

    /// <summary>Writes the snapshot text as a new file (never over an existing one), atomically.</summary>
    public static string WriteSnapshot(string directory, string fileName, string json)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, fileName);
        for (var n = 2; File.Exists(target) && n < 100; n++)
        {
            target = Path.Combine(directory, Path.GetFileNameWithoutExtension(fileName) + "-" + n.ToString(CultureInfo.InvariantCulture) + ".json");
        }
        var temp = Path.Combine(directory, "." + Path.GetFileName(target) + ".tmp");
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, target, overwrite: false);
        return target;
    }

    /// <summary>Snapshots kept in the folder; the oldest Kannu-written ones beyond this are removed.</summary>
    public const int KeepSnapshots = 10;

    // ---- Locating the tools ----

    /// <summary>
    /// Folders searched, in order: the user's choice; uv's and pipx's bin folder (<c>%USERPROFILE%\.local\bin</c> on
    /// Windows); <c>PIPX_BIN_DIR</c>; uv's tool environments (<c>%APPDATA%\uv\tools\&lt;tool&gt;\Scripts</c>); then PATH.
    /// For <c>uv.exe</c> also cargo's and winget's link folders.
    /// </summary>
    public static IReadOnlyList<string> CandidateDirectories(string? userDirectory, string home, string appData, string localAppData,
        string? pipxBinDir, string? pathEnv)
    {
        var dirs = new List<string>();
        void Add(string? dir)
        {
            dir = dir?.Trim().Trim('"');
            if (string.IsNullOrEmpty(dir)) return;
            if (dir.StartsWith('~')) dir = home + dir[1..];
            if (!dirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) dirs.Add(dir);
        }
        Add(userDirectory);
        Add(Path.Combine(home, ".local", "bin"));
        Add(pipxBinDir);
        Add(Path.Combine(appData, "uv", "tools", ToolName, "Scripts"));
        Add(Path.Combine(home, ".cargo", "bin"));
        Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links"));
        foreach (var entry in (pathEnv ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)) Add(entry);
        return dirs;
    }

    /// <summary>The first <c>&lt;name&gt;.exe</c> in the folders.</summary>
    public static string? Locate(string name, IEnumerable<string> directories, Func<string, bool> exists)
    {
        foreach (var dir in directories)
        {
            string candidate;
            try
            {
                candidate = Path.Combine(dir, name + ".exe");
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (exists(candidate)) return candidate;
        }
        return null;
    }

    public static readonly IReadOnlyList<string> UvToolListArguments = ["tool", "list"];

    /// <summary><c>uv tool list</c> prints one <c>name vX.Y.Z</c> line per tool; <c>adr-discovery</c> has no <c>--version</c>.</summary>
    public static string? VersionFromUvToolList(string text, string tool)
    {
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] != tool) continue;
            var version = parts[1].StartsWith('v') ? parts[1][1..] : parts[1];
            return version.Length > 32 ? version[..32] : version;
        }
        return null;
    }
}

/// <summary>
/// When Kannu runs its own Discovery scan (port of macOS <c>ADRScanTrigger</c>): daily; sooner when the MCP
/// servers an agent's settings declare changed, at most once per debounce (server sets compared, not file
/// dates); after a scan that produced no snapshot, again after 1, 2, 4, 8, 16 hours, never later than daily.
/// </summary>
public sealed class AdrScanTrigger
{
    public const string Scheduled = "scheduled";
    public const string ConfigChanged = "config changed";
    public const string Retry = "retry after a failed scan";

    public static readonly long DailyMs = 24 * 3600_000L;
    public static readonly long DebounceMs = 300_000L;

    public long IntervalMs { get; }
    public long DebounceIntervalMs { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Baseline { get; private set; }
    public bool PendingChange { get; private set; }

    public AdrScanTrigger(long intervalMs, long debounceMs)
    {
        IntervalMs = intervalMs;
        DebounceIntervalMs = debounceMs;
    }

    public static long RetryDelayMs(int consecutiveFailures, long intervalMs) =>
        consecutiveFailures <= 0 ? intervalMs : Math.Min(3600_000L * (1L << (Math.Min(consecutiveFailures, 10) - 1)), intervalMs);

    /// <summary><paramref name="inventory"/> null: the settings were not read this time; nothing learned, nothing lost.</summary>
    public string? Evaluate(long nowMs, long? lastScanMs, int consecutiveFailures, IReadOnlyDictionary<string, IReadOnlyList<string>>? inventory)
    {
        if (inventory is not null)
        {
            if (Baseline is not null && !Same(Baseline, inventory)) PendingChange = true;
            Baseline = inventory;
        }
        var since = lastScanMs is { } last ? nowMs - last : long.MaxValue;
        if (since >= RetryDelayMs(consecutiveFailures, IntervalMs))
        {
            PendingChange = false;
            return consecutiveFailures > 0 && since < IntervalMs ? Retry : Scheduled;
        }
        if (PendingChange && since >= ChangeDelay(consecutiveFailures))
        {
            PendingChange = false;
            return ConfigChanged;
        }
        return null;
    }

    /// <summary>When the next automatic scan is due; null means at the next check (Kannu has not run one yet).</summary>
    public long? NextScanMs(long? lastScanMs, int consecutiveFailures)
    {
        if (lastScanMs is not { } last) return null;
        var due = last + RetryDelayMs(consecutiveFailures, IntervalMs);
        return PendingChange ? Math.Min(due, last + ChangeDelay(consecutiveFailures)) : due;
    }

    /// <summary>Any scan covers a pending change.</summary>
    public void ScanStarted() => PendingChange = false;

    private long ChangeDelay(int consecutiveFailures) =>
        consecutiveFailures > 0 ? Math.Max(DebounceIntervalMs, RetryDelayMs(consecutiveFailures, IntervalMs)) : DebounceIntervalMs;

    private static bool Same(IReadOnlyDictionary<string, IReadOnlyList<string>> a, IReadOnlyDictionary<string, IReadOnlyList<string>> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && pair.Value.SequenceEqual(other));
}
