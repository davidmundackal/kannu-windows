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

// Usage: kannu-hook <provider> [--state <raw state>]
//
// Reads the agent's hook payload from stdin and updates the session's status file. It must never
// get in the agent's way: whatever happens it prints nothing and exits 0, because a hook that fails
// or writes to stdout can change what the agent does next.

using System.Text;
using Kannu.Core;

try
{
    var provider = args.Length > 0 ? args[0] : "claude";
    string? forcedState = null;
    for (var i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == "--state") forcedState = args[i + 1];
    }

    using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
    var buffer = new char[HookRunner.MaxInputChars + 1];
    var read = stdin.ReadBlock(buffer, 0, buffer.Length);

    var statusDirectory = Environment.GetEnvironmentVariable("KANNU_STATUS_DIR") is { Length: > 0 } overridden
        ? overridden
        : StatusPaths.DefaultStatusDirectory();

    HookRunner.Run(provider, forcedState, new string(buffer, 0, read), statusDirectory,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
catch
{
    // Deliberately swallowed: see the header.
}

return 0;
