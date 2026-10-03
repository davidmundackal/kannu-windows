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

using System.Globalization;
using System.Text.Json;

namespace Kannu.Core;

/// <summary>
/// A snapshot written by ADR Discovery (<c>adr-discovery --json</c>, github.com/uber/ADR). Port of macOS
/// <c>ADRSnapshot</c>: schema 1.x, snake_case keys, strict on the schema major and on the fields upstream
/// always writes, lenient on unknown keys. The <c>coverage</c> block says what the scan could not see, and
/// a partial scan must never read as a clean one.
/// </summary>
public sealed record AdrSnapshot
{
    public sealed record Evidence(string Stage, string Channel, string Path, string Proof, double Confidence, string? Rung);

    public sealed record Band(string Label, IReadOnlyList<string> Channels);

    public sealed record Risk(bool? Pinned, IReadOnlyList<string> Factors, IReadOnlyList<string> CredentialKinds,
        IReadOnlyList<string> EnvNames, string? Transport, IReadOnlyList<string> Destinations, bool Unattended);

    public sealed record Asset(string AssetId, string Kind, string Name, string Identity, string? CatalogId, string? Vendor,
        string? Version, string? InstallPath, string? InstallRoot, string? InstallMethod, string Liveness, Band? Confidence,
        Risk? Risk, string? Sanction, IReadOnlyList<string> Flags);

    public sealed record Finding(string Rule, string Severity, string AssetId, string Summary, IReadOnlyList<Evidence> Evidence);

    public sealed record ReviewItem(string Path, double Score, IReadOnlyList<string> Signals);

    public sealed record Coverage(int RootsSwept, int BoundariesHit, int Denied, int Unavailable, int Truncated, int Probes, int OutOfScope)
    {
        public static readonly Coverage Empty = new(0, 0, 0, 0, 0, 0, 0);

        /// <summary>Upstream's own definition: nothing went unread. Never a claim that the inventory is complete.</summary>
        public bool IsComplete => BoundariesHit == 0 && Denied == 0 && Unavailable == 0 && Truncated == 0;

        public int GapCount => BoundariesHit + Denied + Unavailable + Truncated;
    }

    public required string SchemaVersion { get; init; }
    public required string CatalogVersion { get; init; }
    public required string Hostname { get; init; }
    public required string Username { get; init; }
    public required string Platform { get; init; }
    public required string Timestamp { get; init; }
    public IReadOnlyList<Asset> Assets { get; init; } = [];
    public IReadOnlyList<Finding> Findings { get; init; } = [];
    public IReadOnlyList<ReviewItem> ReviewQueue { get; init; } = [];
    public Coverage CoverageInfo { get; init; } = Coverage.Empty;

    public const int SupportedSchemaMajor = 1;

    public Asset? AssetById(string id) => Assets.FirstOrDefault(a => a.AssetId == id);

    /// <summary>When ADR ran the scan (RFC 3339), in Unix ms; null when upstream omitted or reshaped it.</summary>
    public long? GeneratedAtMs =>
        Timestamp.Contains('T') && DateTimeOffset.TryParse(Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToUnixTimeMilliseconds()
            : null;

    // ---- Decoding ----

    public static AdrSnapshot Decode(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            throw AdrSnapshotException.NotASnapshot();
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw AdrSnapshotException.NotASnapshot();
            // The schema major first, so a future major fails with a message, not deep inside a nested type.
            var version = "1.0";
            if (root.TryGetProperty("schema_version", out var v) && v.ValueKind != JsonValueKind.Null)
            {
                version = v.ValueKind == JsonValueKind.String ? v.GetString()! : throw AdrSnapshotException.NotASnapshot();
            }
            var majorText = version.Split('.')[0];
            if (!int.TryParse(majorText, NumberStyles.None, CultureInfo.InvariantCulture, out var major) || major != SupportedSchemaMajor)
            {
                throw AdrSnapshotException.UnsupportedSchema(version);
            }
            // A JSON object with none of the snapshot's keys is some other file, not an old snapshot.
            if (!root.TryGetProperty("assets", out _) && !root.TryGetProperty("findings", out _) && !root.TryGetProperty("schema_version", out _))
            {
                throw AdrSnapshotException.NotASnapshot();
            }
            try
            {
                return new AdrSnapshot
                {
                    SchemaVersion = version,
                    CatalogVersion = OptString(root, "catalog_version") ?? "unknown",
                    Hostname = OptString(root, "hostname") ?? "",
                    Username = OptString(root, "username") ?? "",
                    Platform = OptString(root, "platform") ?? "",
                    Timestamp = OptString(root, "timestamp") ?? "",
                    Assets = OptList(root, "assets", ReadAsset),
                    Findings = OptList(root, "findings", ReadFinding),
                    ReviewQueue = OptList(root, "review_queue", e => new ReviewItem(Str(e, "path"), Num(e, "score"), Strings(e, "signals"))),
                    CoverageInfo = Present(root, "coverage") is { } c ? ReadCoverage(c) : Coverage.Empty,
                };
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or KeyNotFoundException)
            {
                throw AdrSnapshotException.NotASnapshot();
            }
        }
    }

    public static AdrSnapshot Load(string path) => Decode(File.ReadAllText(path));

    /// <summary>The newest <c>snapshot-*.json</c> in a folder by modification time; null when there is none.</summary>
    public static string? NewestSnapshotPath(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return null;
            return new DirectoryInfo(directory).EnumerateFiles("snapshot-*.json")
                .Where(f => !f.Name.StartsWith('.') && f.Extension == ".json" && !f.Attributes.HasFlag(FileAttributes.Hidden))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Asset ReadAsset(JsonElement e) => new(
        Str(e, "asset_id"), Str(e, "kind"), Str(e, "name"), Str(e, "identity"),
        OptString(e, "catalog_id"), OptString(e, "vendor"), OptString(e, "version"), OptString(e, "install_path"),
        OptString(e, "install_root"), OptString(e, "install_method"), Str(e, "liveness"),
        Present(e, "confidence") is { } band ? new Band(Str(band, "label"), Strings(band, "channels")) : null,
        Present(e, "risk") is { } risk
            ? new Risk(OptBool(risk, "pinned"), Strings(risk, "factors"), Strings(risk, "credential_kinds"), Strings(risk, "env_names"),
                OptString(risk, "transport"), Strings(risk, "destinations"), Bool(risk, "unattended"))
            : null,
        OptString(e, "sanction"), Strings(e, "flags"));

    private static Finding ReadFinding(JsonElement e) => new(
        Str(e, "rule"), Str(e, "severity"), Str(e, "asset_id"), Str(e, "summary"),
        List(e, "evidence", x => new Evidence(Str(x, "stage"), Str(x, "channel"), Str(x, "path"), Str(x, "proof"), Num(x, "confidence"), OptString(x, "rung"))));

    private static Coverage ReadCoverage(JsonElement e) => new(
        Count(e, "roots_swept"), Count(e, "boundaries_hit"), Count(e, "denied"), Count(e, "unavailable"),
        Count(e, "truncated"), Count(e, "probes"), Count(e, "out_of_scope"));

    // Required fields throw (macOS's Codable fails the whole file on them); optional ones may be absent or null.

    private static JsonElement? Present(JsonElement e, string key) =>
        e.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static JsonElement Required(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && Present(e, key) is { } value ? value : throw new FormatException(key);

    private static string Str(JsonElement e, string key) =>
        Required(e, key) is { ValueKind: JsonValueKind.String } s ? s.GetString()! : throw new FormatException(key);

    private static string? OptString(JsonElement e, string key) => Present(e, key) switch
    {
        null => null,
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        _ => throw new FormatException(key),
    };

    private static double Num(JsonElement e, string key) =>
        Required(e, key) is { ValueKind: JsonValueKind.Number } n ? n.GetDouble() : throw new FormatException(key);

    private static bool Bool(JsonElement e, string key) => Required(e, key).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new FormatException(key),
    };

    private static bool? OptBool(JsonElement e, string key) => Present(e, key)?.ValueKind switch
    {
        null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new FormatException(key),
    };

    private static List<T> List<T>(JsonElement e, string key, Func<JsonElement, T> read) =>
        Required(e, key) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().Select(read).ToList() : throw new FormatException(key);

    private static List<T> OptList<T>(JsonElement e, string key, Func<JsonElement, T> read) =>
        Present(e, key) is null ? [] : List(e, key, read);

    private static List<string> Strings(JsonElement e, string key) =>
        List(e, key, x => x.ValueKind == JsonValueKind.String ? x.GetString()! : throw new FormatException(key));

    private static int Count(JsonElement e, string key) =>
        Required(e, key) is { ValueKind: JsonValueKind.Array } array ? array.GetArrayLength() : throw new FormatException(key);

    // ---- Findings (macOS AgentSecurityFinding.findings(from:existing:now:)) ----

    public const string Source = "discovery";

    /// <summary>ADR's words <c>high</c>/<c>medium</c>/<c>low</c>; anything unknown lands on medium rather than being dropped.</summary>
    public static FindingSeverity Severity(string word) => word.ToLowerInvariant() switch
    {
        "high" or "critical" => FindingSeverity.High,
        "low" or "info" => FindingSeverity.Info,
        _ => FindingSeverity.Medium,
    };

    /// <summary>Human titles for the rules Discovery raises today; an unknown rule is prettified.</summary>
    public static string Title(string rule)
    {
        switch (rule)
        {
            case "unpinned_mcp_server": return "Unpinned MCP server";
            case "plaintext_transport": return "MCP server over plain HTTP";
            case "undeclared_mcp_server": return "Running MCP server nobody declared";
            case "unattended_execution": return "Permission checks bypassed";
            case "third_party_destination": return "MCP server reaches outside your domains";
        }
        var words = rule.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return rule;
        var first = words[0];
        return string.Join(" ", new[] { char.ToUpperInvariant(first[0]) + first[1..].ToLowerInvariant() }.Concat(words.Skip(1)));
    }

    /// <summary>A path "Show in folder" may select: absolute (or under <c>~</c>), with no <c>..</c> in it.</summary>
    public static string? RevealablePath(string? path)
    {
        var trimmed = path?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (!(SecurityFindings.IsAbsolute(trimmed) || trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal)
              || trimmed.StartsWith('/'))) return null;
        return trimmed.Split('/', '\\').Contains("..") ? null : trimmed;
    }

    /// <summary>
    /// Every timestamp is fixed for a given snapshot (first seen carried over, last seen from the scan's own
    /// time), so rebuilding the findings does not churn the UI.
    /// </summary>
    public IReadOnlyList<SecurityFinding> ToFindings(IEnumerable<SecurityFinding> existing, long nowMs)
    {
        var previous = new Dictionary<string, SecurityFinding>();
        foreach (var f in existing) previous.TryAdd(f.Id, f);
        var scannedAt = GeneratedAtMs;
        return Findings.Select(finding =>
        {
            var asset = AssetById(finding.AssetId);
            var evidence = finding.Evidence.Select(e => e.Path.Length == 0 ? e.Proof : $"{e.Proof} — {e.Path}").ToList();
            var id = SecurityFindings.StableId(Source, finding.Rule, finding.AssetId, evidence);
            previous.TryGetValue(id, out var before);
            var firstSeen = before?.FirstSeenMs ?? nowMs;
            return new SecurityFinding
            {
                Id = id,
                Source = Source,
                Rule = finding.Rule,
                Severity = Severity(finding.Severity),
                Title = Title(finding.Rule),
                Summary = finding.Summary,
                Evidence = evidence,
                AssetName = asset?.Name,
                RevealPath = finding.Evidence.Select(e => RevealablePath(e.Path)).FirstOrDefault(p => p is not null) ?? RevealablePath(asset?.InstallPath),
                FirstSeenMs = firstSeen,
                LastSeenMs = scannedAt ?? before?.LastSeenMs ?? firstSeen,
                // The asset and the rule: a reworded proof is a new finding, but stays one row.
                GroupSubject = finding.AssetId,
            };
        }).ToList();
    }
}

public sealed class AdrSnapshotException : Exception
{
    private AdrSnapshotException(string message) : base(message)
    {
    }

    public static AdrSnapshotException NotASnapshot() => new("The file is not an ADR Discovery snapshot.");

    public static AdrSnapshotException UnsupportedSchema(string version) =>
        new($"Snapshot schema {version} is newer than this Kannu understands (1.x). Update Kannu.");
}

/// <summary>What the last ingested snapshot said about itself, shown in Settings so a partial scan is never taken for a clean one.</summary>
public sealed record AdrScanRecord(long DateMs, string Origin, string FileName, int AssetCount, int FindingCount, int ReviewCount,
    bool CoverageComplete, int CoverageGaps, string CatalogVersion, string SchemaVersion, string Platform)
{
    public const string OriginKannu = "kannu";
    public const string OriginWatched = "watched";

    public static AdrScanRecord From(AdrSnapshot snapshot, string origin, string fileName, long nowMs) => new(
        nowMs, origin, fileName, snapshot.Assets.Count, snapshot.Findings.Count, snapshot.ReviewQueue.Count,
        snapshot.CoverageInfo.IsComplete, snapshot.CoverageInfo.GapCount, snapshot.CatalogVersion, snapshot.SchemaVersion, snapshot.Platform);
}
