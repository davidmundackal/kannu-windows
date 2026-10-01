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

namespace Kannu.Core;

/// <summary>
/// The state a hook writes into a status file. The wire strings are the same ones Kannu for macOS
/// writes, so a status file means the same thing on both platforms (see docs/STATUS_CONTRACT.md).
/// </summary>
public enum RawState
{
    Idle,
    Thinking,
    Executing,
    AwaitingInput,
    Stopped,
    QuotaExceeded,
}

public static class RawStateWire
{
    public static string ToWire(this RawState state) => state switch
    {
        RawState.Idle => "idle",
        RawState.Thinking => "thinking",
        RawState.Executing => "executing",
        RawState.AwaitingInput => "awaiting_input",
        RawState.Stopped => "stopped",
        RawState.QuotaExceeded => "quota_exceeded",
        _ => "idle",
    };

    /// <summary>Parses a wire string, accepting the legacy spellings macOS Kannu also reads.</summary>
    public static RawState? Parse(string? wire) => wire?.Trim().ToLowerInvariant() switch
    {
        "idle" => RawState.Idle,
        "thinking" => RawState.Thinking,
        "executing" => RawState.Executing,
        "awaiting_input" or "awaitinginput" or "awaiting" => RawState.AwaitingInput,
        "stopped" or "stop" or "completed" or "aborted" or "error" => RawState.Stopped,
        "quota_exceeded" => RawState.QuotaExceeded,
        _ => null,
    };

    /// <summary>
    /// How urgent a state is when two writes race. A higher value is never overwritten by a lower one
    /// inside the merge window (<see cref="StateMerge"/>).
    /// </summary>
    public static int Priority(this RawState state) => state switch
    {
        RawState.QuotaExceeded => 50,
        RawState.AwaitingInput => 40,
        RawState.Stopped => 30,
        RawState.Executing => 20,
        RawState.Thinking => 10,
        _ => 0,
    };
}
