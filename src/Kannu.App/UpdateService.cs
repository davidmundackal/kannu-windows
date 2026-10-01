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
using System.Threading.Tasks;
using System.Windows.Threading;
using Kannu.Core;
using Velopack;
using Velopack.Sources;

namespace Kannu.App;

/// <summary>
/// Keeps Kannu current from its GitHub Releases, the Windows counterpart of macOS Kannu's Sparkle
/// updater: a check at launch and once a day, the download in the background, and the update applied
/// when Kannu next starts (or now, from the tray). Off in Debug builds and in any build Velopack did
/// not install, such as one run straight from <c>scripts/publish.ps1</c>'s output.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly UpdateManager? _manager;
    private readonly DispatcherTimer _timer;
    private VelopackAsset? _ready;
    private bool _checking;

    /// <summary>An update finished downloading; the argument is its version.</summary>
    public event Action<string>? UpdateReady;

    public UpdateService()
    {
        _manager = CreateManager();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CheckInterval };
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    private static UpdateManager? CreateManager()
    {
#if DEBUG
        return null;
#else
        // A test build ("0.1.0-test.1") follows test releases; a stable build sees stable ones only.
        var manager = new UpdateManager(new GithubSource(ReleaseInfo.RepositoryUrl, accessToken: null, prerelease: ReleaseInfo.Version.Contains('-')));
        return manager.IsInstalled ? manager : null;
#endif
    }

    public bool IsEnabled => _manager is not null;

    public string? ReadyVersion => _ready?.Version.ToString();

    public void Start()
    {
        if (_manager is null) return;
        _timer.Start();
        _ = CheckAsync();
    }

    /// <returns>A message for the user.</returns>
    public async Task<string> CheckAsync()
    {
        if (_manager is null) return "Updates are off in this build.";
        if (_ready is not null) return $"Kannu {ReadyVersion} is ready. Restart to update.";
        if (_checking) return "Already checking for updates.";
        _checking = true;
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null) return $"Kannu {ReleaseInfo.Version} is the latest version.";
            await _manager.DownloadUpdatesAsync(update);
            _ready = update.TargetFullRelease;
            UpdateReady?.Invoke(ReadyVersion!);
            return $"Kannu {ReadyVersion} is ready. Restart to update.";
        }
        // Velopack reports offline, rate-limited and malformed-feed failures with assorted exception
        // types; none of them may take the notch down, and the next daily check tries again.
        catch (Exception e)
        {
            return $"Could not check for updates: {e.Message}";
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Quits, applies the downloaded update and starts the new version.</summary>
    public void RestartToUpdate()
    {
        if (_manager is not null && _ready is not null) _manager.ApplyUpdatesAndRestart(_ready);
    }

    public void Dispose() => _timer.Stop();
}
