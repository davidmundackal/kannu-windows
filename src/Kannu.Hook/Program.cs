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

// Usage: kannu-hook <state> <provider> [hook_event] [matcher_key]   (hook JSON on stdin)
//
// The same arguments as the macOS hook script, so the installers' tables carry over. It must never
// get in the agent's way: it always exits 0 and always prints the one line the agent expects
// (HookOutput), even when everything else failed.

using System.Runtime.InteropServices;
using System.Text;
using Kannu.Core;

var invocation = HookInvocation.FromArgs(args);
var copilotCli = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COPILOT_CLI"));
string output;

try
{
    using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
    var buffer = new char[HookRunner.MaxInputChars + 1];
    var read = stdin.ReadBlock(buffer, 0, buffer.Length);

    var statusDirectory = Environment.GetEnvironmentVariable("KANNU_STATUS_DIR") is { Length: > 0 } overridden
        ? overridden
        : StatusPaths.DefaultStatusDirectory();
    var environment = new HookEnvironment(
        copilotCli,
        HasConsoleWindow(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    output = HookRunner.Run(invocation, new string(buffer, 0, read), statusDirectory,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), environment).Output;
}
catch
{
    output = HookOutput.Fallback(invocation.Provider, copilotCli);
}

if (output.Length > 0) Console.Out.WriteLine(output);
return 0;

// A backstop to COPILOT_CLI: a terminal agent runs its hooks in a console whose window is on screen;
// an editor's extension host spawns them with no console window or a hidden one.
static bool HasConsoleWindow()
{
    if (!OperatingSystem.IsWindows()) return false;
    var window = Native.GetConsoleWindow();
    return window != IntPtr.Zero && Native.IsWindowVisible(window);
}

internal static partial class Native
{
    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetConsoleWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr hWnd);
}
