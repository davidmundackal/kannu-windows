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

using System.Text;

namespace Kannu.Core;

/// <summary>One status file as the notch shows it.</summary>
public sealed record AgentSession(string Key, StatusRecord Record, TrafficLight Light, bool Visible, long AgeMs)
{
    /// <summary>What the row is called: the project folder, else the provider.</summary>
    public string Title =>
        !string.IsNullOrWhiteSpace(Record.Name) ? Record.Name!
        : !string.IsNullOrWhiteSpace(Record.Project) ? Record.Project!
        : StatusPaths.ProjectName(Record.Cwd) ?? Record.Provider;
}

/// <summary>Reads and writes status files. Shared by the hook (writer) and the app (reader).</summary>
public static class StatusStore
{
    /// <summary>
    /// Reads one status file. Opens with ReadWrite|Delete sharing so a reader never blocks a hook's
    /// atomic replace on Windows. Returns null for a missing, oversized, locked or malformed file.
    /// </summary>
    public static StatusRecord? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > StatusRecord.MaxFileBytes) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return StatusRecord.TryParse(reader.ReadToEnd());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes through a temp file in the same directory and renames it over the target, so a reader
    /// sees the old file or the new one, never half of one. Retries briefly: on Windows a rename over
    /// a file another process has open without delete sharing fails with a sharing violation.
    /// </summary>
    public static void WriteAtomic(string path, StatusRecord record)
    {
        var dir = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(dir, $".kannu-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, record.ToJson(), new UTF8Encoding(false));
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(15);
                }
            }
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Takes the directory-wide lock that serialises every hook's read-modify-write. Returns null if
    /// it cannot be had within <paramref name="timeout"/>: a possible lost update beats a hung hook,
    /// which would stall the agent itself. The lock file is never deleted — unlinking a lock another
    /// hook has opened but not yet locked hands that hook a lock nobody else respects.
    /// </summary>
    public static IDisposable? TryLock(string statusDirectory, TimeSpan timeout)
    {
        var path = Path.Combine(statusDirectory, StatusPaths.LockFileName);
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Every readable status file in <paramref name="statusDirectory"/>, keyed by file name stem.</summary>
    public static IReadOnlyList<(string Key, StatusRecord Record)> ReadAll(string statusDirectory)
    {
        var result = new List<(string, StatusRecord)>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(statusDirectory, "*.json");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        try
        {
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith('.')) continue;
                if (Read(file) is { } record) result.Add((Path.GetFileNameWithoutExtension(name), record));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The directory changed under the enumeration; the watcher will ask again.
        }
        return result;
    }

    /// <summary>Resolves records to sessions, most urgent first, then most recent.</summary>
    public static IReadOnlyList<AgentSession> Resolve(IEnumerable<(string Key, StatusRecord Record)> records, long nowMs) =>
        records
            .Select(r =>
            {
                var age = nowMs - r.Record.Ts;
                var light = StatusResolver.Resolve(RawStateWire.Parse(r.Record.State), age);
                return new AgentSession(r.Key, r.Record, light.Light, light.Visible, Math.Max(0, age));
            })
            .Where(s => s.Visible)
            .OrderByDescending(s => s.Light.Urgency())
            .ThenBy(s => s.AgeMs)
            .ToList();
}
