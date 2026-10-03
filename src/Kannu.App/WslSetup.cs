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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// Kannu's hook for agents inside WSL: written into each distro's agent settings, pointing at the
/// Windows hook by its Linux path, so their sessions reach the same notch. Every call can start the
/// WSL VM and take seconds: callers run it off the UI thread.
/// </summary>
internal static class WslSetup
{
    private static string WslExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

    public static bool IsAvailable => File.Exists(WslExe);

    /// <summary>The installed distros; empty when WSL is not installed or has none.</summary>
    public static IReadOnlyList<string> Distros()
    {
        if (!IsAvailable) return [];
        return Run(Encoding.Unicode, "--list", "--quiet") is { } output ? WslAgents.ParseDistros(output) : [];
    }

    /// <summary>One distro's state: which CLI agents are set up there, and which carry Kannu's hook.</summary>
    public sealed record DistroState(string Distro, IReadOnlyList<AgentProvider> Present, IReadOnlyList<AgentProvider> Installed, string? Problem);

    public static DistroState Inspect(string distro)
    {
        if (Installer(distro, out var problem) is not { } installer) return new DistroState(distro, [], [], problem);
        var present = WslAgents.CliProviders.Where(p => WslAgents.IsPresent(installer.Layout, p)).ToList();
        var installed = present.Where(p => SafeIsInstalled(installer, p)).ToList();
        return new DistroState(distro, present, installed, present.Count == 0 ? "No CLI agents set up in this distro yet." : null);
    }

    /// <returns>A message for the user.</returns>
    public static string Install(string distro)
    {
        HookSetup.EnsureHookExe();
        if (Installer(distro, out var problem) is not { } installer) return problem ?? "WSL did not answer.";
        var done = new List<string>();
        var failed = new List<string>();
        foreach (var provider in WslAgents.CliProviders.Where(p => WslAgents.IsPresent(installer.Layout, p)))
        {
            try
            {
                installer.Install(provider);
                done.Add(provider.DisplayName());
            }
            catch (Exception e) when (e is HookInstallException or IOException or UnauthorizedAccessException)
            {
                failed.Add($"{provider.DisplayName()}: {e.Message}");
            }
        }
        Diagnostics.Info($"WSL hooks installed in a distro for {done.Count} agents");
        if (done.Count == 0 && failed.Count == 0) return $"No CLI agents are set up in {distro} yet. Run one there once, then try again.";
        return (done.Count > 0 ? $"Installed in {distro} for {string.Join(", ", done)}. New sessions there will show on the notch." : "")
               + (failed.Count > 0 ? " Not installed: " + string.Join("; ", failed) : "");
    }

    public static string Uninstall(string distro)
    {
        if (Installer(distro, out var problem) is not { } installer) return problem ?? "WSL did not answer.";
        foreach (var provider in WslAgents.CliProviders)
        {
            try
            {
                installer.Uninstall(provider);
            }
            catch (Exception e) when (e is HookInstallException or IOException or UnauthorizedAccessException)
            {
                Diagnostics.Info($"WSL hook removal skipped one agent: {e.GetType().Name}");
            }
        }
        return $"Kannu's hooks removed from {distro}.";
    }

    private static AgentHookInstaller? Installer(string distro, out string? problem)
    {
        problem = null;
        var linuxHome = Run(Encoding.UTF8, "-d", distro, "-e", "sh", "-c", "printf %s \"$HOME\"");
        var home = linuxHome is null ? null
            : new[] { @"\\wsl.localhost", @"\\wsl$" }.Select(share => WslAgents.UncHome(distro, linuxHome, share)).FirstOrDefault(p => p is not null && Directory.Exists(p));
        if (home is null)
        {
            problem = $"Could not reach {distro}'s home folder. Start the distro once, then try again.";
            return null;
        }
        var hook = HookSetup.InstalledHookPath;
        var linuxHook = Run(Encoding.UTF8, "-d", distro, "-e", "wslpath", "-u", hook)?.Trim() is { Length: > 0 } converted && converted.StartsWith('/')
            ? converted
            : WslAgents.ToMountPath(hook);
        if (linuxHook is null)
        {
            problem = "Kannu's hook is not on a drive WSL can reach.";
            return null;
        }
        return new AgentHookInstaller(new AgentHookLayout(home), hook) { CommandExePath = linuxHook };
    }

    private static bool SafeIsInstalled(AgentHookInstaller installer, AgentProvider provider)
    {
        try
        {
            return installer.IsInstalled(provider);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HookInstallException)
        {
            return false;
        }
    }

    /// <summary>Runs wsl.exe with a 30 s limit (the VM may have to start). Null on any failure.</summary>
    private static string? Run(Encoding encoding, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo(WslExe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = encoding,
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
