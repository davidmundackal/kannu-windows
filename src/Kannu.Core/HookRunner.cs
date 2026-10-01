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

using System.Text.Json;

namespace Kannu.Core;

/// <summary>
/// The body of <c>kannu-hook.exe</c>: one hook invocation, stdin JSON in, status file out.
/// Kept here rather than in the hook's Program so tests drive the real thing.
/// </summary>
public static class HookRunner
{
    /// <summary>Hook input above this is not a hook payload worth reading.</summary>
    public const int MaxInputChars = 1024 * 1024;

    /// <returns>The decision taken, for tests and diagnostics.</returns>
    public static HookDecision? Run(string provider, string? forcedState, string stdin, string statusDirectory, long nowMs)
    {
        if (stdin.Length > MaxInputChars) return null;

        HookDecision decision;
        try
        {
            using var doc = JsonDocument.Parse(stdin);
            decision = HookEventMapper.Map(provider, forcedState, doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
        if (decision.Action == HookAction.Ignore) return decision;

        Directory.CreateDirectory(statusDirectory);
        var path = Path.Combine(statusDirectory, StatusPaths.StatusFileName(provider, decision.SessionId));

        using var _ = StatusStore.TryLock(statusDirectory, TimeSpan.FromMilliseconds(1500));

        if (decision.Action == HookAction.Delete)
        {
            File.Delete(path);
            return decision;
        }

        var existing = StatusStore.Read(path);
        var (state, ts) = StateMerge.Apply(existing, decision.State, decision.HookEvent, nowMs);
        var cwd = decision.Cwd ?? existing?.Cwd;
        var record = new StatusRecord
        {
            State = state.ToWire(),
            Ts = ts,
            Provider = StatusPaths.SanitizeId(provider, "unknown"),
            HookEvent = decision.HookEvent,
            Name = existing?.Name,
            Cwd = cwd,
            Project = StatusPaths.ProjectName(cwd) ?? existing?.Project,
        };
        StatusStore.WriteAtomic(path, record);
        return decision with { State = state };
    }
}
