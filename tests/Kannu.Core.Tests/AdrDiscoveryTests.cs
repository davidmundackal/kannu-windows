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
using Kannu.Core;
using Xunit;

namespace Kannu.Core.Tests;

public sealed class AdrDiscoveryTests : IDisposable
{
    private const long H = 3600_000L;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kannu-adrd-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Arguments_print_a_snapshot_and_write_nothing()
    {
        Assert.Equal(["--json", "--dry-run"], AdrDiscovery.Arguments(null));
        Assert.Equal(["--json", "--dry-run", "--policy", @"C:\p.json"], AdrDiscovery.Arguments(@" C:\p.json "));
        Assert.True(AdrDiscovery.IsValidScan(AdrDiscovery.Arguments(null)));
        Assert.False(AdrDiscovery.IsValidScan(["--json"]));
        Assert.False(AdrDiscovery.IsValidScan(["--json", "--dry-run", "--root", "C:\\fixture"]));
        Assert.False(AdrDiscovery.IsValidScan(["--json", "--dry-run", "--explain"]));
        Assert.False(AdrDiscovery.IsValidScan(["--json", "--dry-run", "--output-dir", "x"]));
    }

    [Fact]
    public void Exit_codes_and_commands_match_docs()
    {
        Assert.True(AdrDiscovery.ProducedSnapshot(0));
        Assert.True(AdrDiscovery.ProducedSnapshot(2));
        Assert.False(AdrDiscovery.ProducedSnapshot(1));
        Assert.Equal(TimeSpan.FromSeconds(180), AdrDiscovery.Timeout);
        Assert.Equal("uv tool install \"adr-discovery @ git+https://github.com/uber/ADR#subdirectory=Discovery\"", AdrDiscovery.InstallCommand);
    }

    [Fact]
    public void Snapshot_file_uses_adrs_own_name_and_never_overwrites()
    {
        Assert.Equal("snapshot-2026-10-03T112444+0000.json", AdrDiscovery.SnapshotFileName("2026-10-03T11:24:44+00:00", DateTimeOffset.UnixEpoch));
        Assert.Equal("snapshot-1970-01-01T000000+0000.json", AdrDiscovery.SnapshotFileName("..\\evil", DateTimeOffset.UnixEpoch));
        var first = AdrDiscovery.WriteSnapshot(_dir, "snapshot-x.json", "{}");
        var second = AdrDiscovery.WriteSnapshot(_dir, "snapshot-x.json", "{\"a\":1}");
        Assert.NotEqual(first, second);
        Assert.Equal("{}", File.ReadAllText(first));
        Assert.EndsWith("snapshot-x-2.json", second);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Candidate_directories_follow_uv_and_pipx_on_windows()
    {
        var dirs = AdrDiscovery.CandidateDirectories(@" D:\tools ", @"C:\Users\dev", @"C:\Users\dev\AppData\Roaming", @"C:\Users\dev\AppData\Local",
            null, @"C:\Windows\System32;;C:\Users\dev\.local\bin");
        Assert.Equal(@"D:\tools", dirs[0]);
        Assert.Equal(Path.Combine(@"C:\Users\dev", ".local", "bin"), dirs[1]);
        Assert.Contains(Path.Combine(@"C:\Users\dev\AppData\Roaming", "uv", "tools", "adr-discovery", "Scripts"), dirs);
        Assert.Contains(@"C:\Windows\System32", dirs);
        Assert.Equal(dirs.Count, dirs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var found = AdrDiscovery.Locate("adr-discovery", dirs, p => p == Path.Combine(@"C:\Windows\System32", "adr-discovery.exe"));
        Assert.Equal(Path.Combine(@"C:\Windows\System32", "adr-discovery.exe"), found);
        Assert.Null(AdrDiscovery.Locate("adr-discovery", dirs, _ => false));
    }

    [Fact]
    public void Version_comes_from_uv_tool_list()
    {
        const string list = "adr-discovery v0.2.0\n- adr-discovery.exe\nruff v0.6.1\n- ruff.exe\n";
        Assert.Equal("0.2.0", AdrDiscovery.VersionFromUvToolList(list, "adr-discovery"));
        Assert.Null(AdrDiscovery.VersionFromUvToolList(list, "adr-sensor"));
    }

    private static Dictionary<string, IReadOnlyList<string>> Inv(params string[] servers) =>
        new() { ["a.json"] = servers.ToList() };

    [Fact]
    public void Trigger_scans_first_then_daily()
    {
        var t = new AdrScanTrigger(24 * H, 300_000);
        Assert.Equal(AdrScanTrigger.Scheduled, t.Evaluate(0, null, 0, null));
        Assert.Null(t.Evaluate(23 * H, 0, 0, null));
        Assert.Equal(AdrScanTrigger.Scheduled, t.Evaluate(24 * H, 0, 0, null));
        Assert.Null(t.NextScanMs(null, 0));
        Assert.Equal(24 * H, t.NextScanMs(0, 0));
    }

    [Fact]
    public void Trigger_waits_out_the_debounce_after_a_config_change()
    {
        var t = new AdrScanTrigger(24 * H, 300_000);
        Assert.Null(t.Evaluate(1_000, 0, 0, Inv("|a")));
        Assert.Null(t.Evaluate(60_000, 0, 0, Inv("|a", "|b")));
        Assert.True(t.PendingChange);
        Assert.Equal(300_000, t.NextScanMs(0, 0));
        Assert.Null(t.Evaluate(120_000, 0, 0, null));
        Assert.Equal(AdrScanTrigger.ConfigChanged, t.Evaluate(300_000, 0, 0, null));
        Assert.Null(t.Evaluate(360_000, 0, 0, Inv("|a", "|b")));
    }

    [Fact]
    public void Trigger_retries_after_failures_one_two_four_hours()
    {
        Assert.Equal(1 * H, AdrScanTrigger.RetryDelayMs(1, 24 * H));
        Assert.Equal(2 * H, AdrScanTrigger.RetryDelayMs(2, 24 * H));
        Assert.Equal(16 * H, AdrScanTrigger.RetryDelayMs(5, 24 * H));
        Assert.Equal(24 * H, AdrScanTrigger.RetryDelayMs(6, 24 * H));
        Assert.Equal(24 * H, AdrScanTrigger.RetryDelayMs(0, 24 * H));
        var t = new AdrScanTrigger(24 * H, 300_000);
        Assert.Null(t.Evaluate(H - 1, 0, 1, null));
        Assert.Equal(AdrScanTrigger.Retry, t.Evaluate(H, 0, 1, null));
    }

    [Fact]
    public void While_failing_a_config_change_waits_for_the_retry()
    {
        var t = new AdrScanTrigger(24 * H, 300_000);
        t.Evaluate(1_000, 0, 2, Inv("|a"));
        Assert.Null(t.Evaluate(400_000, 0, 2, Inv("|a", "|b")));
        Assert.Equal(2 * H, t.NextScanMs(0, 2));
        t.ScanStarted();
        Assert.False(t.PendingChange);
    }
}
