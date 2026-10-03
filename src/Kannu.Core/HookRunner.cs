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

/// <summary>What one hook call did, and the line the agent must read on stdout.</summary>
public sealed record HookResult(string Output, HookDecision Decision, string Provider, string SessionId);

/// <summary>
/// The body of <c>kannu-hook.exe</c>: one hook invocation, stdin JSON in, status file and stdout line
/// out. A port of the macOS hook script's Python body (v43), minus the security checks (later phase)
/// and the Unix terminal lookup. Kept in Core so tests drive the real thing.
/// </summary>
public static class HookRunner
{
    /// <summary>Past this the payload is not read; the event still moves the light by the installer's state.</summary>
    public const int MaxInputChars = 16 * 1024 * 1024;

    private const long StickyYellowMs = 300_000;

    private static readonly HashSet<string> TitleEvents =
        ["beforeSubmitPrompt", "stop", "SessionStart", "UserPromptSubmit", "Stop", "PreInvocation"];

    private static readonly HashSet<string> ToolEvents =
        ["preToolUse", "beforeMCPExecution", "postToolUse", "postToolUseFailure", "PreToolUse", "PostToolUse",
         "PostToolUseFailure", "BeforeTool", "AfterTool"];

    private static readonly HashSet<string> UnattendedModes =
        ["bypasspermissions", "dangerouslyskippermissions", "never", "yolo", "autoapprove"];

    public static HookResult Run(HookInvocation invocation, string stdin, string statusDirectory, long nowMs, HookEnvironment environment)
    {
        using var payload = HookPayload.Parse(stdin.Length > MaxInputChars ? "" : stdin);

        var provider = StatusPaths.SanitizeId(invocation.Provider, "unknown");
        var hookEvent = invocation.HookEvent is { Length: > 0 } and not "unknown"
            ? invocation.HookEvent
            : payload.Pick("hook_event_name", "hookEventName");

        var sessionId = StatusPaths.SanitizeId(payload.Pick(
            "agentId", "agent_id", "composerId", "composer_id", "conversation_id", "conversationId",
            "session_id", "sessionId", "thread_id"));

        // Claude Code and Qwen Code fire a subagent's hooks with its agent_id beside the parent's
        // session_id. The file stays the subagent's own; parent_id folds its card into the chat's.
        var parentId = "";
        if (provider is "claude" or "qwen" && payload.Pick("agent_id", "agentId").Length > 0)
        {
            parentId = StatusPaths.SanitizeId(payload.Pick("session_id", "sessionId"), "");
            if (parentId == sessionId) parentId = "";
        }

        // Copilot CLI reads the same hooks file as VS Code, so its events arrive as "vscode". The CLI
        // sets COPILOT_CLI and runs in a console; VS Code's extension host has neither. A conversation
        // already filed as copilot keeps it.
        if (provider == "vscode"
            && (environment.CopilotCli || environment.HasConsole
                || File.Exists(Path.Combine(statusDirectory, StatusPaths.StatusFileName("copilot", sessionId)))))
        {
            provider = "copilot";
        }

        var decision = HookEventMapper.Decide(provider, invocation, hookEvent, payload);
        var output = HookOutput.For(provider);
        var result = new HookResult(output, decision, provider, sessionId);
        if (decision.Action == HookAction.Ignore) return result;

        // SessionStart also fires for /compact and /resume mid-conversation; seeding idle there would
        // dim a session that is working.
        if (hookEvent == "SessionStart" && payload.Pick("source") is "compact" or "resume") return result with { Decision = HookDecision.Ignore };

        Directory.CreateDirectory(statusDirectory);
        var statusFile = Path.Combine(statusDirectory, StatusPaths.StatusFileName(provider, sessionId));

        // Claude runs every hook group of one event as separate processes in parallel; the merge below
        // compares against the file, so the whole read-modify-write is serialised.
        using var _ = StatusStore.TryLock(statusDirectory, TimeSpan.FromMilliseconds(1500));

        if (provider == "copilot")
        {
            // A Copilot CLI session an older hook filed as vscode: that card is this one.
            TryDelete(Path.Combine(statusDirectory, StatusPaths.StatusFileName("vscode", sessionId)));
        }

        if (decision.Action == HookAction.Delete)
        {
            // The session is gone: drop the card rather than leave a terminal state to age out.
            TryDelete(statusFile);
            return result;
        }

        var existing = StatusStore.Read(statusFile);
        var existingState = existing?.State ?? "";
        var state = decision.State;

        // Permission checks bypassed: sticky for the session, because it describes how the session
        // was launched, not what it is doing now.
        var mode = HookEventMapper.Compact(payload.Pick("permission_mode", "permissionMode", "approval_policy"));
        var unattended = existing?.Unattended == true || UnattendedModes.Contains(mode);

        // Tool failures since the last prompt (diagnostic only; an Esc interrupt is not an error).
        var toolErrors = existing?.ToolErrors ?? 0;
        var antigravityStopError = provider == "antigravity" && hookEvent == "Stop" && state != RawState.QuotaExceeded
                                   && payload.Pick("error").Length > 0;
        if (TurnMetrics.PromptEvents.Contains(hookEvent)) toolErrors = 0;
        else if (hookEvent is "PostToolUseFailure" or "postToolUseFailure" or "StopFailure")
        {
            if (!payload.IsTrue("is_interrupt")) toolErrors = Math.Min(toolErrors + 1, 999);
        }
        else if (antigravityStopError) toolErrors = Math.Min(toolErrors + 1, 999);

        // A run that ENDED on an error, decided from this event before the merge can substitute a
        // held yellow; a later stopped write that learns nothing new keeps the verdict.
        var endedOnError = state == RawState.Stopped
                           && (hookEvent == "StopFailure" || antigravityStopError || existing?.EndedOnError == true);

        var (mergedState, ts) = StateMerge.Apply(existing, state, hookEvent, nowMs);
        state = mergedState;

        var (turn, transcript) = TurnMetrics.Next(existing, new TurnMetrics.Input(
            provider, parentId, hookEvent,
            payload.Pick("transcript_path"),
            payload.IsTrue("stop_hook_active"),
            payload.Pick("tool_use_id", "toolUseId"),
            payload.Tool,
            payload.RawLength,
            nowMs,
            environment.Home,
            () => StatusStore.Read(Path.Combine(statusDirectory, StatusPaths.StatusFileName(provider, parentId)))?.TurnStartedMs ?? 0,
            TurnMetrics.FileSize));

        var (project, workdir) = ProjectAndWorkdir(payload);
        project = First(project, existing?.Project);
        workdir = First(workdir, existing?.Cwd);

        var name = TitleEvents.Contains(hookEvent)
            ? First(payload.Pick("conversation_title", "title", "chat_name", "conversation_name", "chatTitle", "bubbleTitle"), existing?.Name)
            : existing?.Name ?? "";
        if (state == RawState.QuotaExceeded) name = "Quota exceeded";
        if (ToolEvents.Contains(hookEvent) && HookEventMapper.Compact(name) == HookEventMapper.Compact(payload.Tool)) name = "";

        // Sticky yellow: while an approval card is open, Cursor's reasoning noise must not repaint it
        // green. Only afterAgentThought is absorbed, and the original ts is kept so the stale clock
        // still applies.
        if (existing is not null && existingState == "awaiting_input" && state is not (RawState.AwaitingInput or RawState.Stopped)
            && hookEvent == "afterAgentThought" && nowMs - existing.Ts <= StickyYellowMs)
        {
            var held = existing.Clone();
            held.Provider = provider;
            if (name.Length > 0) held.Name = name;
            if (project.Length > 0) held.Project = project;
            if (workdir.Length > 0) held.Cwd = workdir;
            held.ToolErrors = toolErrors > 0 ? toolErrors : null;
            if (unattended) held.Unattended = true;
            held.EndedOnError = null;
            ApplyTurn(held, turn, transcript);
            ApplyHost(held, environment.Host);
            TryWrite(statusFile, held);
            return result with { Decision = HookDecision.Write(RawStateWire.Parse(existingState) ?? RawState.AwaitingInput) };
        }

        var record = new StatusRecord
        {
            State = state.ToWire(),
            Ts = ts,
            Provider = provider,
            HookEvent = hookEvent,
            Name = NullIfEmpty(name),
            Project = NullIfEmpty(project),
            Cwd = NullIfEmpty(workdir),
            ToolErrors = toolErrors > 0 ? toolErrors : null,
            Unattended = unattended ? true : null,
            EndedOnError = endedOnError ? true : null,
            ParentId = NullIfEmpty(parentId),
        };
        ApplyTurn(record, turn, transcript);
        record.HostPid = existing?.HostPid;
        record.HostName = existing?.HostName;
        record.HostWindow = existing?.HostWindow;
        ApplyHost(record, environment.Host);
        TryWrite(statusFile, record);
        return result with { Decision = HookDecision.Write(state) };
    }

    private static (string Project, string Workdir) ProjectAndWorkdir(HookPayload payload)
    {
        foreach (var key in new[] { "workspace_roots", "workspacePaths", "workspace_paths" })
        {
            var roots = payload.Get(key);
            if (roots.ValueKind != JsonValueKind.Array || roots.GetArrayLength() == 0) continue;
            var first = roots[0];
            var root = (first.ValueKind == JsonValueKind.String ? first.GetString() : first.ToString()) ?? "";
            root = root.Replace("file://", "").TrimEnd('/');
            if (root.Length > 0) return (StatusPaths.ProjectName(root) ?? "", root);
            break;
        }

        // Claude Code sends cwd rather than workspace roots.
        var cwd = payload.Pick("cwd");
        if (cwd.Length == 0) return ("", "");
        var cleaned = cwd.Replace("file://", "").TrimEnd('/');
        return (StatusPaths.ProjectName(cleaned) ?? "", cleaned);
    }

    private static void ApplyTurn(StatusRecord record, Turn? turn, string transcript)
    {
        record.TurnStartedMs = turn?.StartedMs;
        record.TurnEndedMs = turn?.EndedMs;
        record.TurnToolCalls = turn?.ToolCalls;
        record.TurnToolIds = turn is null ? null : [.. turn.ToolIds];
        record.TurnTranscriptOffset = turn?.TranscriptOffset;
        record.TranscriptPath = NullIfEmpty(transcript);
    }

    /// <summary>A host found by this event replaces the recorded one; none found keeps it.</summary>
    private static void ApplyHost(StatusRecord record, HookHost? host)
    {
        if (host is null || (host.Pid is null && host.Window is null)) return;
        record.HostPid = host.Pid;
        record.HostName = host.Pid is null ? null : NullIfEmpty(host.Name ?? "");
        record.HostWindow = host.Window;
    }

    /// <summary>A full disk or a path replaced by a directory must not cost the agent its stdout line.</summary>
    private static void TryWrite(string path, StatusRecord record)
    {
        try
        {
            StatusStore.WriteAtomic(path, record);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string First(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a.Trim() : !string.IsNullOrWhiteSpace(b) ? b.Trim() : "";

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}

/// <summary>
/// The only thing the hook writes to stdout, per agent. Each host validates it differently, so this
/// is the macOS hook's table exactly.
/// </summary>
public static class HookOutput
{
    public const string Allow = """{"permission":"allow","continue":true}""";

    /// <summary>The line for <paramref name="provider"/> (after the vscode/copilot split).</summary>
    public static string For(string provider) => provider switch
    {
        // Codex validates strictly and rejects unknown keys; empty stdout with exit 0 is success.
        "codex" => "",
        // Gemini CLI parses stdout as JSON; Qwen Code and Copilot CLI need nothing: an empty object.
        "gemini" or "qwen" or "copilot" => "{}",
        _ => Allow,
    };

    /// <summary>
    /// What to print when the hook failed before it knew the provider for sure: the raw argument, with
    /// Copilot CLI recognised by its environment variable, as the macOS script's no-Python branch does.
    /// </summary>
    public static string Fallback(string providerArgument, bool copilotCli) =>
        providerArgument == "vscode" && copilotCli ? "{}" : For(providerArgument);
}
