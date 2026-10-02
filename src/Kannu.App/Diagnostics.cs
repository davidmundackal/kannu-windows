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
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// Kannu's own log, crash reports and log export, in <c>%LOCALAPPDATA%\Kannu\logs</c>. Nothing here
/// leaves the PC by itself: reports are offered on the next launch (<see cref="ProblemReportWindow"/>)
/// and go to GitHub only if the user opens the pre-filled issue. Never log agent conversations,
/// prompts, file contents or credentials.
/// </summary>
internal static class Diagnostics
{
    private const long MaxLogBytes = 1_000_000;
    private static readonly object LogLock = new();
    private static int _crashWritten;

    public static string LogsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kannu", "logs");

    private static string LogFile => Path.Combine(LogsDirectory, "kannu.log");

    public static string OsDescription => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(LogsDirectory);
                if (File.Exists(LogFile) && new FileInfo(LogFile).Length > MaxLogBytes)
                {
                    File.Move(LogFile, Path.Combine(LogsDirectory, "kannu.1.log"), overwrite: true);
                }
                File.AppendAllText(LogFile, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z {level} {message}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be written must never take Kannu down.
        }
    }

    /// <summary>Records every crash path. Called once, before the first window.</summary>
    public static void InstallCrashHandlers(Application app)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception);
        app.DispatcherUnhandledException += (_, e) => WriteCrash(e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Error("Unobserved task exception", e.Exception.GetBaseException());
            e.SetObserved();
        };
        Info($"Kannu {ReleaseInfo.Version} ({ReleaseInfo.Codename}) started on {OsDescription}");
    }

    /// <summary>The crash report, written once per run; Kannu then ends as it would have.</summary>
    private static void WriteCrash(Exception? exception)
    {
        if (exception is null || Interlocked.Exchange(ref _crashWritten, 1) == 1) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            Directory.CreateDirectory(LogsDirectory);
            var text = ProblemReports.CrashText(exception.ToString(), ReleaseInfo.Version, OsDescription, now);
            File.WriteAllText(Path.Combine(LogsDirectory, ProblemReports.FileName(ProblemKind.Crash, now)), Scrub(text));
            Error("Crashed", exception);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static string Scrub(string text) => ProblemReports.Scrub(text,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName);

    /// <summary>
    /// A zip of the logs and reports, each scrubbed; memory dumps are left out because they can hold
    /// private data (the folder can be opened for those).
    /// </summary>
    public static void ExportLogs(string zipPath)
    {
        Directory.CreateDirectory(LogsDirectory);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(LogsDirectory)
                     .Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
        {
            string text;
            try
            {
                // Shared read: the log may be open for writing.
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            catch (IOException)
            {
                continue;
            }
            var entry = zip.CreateEntry(Path.GetFileName(file));
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(Scrub(text));
        }
        var about = zip.CreateEntry("about.txt");
        using (var writer = new StreamWriter(about.Open(), new UTF8Encoding(false)))
        {
            writer.Write($"Kannu for Windows {ReleaseInfo.Version} ({ReleaseInfo.Codename})\nWindows: {OsDescription}\nExported: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n");
        }
    }

    /// <summary>"Report a problem" from Settings: a new issue with the build and Windows version filled in.</summary>
    public static string NewIssueUrl() => ProblemReports.IssueUrl(ReleaseInfo.RepositoryUrl, "",
        $"**What happened**\n\n\n**What you expected**\n\n\n**Steps to reproduce**\n\n\n---\nKannu for Windows {ReleaseInfo.Version} ({ReleaseInfo.Codename})\nWindows: {OsDescription}\n");
}

/// <summary>
/// Notices when Kannu's UI thread stops answering (<see cref="FreezeDetector"/>) and writes a freeze
/// report plus a memory dump of the stuck process, the Windows counterpart of macOS Kannu's
/// HangWatchdog. A background thread wakes once a second; it never touches the UI.
/// </summary>
internal sealed class FreezeWatchdog : IDisposable
{
    private readonly FreezeDetector _detector = new();
    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private volatile bool _stopped;

    public FreezeWatchdog(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _thread = new Thread(Run) { IsBackground = true, Name = "Kannu freeze watchdog", Priority = ThreadPriority.BelowNormal };
    }

    public void Start() => _thread.Start();

    private void Run()
    {
        while (!_stopped)
        {
            Thread.Sleep((int)_detector.IntervalMs);
            switch (_detector.Tick(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                case FreezeAction.Ping:
                    _dispatcher.BeginInvoke(DispatcherPriority.Normal, _detector.Answered);
                    break;
                case FreezeAction.Freeze:
                    WriteFreeze(_detector.StuckMs);
                    break;
            }
        }
    }

    private static void WriteFreeze(long stuckMs)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            Directory.CreateDirectory(Diagnostics.LogsDirectory);
            var reportName = ProblemReports.FileName(ProblemKind.Freeze, now);
            var dumpName = Path.ChangeExtension(reportName, ".dmp");
            // One dump at a time: they are large.
            foreach (var old in Directory.EnumerateFiles(Diagnostics.LogsDirectory, "freeze-*.dmp")) File.Delete(old);
            var dumped = Interop.NativeMethods.WriteMiniDump(Path.Combine(Diagnostics.LogsDirectory, dumpName));
            var text = ProblemReports.FreezeText(stuckMs, dumped ? dumpName : null, ReleaseInfo.Version, Diagnostics.OsDescription, now);
            File.WriteAllText(Path.Combine(Diagnostics.LogsDirectory, reportName), Diagnostics.Scrub(text));
            Diagnostics.Error($"The UI thread did not answer for {stuckMs} ms; freeze report written");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => _stopped = true;
}
