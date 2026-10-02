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
using Kannu.Core;

namespace Kannu.App;

/// <summary>The settings file plus a change event, so the notch follows Settings live. UI thread only.</summary>
internal sealed class SettingsStore(string path)
{
    public AppSettings Current { get; private set; } = AppSettings.Load(path);

    public event Action<AppSettings>? Changed;

    public void Update(Func<AppSettings, AppSettings> change)
    {
        var next = change(Current);
        if (next == Current) return;
        Current = next;
        try
        {
            next.Save(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Kept for this session; the next change tries the file again.
        }
        Changed?.Invoke(next);
    }
}
