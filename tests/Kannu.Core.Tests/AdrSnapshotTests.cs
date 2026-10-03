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

public sealed class AdrSnapshotTests : IDisposable
{
    /// <summary>
    /// A real snapshot: upstream adr-discovery (github.com/uber/ADR, commit d967665) run with
    /// <c>--root &lt;fixture&gt; --json --dry-run</c> over a fixture whose <c>~/.cursor/mcp.json</c> declares an
    /// unpinned <c>npx</c> server and a plain-HTTP one. Hostname and username replaced. Exit code was 2 (partial).
    /// </summary>
    internal const string Sample = """
{
  "assets": [
    {
      "asset_id": "4320cfeade473fb05d3ec3522628bb33",
      "catalog_id": null,
      "confidence": {
        "channels": [
          "config"
        ],
        "label": "low"
      },
      "evidence": [
        {
          "channel": "config",
          "confidence": 0.85,
          "path": "/home/dev/.cursor/mcp.json",
          "proof": "declared in plugin scope",
          "rung": null,
          "stage": "extractor"
        },
        {
          "channel": "config",
          "confidence": 0.85,
          "path": "/home/dev/.cursor/mcp.json",
          "proof": "declared in plugin scope",
          "rung": null,
          "stage": "extractor"
        }
      ],
      "flags": [
        "config_scope=plugin",
        "merged_2"
      ],
      "identity": "mcp_server:github",
      "install_method": null,
      "install_path": "/home/dev/.cursor/mcp.json",
      "install_root": "/home/dev/.cursor",
      "kind": "mcp_server",
      "last_used": null,
      "liveness": "declared_only",
      "location": "local",
      "name": "github",
      "owner": "system",
      "risk": {
        "credential_kinds": [
          "inherited"
        ],
        "destinations": [],
        "env_names": [
          "<inherits parent environment>"
        ],
        "factors": [
          "unpinned_supply_chain"
        ],
        "pinned": false,
        "transport": "stdio",
        "unattended": false
      },
      "sanction": "unknown",
      "vendor": null,
      "verification": [],
      "version": null
    },
    {
      "asset_id": "166b903d48e9d76c18a6f4f2c5167105",
      "catalog_id": null,
      "confidence": {
        "channels": [
          "config"
        ],
        "label": "low"
      },
      "evidence": [
        {
          "channel": "config",
          "confidence": 0.85,
          "path": "/home/dev/.cursor/mcp.json",
          "proof": "declared in plugin scope",
          "rung": null,
          "stage": "extractor"
        },
        {
          "channel": "config",
          "confidence": 0.85,
          "path": "/home/dev/.cursor/mcp.json",
          "proof": "declared in plugin scope",
          "rung": null,
          "stage": "extractor"
        }
      ],
      "flags": [
        "config_scope=plugin",
        "merged_2"
      ],
      "identity": "mcp_server:remote",
      "install_method": null,
      "install_path": "/home/dev/.cursor/mcp.json",
      "install_root": "/home/dev/.cursor",
      "kind": "mcp_server",
      "last_used": null,
      "liveness": "declared_only",
      "location": "local",
      "name": "remote",
      "owner": "system",
      "risk": {
        "credential_kinds": [
          "inherited"
        ],
        "destinations": [
          "mcp.example.com"
        ],
        "env_names": [
          "<inherits parent environment>"
        ],
        "factors": [
          "plaintext_transport"
        ],
        "pinned": null,
        "transport": "http",
        "unattended": false
      },
      "sanction": "unknown",
      "vendor": null,
      "verification": [],
      "version": null
    }
  ],
  "catalog_version": "2026.08.22",
  "coverage": {
    "boundaries_hit": [],
    "denied": [],
    "out_of_scope": [
      "instruction_files",
      "agent_hooks",
      "scheduling_mechanisms"
    ],
    "probes": [
      {
        "detail": "unavailable",
        "name": "exec_journal",
        "status": "degraded"
      },
      {
        "detail": "4 candidates; 4 entries before the sweep",
        "name": "enumerator",
        "status": "ran"
      },
      {
        "detail": "2 bridging merge(s) refused",
        "name": "resolver",
        "status": "ran"
      }
    ],
    "roots_swept": [
      {
        "depth_reached": 1,
        "entries": 2,
        "path": "/home/dev"
      },
      {
        "depth_reached": 0,
        "entries": 0,
        "path": "/home/dev/Projects"
      },
      {
        "depth_reached": 0,
        "entries": 0,
        "path": "/home/dev/src"
      },
      {
        "depth_reached": 0,
        "entries": 0,
        "path": "/home/dev/code"
      },
      {
        "depth_reached": 0,
        "entries": 0,
        "path": "/home/dev/work"
      },
      {
        "depth_reached": 0,
        "entries": 0,
        "path": "/home/dev/dev"
      },
      {
        "depth_reached": 0,
        "entries": 0,
        "path": "/home/dev/git"
      },
      {
        "depth_reached": 0,
        "entries": 0,
        "path": "/home/dev/repos"
      },
      {
        "depth_reached": 0,
        "entries": 0,
        "path": "/opt"
      },
      {
        "depth_reached": 0,
        "entries": 0,
        "path": "/srv"
      },
      {
        "depth_reached": 0,
        "entries": 0,
        "path": "/usr/local"
      },
      {
        "depth_reached": 0,
        "entries": 0,
        "path": "/workspace"
      },
      {
        "depth_reached": 0,
        "entries": 0,
        "path": "/Users"
      },
      {
        "depth_reached": 2,
        "entries": 3,
        "path": "/home"
      }
    ],
    "truncated": [],
    "unavailable": [
      {
        "provider": "packages",
        "reason": "no provider for this platform"
      },
      {
        "provider": "applications",
        "reason": "no provider for this platform"
      },
      {
        "provider": "processes",
        "reason": "no provider for this platform"
      },
      {
        "provider": "sockets",
        "reason": "no provider for this platform"
      },
      {
        "provider": "dns_cache",
        "reason": "no provider for this platform"
      },
      {
        "provider": "exec_journal",
        "reason": "no provider for this platform"
      }
    ]
  },
  "findings": [
    {
      "asset_id": "4320cfeade473fb05d3ec3522628bb33",
      "evidence": [
        {
          "channel": "config",
          "confidence": 0.9,
          "path": "/home/dev/.cursor/mcp.json",
          "proof": "no version in the resolved operand",
          "rung": null,
          "stage": "judge"
        }
      ],
      "rule": "unpinned_mcp_server",
      "severity": "medium",
      "summary": "github resolves its package at launch time"
    },
    {
      "asset_id": "166b903d48e9d76c18a6f4f2c5167105",
      "evidence": [
        {
          "channel": "config",
          "confidence": 0.9,
          "path": "/home/dev/.cursor/mcp.json",
          "proof": "declared url uses a plaintext scheme",
          "rung": null,
          "stage": "judge"
        }
      ],
      "rule": "plaintext_transport",
      "severity": "medium",
      "summary": "remote is reached over http://"
    }
  ],
  "hostname": "test-host",
  "platform": "linux",
  "review_queue": [],
  "schema_version": "1.0",
  "timestamp": "2026-10-03T11:24:44+00:00",
  "username": "dev"
}
""";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kannu-adr-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Decodes_upstreams_own_output()
    {
        var snapshot = AdrSnapshot.Decode(Sample);
        Assert.Equal("1.0", snapshot.SchemaVersion);
        Assert.Equal("test-host", snapshot.Hostname);
        Assert.Equal(2, snapshot.Assets.Count);
        Assert.Equal(["unpinned_mcp_server", "plaintext_transport"], snapshot.Findings.Select(f => f.Rule));
        Assert.Equal("/home/dev/.cursor/mcp.json", snapshot.Findings[0].Evidence[0].Path);
        Assert.Null(snapshot.Findings[0].Evidence[0].Rung);
        Assert.False(snapshot.CoverageInfo.IsComplete);
        Assert.Equal(6, snapshot.CoverageInfo.GapCount);
        Assert.Equal(DateTimeOffset.Parse("2026-10-03T11:24:44+00:00").ToUnixTimeMilliseconds(), snapshot.GeneratedAtMs);
    }

    [Fact]
    public void Maps_findings_with_macOS_ids_titles_and_groups()
    {
        var findings = AdrSnapshot.Decode(Sample).ToFindings([], 1_000);
        var unpinned = findings[0];
        // sha256("discovery\x1Funpinned_mcp_server\x1F<asset>\x1F<evidence>")[..12], computed independently with Python.
        Assert.Equal("254006053d1e8cc8ccb86f5a", unpinned.Id);
        Assert.Equal("bdcdd6219b2a49b1047667a3", unpinned.GroupId);
        Assert.Equal("discovery", unpinned.Source);
        Assert.Equal("Unpinned MCP server", unpinned.Title);
        Assert.Equal(FindingSeverity.Medium, unpinned.Severity);
        Assert.Equal("github resolves its package at launch time", unpinned.Summary);
        Assert.Equal(["no version in the resolved operand — /home/dev/.cursor/mcp.json"], unpinned.Evidence);
        Assert.Equal("github", unpinned.AssetName);
        Assert.Equal("/home/dev/.cursor/mcp.json", unpinned.RevealPath);
        Assert.Equal(1_000, unpinned.FirstSeenMs);
        Assert.Equal(DateTimeOffset.Parse("2026-10-03T11:24:44+00:00").ToUnixTimeMilliseconds(), unpinned.LastSeenMs);
        Assert.Equal("1d4c0b8320ca325f9b025fb3", findings[1].Id);
        Assert.Equal("MCP server over plain HTTP", findings[1].Title);
        Assert.Contains("reported by ADR Discovery", unpinned.PushBody);
        Assert.DoesNotContain("github", unpinned.PushBody);
    }

    [Fact]
    public void A_rescan_keeps_first_seen()
    {
        var snapshot = AdrSnapshot.Decode(Sample);
        var first = snapshot.ToFindings([], 1_000);
        var again = snapshot.ToFindings(first, 9_000);
        Assert.Equal(first.Select(f => (f.Id, f.FirstSeenMs, f.LastSeenMs)), again.Select(f => (f.Id, f.FirstSeenMs, f.LastSeenMs)));
    }

    [Fact]
    public void Severity_and_titles_follow_macOS()
    {
        Assert.Equal(FindingSeverity.High, AdrSnapshot.Severity("CRITICAL"));
        Assert.Equal(FindingSeverity.Info, AdrSnapshot.Severity("low"));
        Assert.Equal(FindingSeverity.Medium, AdrSnapshot.Severity("whatever"));
        Assert.Equal("Running MCP server nobody declared", AdrSnapshot.Title("undeclared_mcp_server"));
        Assert.Equal("MCP server reaches outside your domains", AdrSnapshot.Title("third_party_destination"));
        Assert.Equal("Shadow ai browser", AdrSnapshot.Title("shadow_ai_browser"));
        Assert.Equal("Shadow", AdrSnapshot.Title("SHADOW"));
    }

    [Fact]
    public void Reveal_paths_are_absolute_and_never_climb()
    {
        Assert.Equal(@"C:\Users\dev\.cursor\mcp.json", AdrSnapshot.RevealablePath(@"C:\Users\dev\.cursor\mcp.json"));
        Assert.Null(AdrSnapshot.RevealablePath(@"C:\Users\dev\..\x"));
        Assert.Null(AdrSnapshot.RevealablePath("relative/file"));
        Assert.Null(AdrSnapshot.RevealablePath(""));
    }

    [Fact]
    public void Rejects_a_newer_schema_with_a_message()
    {
        var e = Assert.Throws<AdrSnapshotException>(() => AdrSnapshot.Decode("""{"schema_version":"2.0","assets":[]}"""));
        Assert.Contains("2.0", e.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"other":1}""")]
    [InlineData("""{"assets":[{"asset_id":"a"}]}""")]
    [InlineData("""{"findings":[{"rule":"x","severity":"high","asset_id":"a","summary":"s"}]}""")]
    [InlineData("""{"schema_version":1}""")]
    public void Rejects_what_is_not_a_snapshot(string json)
    {
        var e = Assert.Throws<AdrSnapshotException>(() => AdrSnapshot.Decode(json));
        Assert.Equal("The file is not an ADR Discovery snapshot.", e.Message);
    }

    [Fact]
    public void Missing_lists_decode_empty_and_unknown_keys_are_ignored()
    {
        var snapshot = AdrSnapshot.Decode("""{"schema_version":"1.3","findings":[],"future":{"x":1}}""");
        Assert.Empty(snapshot.Assets);
        Assert.True(snapshot.CoverageInfo.IsComplete);
        Assert.Equal("unknown", snapshot.CatalogVersion);
        Assert.Null(snapshot.GeneratedAtMs);
    }

    [Fact]
    public void Newest_snapshot_is_picked_by_modification_time()
    {
        Directory.CreateDirectory(_dir);
        var older = Path.Combine(_dir, "snapshot-b.json");
        var newer = Path.Combine(_dir, "snapshot-a.json");
        File.WriteAllText(older, "{}");
        File.WriteAllText(newer, "{}");
        File.WriteAllText(Path.Combine(_dir, "other.json"), "{}");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));
        Assert.Equal(newer, AdrSnapshot.NewestSnapshotPath(_dir));
        Assert.Null(AdrSnapshot.NewestSnapshotPath(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void Scan_record_says_partial()
    {
        var record = AdrScanRecord.From(AdrSnapshot.Decode(Sample), AdrScanRecord.OriginKannu, "snapshot-x.json", 5);
        Assert.False(record.CoverageComplete);
        Assert.Equal(6, record.CoverageGaps);
        Assert.Equal(2, record.FindingCount);
    }

    [Fact]
    public void State_round_trips_adr_fields()
    {
        var path = Path.Combine(_dir, "security.json");
        var state = new SecurityState
        {
            AdrLastKannuScanMs = 42,
            AdrScanFailures = 3,
            AdrLastScan = AdrScanRecord.From(AdrSnapshot.Decode(Sample), AdrScanRecord.OriginWatched, "snapshot-x.json", 7),
        };
        state.AdrAnalyses.Add(new AdrAnalysis { ConversationId = "conv-1", IsMalicious = true, Confidence = 0.91, Tactic = "permission_abuse", DateMs = 9 });
        state.Save(path, 0);
        var loaded = SecurityState.Load(path);
        Assert.Equal(42, loaded.AdrLastKannuScanMs);
        Assert.Equal(3, loaded.AdrScanFailures);
        Assert.Equal(state.AdrLastScan, loaded.AdrLastScan);
        Assert.Equal(state.AdrAnalyses, loaded.AdrAnalyses);
    }
}
