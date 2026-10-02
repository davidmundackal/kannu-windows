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
using System.Text.RegularExpressions;

namespace Kannu.Core;

public enum ProblemKind
{
    Crash,
    Freeze,
}

/// <summary>A problem report Kannu wrote about itself: <c>crash-&lt;utc&gt;.txt</c> or <c>freeze-&lt;utc&gt;.txt</c>.</summary>
public sealed record ProblemReportFile(ProblemKind Kind, DateTimeOffset At, string FileName);

/// <summary>
/// Kannu's own crash and freeze reports, the Windows counterpart of macOS Kannu's CrashReporter and
/// HangWatchdog: written locally when something goes wrong, offered on the next launch, and sent
/// nowhere unless the user opens the pre-filled GitHub issue. Everything that leaves the PC goes
/// through <see cref="Scrub"/> first (macOS <c>DiagnosticScrub</c>).
/// </summary>
public static partial class ProblemReports
{
    /// <summary>GitHub rejects very long issue URLs; stay well under its limit.</summary>
    public const int MaxIssueUrlLength = 7_000;

    private const string StampFormat = "yyyyMMdd'T'HHmmss'Z'";

    public static string FileName(ProblemKind kind, DateTimeOffset at) =>
        $"{(kind == ProblemKind.Crash ? "crash" : "freeze")}-{at.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture)}.txt";

    public static ProblemReportFile? Parse(string fileName)
    {
        var match = FileNamePattern().Match(fileName);
        if (!match.Success) return null;
        if (!DateTime.TryParseExact(match.Groups[2].Value, StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)) return null;
        var kind = match.Groups[1].Value == "crash" ? ProblemKind.Crash : ProblemKind.Freeze;
        return new ProblemReportFile(kind, new DateTimeOffset(at, TimeSpan.Zero), fileName);
    }

    /// <summary>
    /// The report to offer at launch: the newest one not offered before. A freeze wins over a crash
    /// written at the same moment, because a freeze is often what the user then killed.
    /// </summary>
    public static ProblemReportFile? ToOffer(IEnumerable<string> fileNames, string? lastOffered)
    {
        var last = lastOffered is null ? null : Parse(lastOffered);
        return fileNames.Select(Parse).OfType<ProblemReportFile>()
            .Where(r => last is null || r.At > last.At)
            .OrderByDescending(r => r.At).ThenBy(r => r.Kind == ProblemKind.Freeze ? 0 : 1)
            .FirstOrDefault();
    }

    /// <summary>
    /// Takes out what identifies the person or the PC: the profile folder, the user name and the
    /// computer name, in any case. Names shorter than three characters are left alone, since
    /// replacing them would mangle ordinary words.
    /// </summary>
    public static string Scrub(string text, string? home, string? userName, string? machineName)
    {
        if (!string.IsNullOrEmpty(home))
        {
            text = text.Replace(home.TrimEnd('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        foreach (var (name, stand) in new[] { (userName, "<user>"), (machineName, "<pc>") })
        {
            if (name is null || name.Length < 3) continue;
            text = Regex.Replace(text, $@"(?<![A-Za-z0-9]){Regex.Escape(name)}(?![A-Za-z0-9])", stand, RegexOptions.IgnoreCase);
        }
        return text;
    }

    /// <summary>The text written when Kannu crashes: what, where, which build.</summary>
    public static string CrashText(string exception, string version, string os, DateTimeOffset at) =>
        $"""
        Kannu for Windows {version} crashed.
        When: {at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC
        Windows: {os}

        {exception.TrimEnd()}
        """;

    /// <summary>The text written when Kannu's window stopped responding.</summary>
    public static string FreezeText(long stuckMs, string? dumpFileName, string version, string os, DateTimeOffset at) =>
        $"""
        Kannu for Windows {version} stopped responding for at least {stuckMs / 1000} seconds.
        When: {at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC
        Windows: {os}

        {(dumpFileName is null
            ? "No memory dump could be written."
            : $"A memory dump was saved as {dumpFileName} in Kannu's logs folder. It can contain private data, so do not attach it to a public issue; the maintainer will ask for it privately if needed.")}
        """;

    /// <summary>A GitHub "new issue" link with the title and body filled in, cut to a length GitHub accepts.</summary>
    public static string IssueUrl(string repositoryUrl, string title, string body)
    {
        var prefix = $"{repositoryUrl.TrimEnd('/')}/issues/new?title={Uri.EscapeDataString(title)}&body=";
        var encoded = Uri.EscapeDataString(body);
        if (prefix.Length + encoded.Length <= MaxIssueUrlLength) return prefix + encoded;

        const string cut = "\n\n(The report was cut short to fit in a link. Paste the rest from \"Copy report\".)";
        var budget = MaxIssueUrlLength - prefix.Length - Uri.EscapeDataString(cut).Length;
        var builder = new StringBuilder();
        foreach (var rune in body.EnumerateRunes())
        {
            var piece = Uri.EscapeDataString(rune.ToString());
            if (builder.Length + piece.Length > budget) break;
            builder.Append(piece);
        }
        return prefix + builder + Uri.EscapeDataString(cut);
    }

    [GeneratedRegex(@"^(crash|freeze)-(\d{8}T\d{6}Z)\.txt$")]
    private static partial Regex FileNamePattern();
}

public enum FreezeAction
{
    None,

    /// <summary>Post a ping to the UI thread.</summary>
    Ping,

    /// <summary>The UI thread has not answered for the threshold: record a freeze (once per freeze).</summary>
    Freeze,
}

/// <summary>
/// Decides when Kannu's UI thread is frozen. A watchdog thread calls <see cref="Tick"/> every
/// <see cref="IntervalMs"/>; the UI thread calls <see cref="Answered"/> when it runs the ping. A gap
/// between ticks much longer than the interval means the PC slept, which is not a freeze (macOS
/// HangWatchdog learned this the hard way). Thread-safe.
/// </summary>
public sealed class FreezeDetector(long thresholdMs = 5_000, long intervalMs = 1_000)
{
    private readonly object _lock = new();
    private long? _pingSentAt;
    private long _lastTick;
    private bool _reported;

    public long ThresholdMs { get; } = thresholdMs;

    public long IntervalMs { get; } = intervalMs;

    /// <summary>How long the outstanding ping has waited, as of the last tick.</summary>
    public long StuckMs { get; private set; }

    public FreezeAction Tick(long nowMs)
    {
        lock (_lock)
        {
            var slept = _lastTick != 0 && nowMs - _lastTick > IntervalMs * 3;
            _lastTick = nowMs;
            if (slept && _pingSentAt is not null)
            {
                // The clock jumped past a sleep: give the UI thread a fresh chance to answer.
                _pingSentAt = nowMs;
                return FreezeAction.None;
            }
            if (_pingSentAt is null)
            {
                _pingSentAt = nowMs;
                return FreezeAction.Ping;
            }
            StuckMs = nowMs - _pingSentAt.Value;
            if (_reported || StuckMs < ThresholdMs) return FreezeAction.None;
            _reported = true;
            return FreezeAction.Freeze;
        }
    }

    public void Answered()
    {
        lock (_lock)
        {
            _pingSentAt = null;
            _reported = false;
            StuckMs = 0;
        }
    }
}

/// <summary>
/// Where "start Kannu when you sign in" points. An installed Kannu lives in
/// <c>%LOCALAPPDATA%\Kannu\current\</c>, a folder Velopack replaces on every update; the stable
/// launcher is <c>%LOCALAPPDATA%\Kannu\Kannu.exe</c>, one level up. A Kannu not installed by Velopack
/// (a developer or portable build) has no stable path and offers no sign-in start.
/// </summary>
public static class LaunchAtLogin
{
    public const string RunValueName = "Kannu";

    public static string? StablePath(string appDirectory, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var dir = Path.TrimEndingDirectorySeparator(appDirectory);
        if (!string.Equals(Path.GetFileName(dir), "current", StringComparison.OrdinalIgnoreCase)) return null;
        var stub = Path.Combine(Path.GetDirectoryName(dir)!, "Kannu.exe");
        return exists(stub) ? stub : null;
    }

    public static string Command(string exePath) => $"\"{exePath}\"";

    /// <summary>
    /// Task Manager's Startup tab turns an entry off without deleting it, in
    /// <c>Explorer\StartupApproved\Run</c>: an odd first byte means disabled.
    /// </summary>
    public static bool DisabledInTaskManager(byte[]? approved) => approved is { Length: > 0 } && (approved[0] & 1) == 1;
}
