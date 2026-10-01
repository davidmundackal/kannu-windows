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
using System.Text;
using System.Threading;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// Installs and removes Kannu's Claude Code hooks. The settings transform is
/// <see cref="ClaudeSettingsHooks"/> (tested in Core); this class owns only the file system side:
/// copying the hook executable to a stable path, backing up settings.json, and writing it atomically.
/// </summary>
internal static class HookSetup
{
    private const string HookExeName = "kannu-hook.exe";

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    /// <summary>A stable path, so moving or updating Kannu.exe does not break the installed hooks.</summary>
    private static string InstalledHookPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kannu", "bin", HookExeName);

    private static string BundledHookPath => Path.Combine(AppContext.BaseDirectory, HookExeName);

    public static bool IsInstalled()
    {
        try
        {
            return File.Exists(SettingsPath) && ClaudeSettingsHooks.IsInstalled(File.ReadAllText(SettingsPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <returns>A message for the user.</returns>
    /// <exception cref="InvalidDataException">settings.json is not valid JSON; it was left untouched.</exception>
    public static string Install()
    {
        if (!File.Exists(BundledHookPath))
        {
            throw new FileNotFoundException(
                $"{HookExeName} was not found next to Kannu.exe. Build Kannu with scripts/publish.ps1, which puts both in one folder.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(InstalledHookPath)!);
        CopyWithRetry(BundledHookPath, InstalledHookPath);

        var current = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null;
        var updated = ClaudeSettingsHooks.Install(current, InstalledHookPath);
        Backup(current);
        WriteAtomic(SettingsPath, updated);
        return "Claude Code hooks installed. New Claude Code sessions will show on the notch.";
    }

    /// <returns>A message for the user.</returns>
    /// <exception cref="InvalidDataException">settings.json is not valid JSON; it was left untouched.</exception>
    public static string Remove()
    {
        if (!File.Exists(SettingsPath)) return "Claude Code has no settings.json; nothing to remove.";

        var current = File.ReadAllText(SettingsPath);
        if (!ClaudeSettingsHooks.IsInstalled(current)) return "Kannu's hooks are not installed.";

        var updated = ClaudeSettingsHooks.Remove(current);
        Backup(current);
        WriteAtomic(SettingsPath, updated);
        try { File.Delete(InstalledHookPath); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return "Claude Code hooks removed.";
    }

    /// <summary>Every change keeps the previous file, timestamped, next to it.</summary>
    private static void Backup(string? current)
    {
        if (current is null) return;
        var backup = $"{SettingsPath}.kannu-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.WriteAllText(backup, current, new UTF8Encoding(false));
    }

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".kannu-tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
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
