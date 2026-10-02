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
using System.Text.Json.Nodes;

namespace Kannu.Core;

public enum NotchStyle
{
    /// <summary>Attached to the top edge, rounded below: macOS Kannu's standard notch.</summary>
    Notch,

    /// <summary>A capsule floating just below the top edge: macOS Kannu's Dynamic Island style.</summary>
    FloatingPill,
}

/// <summary>
/// The user's preferences, in <c>%APPDATA%\Kannu\settings.json</c>. A missing, unreadable or
/// half-written file reads as the defaults, and an unknown value in it falls back to its default,
/// so a bad file can never stop Kannu from starting.
/// </summary>
public sealed record AppSettings
{
    public NotchStyle NotchStyle { get; init; } = NotchStyle.Notch;

    /// <summary>Hidden until an agent's light changes, the tray eye is clicked, or (if allowed) the top edge is hovered.</summary>
    public bool HideUntilActivity { get; init; } = true;

    /// <summary>While hidden, resting the pointer at the top edge reveals the notch. Off by default.</summary>
    public bool RevealOnTopEdge { get; init; }

    public bool OpenOnHover { get; init; } = true;

    /// <summary>Sign-in start was switched on once for a fresh install (macOS: on by default, once).</summary>
    public bool LaunchAtLoginInitialized { get; init; }

    /// <summary>The newest crash or freeze report already offered, so each is offered once.</summary>
    public string? LastOfferedReport { get; init; }

    public int? TermsAcceptedVersion { get; init; }

    /// <summary>ISO 8601, UTC.</summary>
    public string? TermsAcceptedAt { get; init; }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kannu", "settings.json");

    public static AppSettings Load(string path)
    {
        JsonObject root;
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject parsed) return new AppSettings();
            root = parsed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }

        var defaults = new AppSettings();
        return new AppSettings
        {
            NotchStyle = String(root, "notchStyle") switch
            {
                "notch" => NotchStyle.Notch,
                "floatingPill" => NotchStyle.FloatingPill,
                _ => defaults.NotchStyle,
            },
            HideUntilActivity = Bool(root, "hideUntilActivity") ?? defaults.HideUntilActivity,
            RevealOnTopEdge = Bool(root, "revealOnTopEdge") ?? defaults.RevealOnTopEdge,
            OpenOnHover = Bool(root, "openOnHover") ?? defaults.OpenOnHover,
            LaunchAtLoginInitialized = Bool(root, "launchAtLoginInitialized") ?? false,
            LastOfferedReport = String(root, "lastOfferedReport"),
            TermsAcceptedVersion = root["termsAcceptedVersion"] is JsonValue v && v.TryGetValue<int>(out var version) ? version : null,
            TermsAcceptedAt = String(root, "termsAcceptedAt"),
        };
    }

    /// <summary>Atomic: a temp file beside the target, renamed over it.</summary>
    public void Save(string path)
    {
        var root = new JsonObject
        {
            ["notchStyle"] = NotchStyle == NotchStyle.FloatingPill ? "floatingPill" : "notch",
            ["hideUntilActivity"] = HideUntilActivity,
            ["revealOnTopEdge"] = RevealOnTopEdge,
            ["openOnHover"] = OpenOnHover,
        };
        if (LaunchAtLoginInitialized) root["launchAtLoginInitialized"] = true;
        if (LastOfferedReport is { } offered) root["lastOfferedReport"] = offered;
        if (TermsAcceptedVersion is { } version) root["termsAcceptedVersion"] = version;
        if (TermsAcceptedAt is { } at) root["termsAcceptedAt"] = at;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    private static string? String(JsonObject root, string key) =>
        root[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? Bool(JsonObject root, string key) =>
        root[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
