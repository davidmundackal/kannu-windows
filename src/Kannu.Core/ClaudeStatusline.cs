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

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Kannu.Core;

/// <summary>
/// Claude Code's plan limits reach Kannu through its statusline: Claude Code runs the statusline
/// command with its <c>rate_limits</c> on stdin. Kannu's statusline is <c>kannu-hook statusline</c>,
/// which saves them to <see cref="ClaudeUsage.FileName"/>. A statusline the user already had is kept
/// and still drawn: Kannu runs it with the same input (macOS: <c>KANNU_USAGE_CHAIN_B64</c>).
/// </summary>
public sealed partial class AgentHookInstaller
{
    /// <summary>The user's own statusline command, run after Kannu's, one line of text.</summary>
    public string StatuslineChainPath => Path.Combine(Layout.Home, ".kannu", "claude-statusline-chain.txt");

    public string StatuslineCommand
    {
        get
        {
            var path = HookExePath.Replace('\\', '/');
            if (path.Contains(' ')) path = $"\"{path}\"";
            return path + " statusline";
        }
    }

    private static bool IsKannuStatusline(string? command) =>
        command is not null && command.Contains("statusline", StringComparison.Ordinal)
        && OwnMarkers.Any(m => command.Contains(m, StringComparison.Ordinal));

    public bool IsUsageStatuslineInstalled()
    {
        try
        {
            return File.Exists(HookExePath)
                   && IsKannuStatusline(Str(ReadForMerge(Layout.ClaudeSettings)["statusLine"]?["command"]));
        }
        catch (Exception e) when (e is HookInstallException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <exception cref="HookInstallException">settings.json could not be safely edited; nothing was changed.</exception>
    public void InstallUsageStatusline()
    {
        var settings = ReadForMerge(Layout.ClaudeSettings);
        var existing = Str(settings["statusLine"]?["command"]);
        if (existing is { Length: > 0 } && !IsKannuStatusline(existing)) WriteText(StatuslineChainPath, existing.Trim() + "\n");
        settings["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = StatuslineCommand, ["padding"] = 0 };
        WriteJson(Layout.ClaudeSettings, settings);
    }

    /// <summary>Puts the user's own statusline back, or removes the key when there was none.</summary>
    public void UninstallUsageStatusline()
    {
        var settings = ReadForMerge(Layout.ClaudeSettings);
        if (IsKannuStatusline(Str(settings["statusLine"]?["command"])))
        {
            var chain = ReadOrEmpty(StatuslineChainPath).Trim();
            if (chain.Length > 0) settings["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = chain, ["padding"] = 0 };
            else settings.Remove("statusLine");
            WriteJson(Layout.ClaudeSettings, settings);
        }
        DeleteIfExists(StatuslineChainPath);
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>What <c>kannu-hook statusline</c> does. Never fails Claude's statusline: errors print a plain line.</summary>
public static class StatuslineRunner
{
    public const int MaxInputChars = 256 * 1024;

    /// <returns>The text Claude Code shows as its statusline.</returns>
    public static string Run(string stdin, string statusDirectory, string chainPath, long nowMs, TimeZoneInfo zone)
    {
        var usage = ClaudeUsage.FromStatusline(stdin, nowMs, zone);
        if (usage is { } u)
        {
            try
            {
                Directory.CreateDirectory(statusDirectory);
                var path = Path.Combine(statusDirectory, ClaudeUsage.FileName);
                var temp = path + ".tmp";
                File.WriteAllText(temp, u.FileJson, new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        string chain;
        try
        {
            chain = File.Exists(chainPath) ? File.ReadAllText(chainPath).Trim() : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            chain = "";
        }
        if (chain.Length > 0 && RunChained(chain, stdin) is { } chained) return chained;
        return usage?.Line ?? "";
    }

    /// <summary>
    /// The user's statusline, in the shell Claude Code itself uses on Windows (Git Bash) when it can be
    /// found, else cmd. Given 5 seconds, as Claude Code would.
    /// </summary>
    private static string? RunChained(string command, string stdin)
    {
        try
        {
            var bash = Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH") is { Length: > 0 } configured && File.Exists(configured)
                ? configured
                : new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin", "bash.exe"),
                }.FirstOrDefault(File.Exists);
            var start = bash is not null
                ? new ProcessStartInfo(bash) { ArgumentList = { "-c", command } }
                : new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") { ArgumentList = { "/d", "/s", "/c", command } };
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.StandardOutputEncoding = new UTF8Encoding(false);
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
            if (!output.Wait(TimeSpan.FromSeconds(5)))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            return output.Result.TrimEnd('\r', '\n');
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or AggregateException)
        {
            return null;
        }
    }
}
