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
using System.Threading;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// Installs and removes Kannu's hooks for each agent. The settings edits are
/// <see cref="AgentHookInstaller"/> (tested in Core); this class only puts <c>kannu-hook.exe</c> at a
/// stable path first, so moving or updating Kannu.exe never breaks an installed hook.
/// </summary>
internal static class HookSetup
{
    private const string HookExeName = "kannu-hook.exe";

    private static readonly AgentHookInstaller Installer = new(
        new AgentHookLayout(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kannu", "bin", HookExeName));

    private static string BundledHookPath => Path.Combine(AppContext.BaseDirectory, HookExeName);

    public static bool IsInstalled(AgentProvider provider)
    {
        try
        {
            return Installer.IsInstalled(provider);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool ToolIsPresent(AgentProvider provider) => Installer.Layout.ToolIsPresent(provider);

    /// <returns>A message for the user.</returns>
    /// <exception cref="HookInstallException">A settings file could not be safely edited; nothing was changed.</exception>
    public static string Install(AgentProvider provider)
    {
        if (!File.Exists(BundledHookPath))
        {
            throw new FileNotFoundException(
                $"{HookExeName} was not found next to Kannu.exe. Build Kannu with scripts/publish.ps1, which puts both in one folder.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Installer.HookExePath)!);
        CopyWithRetry(BundledHookPath, Installer.HookExePath);
        Installer.Install(provider);
        Diagnostics.Info($"Installed the {provider.Id()} hook");
        return $"{provider.DisplayName()} hooks installed. New sessions will show on the notch.";
    }

    /// <returns>A message for the user.</returns>
    public static string Uninstall(AgentProvider provider)
    {
        Installer.Uninstall(provider);
        Diagnostics.Info($"Removed the {provider.Id()} hook");
        return $"{provider.DisplayName()} hooks removed.";
    }

    /// <summary>
    /// Velopack runs this in the new version right after an install or update. Agents set up by the
    /// previous version are rewritten and the hook copied again, so they run this version's hook.
    /// </summary>
    public static void AfterInstallOrUpdate()
    {
        // Never fail an install or update over hooks: the tray can set them up again.
        try
        {
            var rewritten = Installer.Reinstall();
            if ((rewritten.Count > 0 || File.Exists(Installer.HookExePath)) && File.Exists(BundledHookPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Installer.HookExePath)!);
                CopyWithRetry(BundledHookPath, Installer.HookExePath);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Velopack runs this before uninstalling: every agent's settings are left without Kannu.</summary>
    public static void BeforeUninstall()
    {
        Installer.UninstallAll();
        try
        {
            Directory.Delete(Path.GetDirectoryName(Installer.HookExePath)!, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A hook running at this moment holds its executable open for a few milliseconds.</summary>
    private static void CopyWithRetry(string from, string to)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
        }
    }
}
