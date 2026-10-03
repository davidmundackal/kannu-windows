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
/// The hook rebuilds the record on every write from the fields below, so a field not listed here is
/// not carried — the same as the macOS hook.
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

    /// <summary>The chat's title, when the agent sends one.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("project")]
    public string? Project { get; set; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    /// <summary>Tool failures since the last prompt. Diagnostic only.</summary>
    [JsonPropertyName("tool_errors")]
    public int? ToolErrors { get; set; }

    /// <summary>The session runs with permission checks bypassed. Sticky for the session.</summary>
    [JsonPropertyName("unattended")]
    public bool? Unattended { get; set; }

    /// <summary>The run ended on an error (StopFailure, or an Antigravity Stop carrying one).</summary>
    [JsonPropertyName("ended_on_error")]
    public bool? EndedOnError { get; set; }

    /// <summary>For a subagent's file: the chat it belongs to, so its card folds into that chat's.</summary>
    [JsonPropertyName("parent_id")]
    public string? ParentId { get; set; }

    [JsonPropertyName("turn_started_ms")]
    public long? TurnStartedMs { get; set; }

    [JsonPropertyName("turn_ended_ms")]
    public long? TurnEndedMs { get; set; }

    [JsonPropertyName("turn_tool_calls")]
    public int? TurnToolCalls { get; set; }

    [JsonPropertyName("turn_tool_ids")]
    public List<string>? TurnToolIds { get; set; }

    /// <summary>Size of the Claude transcript when the turn began; tokens after it belong to the turn.</summary>
    [JsonPropertyName("turn_transcript_offset")]
    public long? TurnTranscriptOffset { get; set; }

    [JsonPropertyName("transcript_path")]
    public string? TranscriptPath { get; set; }

    /// <summary>
    /// Windows only, for click-through: the process owning the window the agent runs in (an editor,
    /// Windows Terminal, Claude Desktop) and its name, so a reused pid is not trusted.
    /// </summary>
    [JsonPropertyName("host_pid")]
    public int? HostPid { get; set; }

    [JsonPropertyName("host_name")]
    public string? HostName { get; set; }

    /// <summary>Windows only: the classic console window a terminal agent runs in, as a number.</summary>
    [JsonPropertyName("host_window")]
    public long? HostWindow { get; set; }

    /// <summary>Status files are untrusted input: any process running as the user can write one.</summary>
    internal const int MaxFileBytes = 64 * 1024;

    public string ToJson() => JsonSerializer.Serialize(this, StatusJsonContext.Default.StatusRecord);

    public StatusRecord Clone()
    {
        var copy = (StatusRecord)MemberwiseClone();
        copy.TurnToolIds = TurnToolIds is null ? null : [.. TurnToolIds];
        return copy;
    }

    /// <summary>
    /// Parses a status file's text field by field, the way the macOS hook reads one: a field of the
    /// wrong type is dropped, not fatal, and counts must be non-negative integers. Returns null only for
    /// text that is not a JSON object at all. Never throws.
    /// </summary>
    public static StatusRecord? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxFileBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var ids = new List<string>();
            if (root.TryGetProperty("turn_tool_ids", out var idArray) && idArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in idArray.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } id) ids.Add(id);
                }
            }

            return new StatusRecord
            {
                State = Str(root, "state") ?? "",
                Ts = Number(root, "ts") ?? 0,
                Provider = Str(root, "provider") ?? "",
                HookEvent = Str(root, "hook_event"),
                Name = Str(root, "name") ?? Str(root, "title") ?? Str(root, "conversation_title"),
                Project = Str(root, "project") ?? Str(root, "project_name") ?? Str(root, "workspace_name"),
                Cwd = Str(root, "cwd"),
                ToolErrors = (int?)Count(root, "tool_errors", int.MaxValue),
                Unattended = root.TryGetProperty("unattended", out var u) && u.ValueKind == JsonValueKind.True ? true : null,
                EndedOnError = root.TryGetProperty("ended_on_error", out var e) && e.ValueKind == JsonValueKind.True ? true : null,
                ParentId = Str(root, "parent_id"),
                TurnStartedMs = Count(root, "turn_started_ms"),
                TurnEndedMs = Count(root, "turn_ended_ms"),
                TurnToolCalls = (int?)Count(root, "turn_tool_calls", int.MaxValue),
                TurnToolIds = ids.Count > 0 ? ids : null,
                TurnTranscriptOffset = Count(root, "turn_transcript_offset"),
                TranscriptPath = Str(root, "transcript_path"),
                HostPid = (int?)Count(root, "host_pid", int.MaxValue),
                HostName = Str(root, "host_name"),
                HostWindow = Count(root, "host_window"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            && v.GetString()?.Trim() is { Length: > 0 } s ? s : null;

    /// <summary>Any JSON number, integral or not (a hostile or older writer may store a float).</summary>
    private static long? Number(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number) return null;
        if (v.TryGetInt64(out var whole)) return whole;
        return v.TryGetDouble(out var d) && double.IsFinite(d) && Math.Abs(d) < 9.2e18 ? (long)d : null;
    }

    /// <summary>A non-negative integer, as the macOS hook's <c>ht_int</c> accepts; anything else is absent.</summary>
    private static long? Count(JsonElement root, string key, long max = long.MaxValue) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var n) && n >= 0 && n <= max ? n : null;
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StatusRecord))]
internal sealed partial class StatusJsonContext : JsonSerializerContext;
