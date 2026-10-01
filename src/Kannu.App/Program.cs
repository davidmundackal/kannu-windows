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
using Velopack;

namespace Kannu.App;

/// <summary>
/// The entry point. Velopack runs first: when the installer, an update or the uninstaller launches
/// Kannu with one of its lifecycle arguments, the matching hook below runs and the process exits
/// without ever showing a window. Otherwise it applies an update downloaded last session, then the
/// app starts as usual.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => HookSetup.AfterInstallOrUpdate())
            .OnAfterUpdateFastCallback(_ => HookSetup.AfterInstallOrUpdate())
            .OnBeforeUninstallFastCallback(_ => HookSetup.BeforeUninstall())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
