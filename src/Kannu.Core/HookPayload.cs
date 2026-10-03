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

/// <summary>
/// How the agent invoked the hook. Same positional shape as the macOS script, so the installers'
/// tables carry over: <c>kannu-hook &lt;state&gt; &lt;provider&gt; [hook_event] [matcher_key]</c>.
/// <paramref name="ArgState"/> is the installer's state for the event; <paramref name="MatcherKey"/> is
/// set only for a matcher-scoped group, whose state is then trusted as is.
/// </summary>
public sealed record HookInvocation(string ArgState, string Provider, string HookEvent, string MatcherKey)
{
    public static HookInvocation FromArgs(IReadOnlyList<string> args) => new(
        args.Count > 0 ? args[0] : "thinking",
        args.Count > 1 ? args[1] : "unknown",
        args.Count > 2 ? args[2] : "unknown",
        args.Count > 3 ? args[3] : "");
}

/// <summary>What the hook can learn about the process that ran it. Injected so tests can fake it.</summary>
/// <param name="CopilotCli">The <c>COPILOT_CLI</c> variable is set (Copilot CLI sets it for what it spawns).</param>
/// <param name="HasConsole">The hook runs attached to a visible console: a terminal agent, not an editor's extension host.</param>
/// <param name="Home">The user profile folder.</param>
/// <param name="Host">Where the agent's window is, for click-through; null when it could not be found.</param>
public sealed record HookEnvironment(bool CopilotCli, bool HasConsole, string Home, HookHost? Host = null);

/// <summary>The window an agent runs in: its owning process, or a classic console window.</summary>
public sealed record HookHost(int? Pid, string? Name, long? Window);

/// <summary>
/// The hook's stdin JSON, read leniently: missing, wrong-typed or non-object input reads as an empty
/// object, exactly as the macOS script does, so a malformed payload still moves the light by the
/// installer's state.
/// </summary>
internal sealed class HookPayload : IDisposable
{
    private readonly JsonDocument? _doc;

    public JsonElement Root { get; }

    /// <summary>Length of the raw payload, used to tell repeated tool completions apart.</summary>
    public int RawLength { get; }

    public string Tool { get; }

    public JsonElement ToolInput { get; }

    private HookPayload(JsonDocument? doc, JsonElement root, int rawLength)
    {
        _doc = doc;
        Root = root;
        RawLength = rawLength;
        (Tool, ToolInput) = ResolveTool();
    }

    public static HookPayload Parse(string raw)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 256 });
                if (doc.RootElement.ValueKind == JsonValueKind.Object) return new HookPayload(doc, doc.RootElement, raw.Length);
                doc.Dispose();
            }
            catch (JsonException)
            {
                // A deeply nested or malformed document reads as {}.
            }
        }
        return new HookPayload(null, default, raw.Length);
    }

    public bool IsObject => Root.ValueKind == JsonValueKind.Object;

    /// <summary>The first non-blank string among <paramref name="keys"/>, trimmed; "" when none.</summary>
    public string Pick(params string[] keys) => PickFrom(Root, keys);

    public bool IsTrue(string key) =>
        IsObject && Root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    public bool Has(string key) => IsObject && Root.TryGetProperty(key, out _);

    public JsonElement Get(string key) =>
        IsObject && Root.TryGetProperty(key, out var v) ? v : default;

    public static string PickFrom(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object) return "";
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                && v.GetString()?.Trim() is { Length: > 0 } s)
            {
                return s;
            }
        }
        return "";
    }

    public static bool HasAny(JsonElement element, params string[] keys) =>
        element.ValueKind == JsonValueKind.Object && keys.Any(k => element.TryGetProperty(k, out _));

    /// <summary>
    /// The tool name and its input across every agent's spelling, inferring a name from the input's
    /// shape when the agent sends none (as the macOS script does).
    /// </summary>
    private (string Tool, JsonElement Input) ResolveTool()
    {
        var nested = Get("tool");
        var tool = Pick("tool_name", "toolName", "name");
        if (tool.Length == 0 && nested.ValueKind == JsonValueKind.String) tool = nested.GetString()?.Trim() ?? "";
        if (tool.Length == 0 && nested.ValueKind == JsonValueKind.Object) tool = PickFrom(nested, "name", "tool_name");

        var input = Get("tool_input");
        if (input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            input = FirstPresent(Get("input"), Get("arguments"));
            if (input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null && nested.ValueKind == JsonValueKind.Object)
            {
                input = FirstPresent(
                    nested.TryGetProperty("input", out var i) ? i : default,
                    nested.TryGetProperty("arguments", out var a) ? a : default);
            }
        }

        if (tool.Length == 0 && input.ValueKind == JsonValueKind.Object)
        {
            tool = PickFrom(input, "tool_name", "name");
            if (tool.Length == 0)
            {
                if (HasAny(input, "search_term", "searchTerm", "query")) tool = "WebSearch";
                else if (HasAny(input, "url", "uri")) tool = "WebFetch";
                else if (HasAny(input, "questions")) tool = "AskQuestion";
                else if (HasAny(input, "command", "working_directory", "description")) tool = "Shell";
            }
        }
        return (tool, input);
    }

    /// <summary>Python's <c>a or b</c>: the first value that is present and not empty/false.</summary>
    private static JsonElement FirstPresent(JsonElement a, JsonElement b) => IsTruthy(a) ? a : b;

    private static bool IsTruthy(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
        JsonValueKind.String => e.GetString()!.Length > 0,
        JsonValueKind.Object => e.EnumerateObject().Any(),
        JsonValueKind.Array => e.GetArrayLength() > 0,
        JsonValueKind.Number => e.GetDouble() != 0,
        _ => true,
    };

    public void Dispose() => _doc?.Dispose();
}
