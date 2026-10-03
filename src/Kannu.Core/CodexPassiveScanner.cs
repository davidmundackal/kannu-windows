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

namespace Kannu.Core;

/// <summary>What the newest deciding record of a Codex rollout says.</summary>
public enum CodexTailState
{
    /// <summary>A turn is open: the model is thinking or writing.</summary>
    Working,

    /// <summary>A tool call or command is running.</summary>
    ToolInFlight,

    /// <summary>An exec or apply_patch approval request with no decision after it.</summary>
    AwaitingApproval,

    /// <summary>The turn completed (task_complete, or a legacy rollout's final assistant message).</summary>
    Finished,

    /// <summary>The user interrupted the turn (turn_aborted).</summary>
    Aborted,

    /// <summary>The turn ended on an error event.</summary>
    Failed,

    /// <summary>Nothing decisive in the tail (only bookkeeping, or unparseable).</summary>
    Unknown,
}

/// <param name="RecordTimestampMs">The deciding record's own timestamp, when it has one.</param>
public sealed record CodexTailResult(CodexTailState State, long? RecordTimestampMs)
{
    public static readonly CodexTailResult Unknown = new(CodexTailState.Unknown, null);
}

/// <summary>
/// Codex CLI sessions seen without Kannu's hook. Codex writes every session to
/// <c>$CODEX_HOME/sessions/YYYY/MM/DD/rollout-&lt;timestamp&gt;-&lt;uuid&gt;.jsonl</c>; Kannu reads the
/// first line (<c>session_meta</c>: id, cwd) and a bounded tail of each rollout modified within the
/// stale window. macOS Kannu has no hookless Codex path (it reads rollouts only to name hook cards),
/// so these rules are designed from the rollout format, in the shape of <see cref="ClaudePassiveScanner"/>.
/// There is no process check: an open turn whose rollout stops changing ages out instead.
/// Never reads or logs message content beyond the title the name parser already derives.
/// </summary>
public sealed class CodexPassiveScanner(SessionLogParser parser)
{
    /// <summary>Escalating tail windows, as for Claude transcripts: one record can exceed the first.</summary>
    internal static readonly int[] TailWindowLimits = [16_000, 262_144, 1_048_576, 4_194_304];

    private const int MetaByteLimit = 64 * 1024;

    private readonly Dictionary<string, (DateTime Mtime, long Size, CodexTailResult Result)> _tailCache = [];
    private readonly Dictionary<string, (string? Id, string? Cwd)> _metaCache = [];

    /// <summary>
    /// The sessions behind every rollout modified within <see cref="AgentTimings.StaleMinutes"/>.
    /// Conversations in <paramref name="hookedIds"/> are skipped: a hook card always wins.
    /// </summary>
    public IReadOnlyList<AgentSession> Scan(AgentTimings timings, long nowMs, IReadOnlySet<string>? hookedIds = null)
    {
        var sessions = new List<AgentSession>();
        var seen = new HashSet<string>();
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime;
        foreach (var path in parser.RecentSessionPaths(SessionLogProvider.Codex, timings.StaleMinutes, nowUtc))
        {
            if (!Stat(path, out var mtime, out var size)) continue;
            var (metaId, cwd) = Meta(path);
            var id = AgentStateMachine.IsHookConversationId(metaId) ? metaId! : SessionLogParser.SessionId(path, SessionLogProvider.Codex);
            if (!AgentStateMachine.IsHookConversationId(id) || hookedIds?.Contains(id) == true || !seen.Add(id)) continue;

            var tail = Tail(path, mtime, size);
            var mtimeMs = new DateTimeOffset(mtime).ToUnixTimeMilliseconds();
            if (State(tail, mtimeMs, nowMs, timings) is not { } verdict) continue;

            sessions.Add(new AgentSession
            {
                Id = $"codex-{id}",
                Provider = "codex",
                ConversationId = id,
                ChatName = parser.DisplayChatName(path, SessionLogProvider.Codex),
                ProjectName = StatusPaths.ProjectName(cwd) ?? parser.ProjectName(path, SessionLogProvider.Codex),
                RawState = verdict.RawState,
                DisplayState = verdict.State,
                UpdatedAtMs = verdict.UpdatedAtMs,
                IsVisible = verdict.Visible,
                Cwd = cwd,
                RunError = tail.State == CodexTailState.Failed ? RunError.Failed : null,
            });
        }
        return sessions;
    }

    /// <summary>
    /// A tail verdict to a card, with the hook clocks: red for the usual collapse window after the
    /// turn ends, yellow capped like an uncorroborated hook yellow, green only while the rollout
    /// shows activity within <see cref="AgentStateMachine.PassiveWorkingStaleMs"/>. Null means no card.
    /// </summary>
    public static (string RawState, AgentLightState State, bool Visible, long UpdatedAtMs)? State(
        CodexTailResult tail, long mtimeMs, long nowMs, AgentTimings timings)
    {
        var raw = tail.State switch
        {
            CodexTailState.Working => "thinking",
            CodexTailState.ToolInFlight => "executing",
            CodexTailState.AwaitingApproval => "awaiting_input",
            CodexTailState.Finished => "stopped",
            CodexTailState.Aborted => "aborted",
            CodexTailState.Failed => "error",
            _ => null,
        };
        if (raw is null) return null;

        // Activity clocks run from the newest write (a long command still streams output); the end of a
        // turn and an approval prompt run from the record itself, so later bookkeeping cannot extend them.
        var active = tail.State is CodexTailState.Working or CodexTailState.ToolInFlight;
        var ts = active ? Math.Max(tail.RecordTimestampMs ?? mtimeMs, mtimeMs) : tail.RecordTimestampMs ?? mtimeMs;
        var (state, visible) = AgentStateMachine.ResolveHookState(raw, nowMs - ts, timings.CollapseMs, timings.InactiveMs,
            activeStaleMs: AgentStateMachine.PassiveWorkingStaleMs);
        return (raw, state, visible, ts);
    }

    private CodexTailResult Tail(string path, DateTime mtime, long size)
    {
        if (_tailCache.TryGetValue(path, out var cached) && cached.Mtime == mtime && cached.Size == size) return cached.Result;
        var result = CodexTailResult.Unknown;
        foreach (var limit in TailWindowLimits)
        {
            if (SessionLogParser.ReadTrailing(path, limit) is not { } text) continue;
            result = TailStateFromText(text);
            if (result.State != CodexTailState.Unknown || limit >= size) break;
        }
        if (_tailCache.Count > 64) _tailCache.Clear();
        _tailCache[path] = (mtime, size, result);
        return result;
    }

    /// <summary>Classifies the newest deciding record of a rollout tail (newest line first).</summary>
    public static CodexTailResult TailStateFromText(string text)
    {
        // Rollouts from before Codex persisted task_started/task_complete end a turn with the
        // assistant's message and nothing else.
        var hasTurnMarkers = text.Contains("\"task_started\"", StringComparison.Ordinal)
                             || text.Contains("\"task_complete\"", StringComparison.Ordinal);
        foreach (var line in Enumerable.Reverse(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)))
        {
            using var doc = SessionLogParser.TryParse(line);
            if (doc?.RootElement is not { ValueKind: JsonValueKind.Object } json) continue;
            if (SessionLogParser.Obj(json, "payload") is not { } payload) continue;
            var timestamp = SessionLogParser.RecordTimestamp(json);
            var kind = SessionLogParser.Str(payload, "type");
            CodexTailState? state = SessionLogParser.Str(json, "type") switch
            {
                "event_msg" => kind switch
                {
                    "task_complete" => CodexTailState.Finished,
                    "turn_aborted" => CodexTailState.Aborted,
                    "error" => CodexTailState.Failed,
                    "exec_approval_request" or "apply_patch_approval_request" => CodexTailState.AwaitingApproval,
                    "exec_command_begin" or "patch_apply_begin" or "mcp_tool_call_begin" or "web_search_begin" => CodexTailState.ToolInFlight,
                    "agent_message" when !hasTurnMarkers => CodexTailState.Finished,
                    "task_started" or "user_message" or "agent_message" or "agent_reasoning" or "exec_command_end"
                        or "patch_apply_end" or "mcp_tool_call_end" or "web_search_end" => CodexTailState.Working,
                    // token_count, stream_error (a retry), and anything new: bookkeeping.
                    _ => null,
                },
                "response_item" => kind switch
                {
                    "function_call" or "custom_tool_call" or "local_shell_call" => CodexTailState.ToolInFlight,
                    "message" when !hasTurnMarkers && SessionLogParser.Str(payload, "role") == "assistant" => CodexTailState.Finished,
                    "message" or "reasoning" or "function_call_output" or "custom_tool_call_output" or "web_search_call" => CodexTailState.Working,
                    _ => null,
                },
                // session_meta, turn_context, compacted: bookkeeping.
                _ => null,
            };
            if (state is { } s) return new CodexTailResult(s, timestamp);
        }
        return CodexTailResult.Unknown;
    }

    private (string? Id, string? Cwd) Meta(string path)
    {
        // The first line never changes once written: cache by path alone (bounded).
        if (_metaCache.TryGetValue(path, out var cached)) return cached;
        var meta = ReadSessionMeta(path);
        if (meta.Id is null && meta.Cwd is null) return meta;
        if (_metaCache.Count > 256) _metaCache.Clear();
        _metaCache[path] = meta;
        return meta;
    }

    /// <summary>
    /// The id and cwd from a rollout's <c>session_meta</c> first line. Streams the first 64 KB through a
    /// reader instead of parsing the line whole: newer Codex versions put the full base instructions in
    /// that record, so it can be far longer than the window; id and cwd come before them.
    /// </summary>
    public static (string? Id, string? Cwd) ReadSessionMeta(string path)
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytes = new byte[Math.Min(MetaByteLimit, Math.Max(0, stream.Length))];
            var read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (read < bytes.Length) Array.Resize(ref bytes, read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
        var newline = Array.IndexOf(bytes, (byte)'\n');
        var line = newline >= 0 ? bytes.AsSpan(0, newline) : bytes.AsSpan();
        return SessionMetaFromLine(line, isComplete: newline >= 0);
    }

    internal static (string? Id, string? Cwd) SessionMetaFromLine(ReadOnlySpan<byte> line, bool isComplete)
    {
        string? type = null, id = null, cwd = null;
        try
        {
            var reader = new Utf8JsonReader(line, isFinalBlock: isComplete, state: default);
            var inPayload = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 1) inPayload = false;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                var depth = reader.CurrentDepth;
                var name = reader.GetString();
                if (!reader.Read()) break;
                if (depth == 1 && name == "type" && reader.TokenType == JsonTokenType.String) type = reader.GetString();
                else if (depth == 1 && name == "payload" && reader.TokenType == JsonTokenType.StartObject) inPayload = true;
                else if (inPayload && depth == 2 && reader.TokenType == JsonTokenType.String)
                {
                    if (name == "id") id = reader.GetString();
                    else if (name == "cwd") cwd = reader.GetString();
                }
                if (id is not null && cwd is not null && type is not null) break;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && !(depth == 1 && name == "payload"))
                {
                    if (!reader.TrySkip()) break;
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
        }
        // "type" may come after "payload" in a truncated line; only a different type disqualifies.
        return type is null or "session_meta" ? (id, string.IsNullOrEmpty(cwd) ? null : cwd) : (null, null);
    }

    private static bool Stat(string path, out DateTime mtime, out long size)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                mtime = info.LastWriteTimeUtc;
                size = info.Length;
                return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        mtime = default;
        size = 0;
        return false;
    }
}
