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
using System.Text.Json.Serialization;

namespace Kannu.Core;

/// <summary>
/// One session's status file: <c>{provider}-{sessionId}.json</c> in the status directory.
/// The field names are the macOS Kannu contract (docs/STATUS_CONTRACT.md); do not rename them.
/// </summary>
public sealed class StatusRecord
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    /// <summary>Unix time in milliseconds of the write that set <see cref="State"/>.</summary>
    [JsonPropertyName("ts")]
    public long Ts { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("hook_event")]
    public string? HookEvent { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("project")]
    public string? Project { get; set; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    /// <summary>Status files are untrusted input: any process running as the user can write one.</summary>
    internal const int MaxFileBytes = 64 * 1024;

    public string ToJson() => JsonSerializer.Serialize(this, StatusJsonContext.Default.StatusRecord);

    /// <summary>Parses a status file's text. Returns null for anything malformed; never throws.</summary>
    public static StatusRecord? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxFileBytes) return null;
        try
        {
            return JsonSerializer.Deserialize(json, StatusJsonContext.Default.StatusRecord);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StatusRecord))]
internal sealed partial class StatusJsonContext : JsonSerializerContext;
