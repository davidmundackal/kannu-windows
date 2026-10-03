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
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// The connection to Uber's ADR (github.com/uber/ADR, Apache-2.0), a separate install the user owns. Kannu
/// ships no Python, runs no package manager and installs nothing: it finds <c>adr-discovery.exe</c> and
/// <c>uv.exe</c> where uv and pipx put them, and runs them bounded, without a console window. Port of macOS
/// <c>ADRConnection</c> plus its process runner. Every argument list comes from <see cref="AdrDiscovery"/> or
/// <see cref="AdrDetection"/>, pinned by tests.
/// </summary>
internal static class AdrRunner
{
    internal sealed record ToolStatus(
        string? DiscoveryExe,
        string? DiscoveryVersion,
        string? UvExe,
        AdrDetection.CheckoutState CheckoutState,
        string CheckoutReason,
        AdrDetection.ClaudeResolution Claude,
        long CheckedAtMs)
    {
        public bool DetectionReady => CheckoutState == AdrDetection.CheckoutState.Ready && UvExe is not null && Claude.Exe is not null;
    }

    internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, string? StartError);

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static IReadOnlyList<string> Directories(AppSettings s) => AdrDiscovery.CandidateDirectories(
        s.AdrToolDirectory, Home,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable("PIPX_BIN_DIR"),
        Environment.GetEnvironmentVariable("PATH"));

    /// <summary>Blocking (a few stats and at most one bounded <c>uv tool list</c>): call off the UI thread.</summary>
    public static ToolStatus Check(AppSettings s)
    {
        var dirs = Directories(s);
        var discovery = AdrDiscovery.Locate(AdrDiscovery.ToolName, dirs, File.Exists);
        var uv = AdrDiscovery.Locate("uv", dirs, File.Exists);
        string? version = null;
        if (discovery is not null && uv is not null)
        {
            var listed = Run(uv, AdrDiscovery.UvToolListArguments, null, null, TimeSpan.FromSeconds(5), 64_000, 4_096);
            if (listed.StartError is null && !listed.TimedOut) version = AdrDiscovery.VersionFromUvToolList(listed.Stdout, AdrDiscovery.ToolName);
        }
        var (state, reason) = AdrDetection.ValidateCheckout(s.AdrDetectionCheckout, uv, File.Exists, Directory.Exists);
        var claude = AdrDetection.ResolveClaude(DetectionPath(uv).Split(';', StringSplitOptions.RemoveEmptyEntries), File.Exists);
        return new ToolStatus(discovery, version, uv, state, reason, claude, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// The PATH Detection runs with: uv's folder and the native Claude installer's folder first, then the
    /// user's own PATH (Node, Git, a winget-installed Claude). Detection's MCP servers start through <c>uv</c>.
    /// </summary>
    public static string DetectionPath(string? uv)
    {
        var parts = new List<string>();
        if (uv is not null && Path.GetDirectoryName(uv) is { } uvDir) parts.Add(uvDir);
        parts.Add(Path.Combine(Home, ".local", "bin"));
        parts.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        return string.Join(";", parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static IReadOnlyDictionary<string, string> DetectionEnvironment(string? uv) => AdrDetection.Environment(
        DetectionPath(uv), Home,
        Environment.GetEnvironmentVariable("APPDATA"),
        Environment.GetEnvironmentVariable("LOCALAPPDATA"),
        Environment.GetEnvironmentVariable("SystemRoot"),
        Environment.GetEnvironmentVariable("TEMP"),
        Environment.GetEnvironmentVariable("PATHEXT"));

    /// <summary>
    /// Runs one program with no window and no shell, both streams drained (capped, the rest read and dropped,
    /// so a chatty child never blocks on a full pipe), and the whole process tree killed at the timeout.
    /// <paramref name="environment"/> null inherits Kannu's environment; otherwise it replaces it.
    /// </summary>
    public static ProcessResult Run(string exe, IReadOnlyList<string> arguments, string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment, TimeSpan timeout, int stdoutCap, int stderrCap)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;
        if (environment is not null)
        {
            start.Environment.Clear();
            foreach (var (key, value) in environment) start.Environment[key] = value;
        }
        else
        {
            start.Environment["PYTHONIOENCODING"] = "utf-8";
            start.Environment["PYTHONUTF8"] = "1";
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("not started");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new ProcessResult(-1, "", "", false, e.Message);
        }
        using (process)
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
            var stdout = Task.Run(() => Drain(process.StandardOutput, stdoutCap));
            var stderr = Task.Run(() => Drain(process.StandardError, stderrCap));
            var timedOut = !process.WaitForExit((int)timeout.TotalMilliseconds);
            if (timedOut)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                }
                process.WaitForExit(5_000);
            }
            // A grandchild holding the pipe open must not park this worker forever.
            Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(5));
            var code = process.HasExited ? process.ExitCode : -1;
            return new ProcessResult(code, stdout.IsCompleted ? stdout.Result : "", stderr.IsCompleted ? stderr.Result : "", timedOut, null);
        }
    }

    private static string Drain(StreamReader reader, int cap)
    {
        var kept = new StringBuilder();
        var buffer = new char[16_384];
        try
        {
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                var room = cap - kept.Length;
                if (room > 0) kept.Append(buffer, 0, Math.Min(room, read));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
        return kept.ToString();
    }

    /// <summary>The last <paramref name="max"/> characters, trimmed: what an error line shows of stderr.</summary>
    public static string Tail(string text, int max = 400)
    {
        var tail = text.Length > max ? text[^max..] : text;
        return tail.Trim();
    }
}
