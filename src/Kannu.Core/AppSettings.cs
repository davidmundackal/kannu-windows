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

/// <summary>Which monitor the notch appears on.</summary>
public enum NotchDisplay
{
    /// <summary>The main display (Windows' primary monitor).</summary>
    Primary,

    /// <summary>The monitor the pointer is on when the notch appears.</summary>
    Pointer,

    /// <summary>One monitor the user picked (<see cref="AppSettings.DisplayDevice"/>); the main display while it is not connected.</summary>
    Chosen,
}

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

    public NotchDisplay Display { get; init; } = NotchDisplay.Primary;

    /// <summary>Windows' device name of the chosen monitor (<c>\\.\DISPLAY2</c>), for <see cref="NotchDisplay.Chosen"/>.</summary>
    public string? DisplayDevice { get; init; }

    /// <summary>Leave the notch out of screenshots, recordings and screen sharing. Off by default.</summary>
    public bool HideFromCapture { get; init; }

    /// <summary>Stay out of full-screen apps, games and presentations. On by default.</summary>
    public bool HideInFullscreen { get; init; } = true;

    /// <summary>Ctrl+Alt+K opens and closes the notch. Off by default: a global shortcut takes the keys from every app.</summary>
    public bool ShortcutsEnabled { get; init; }

    // ADR (Uber's Agentic Detection and Response toolkit), a separate install (macOS defaults).
    /// <summary>Kannu runs <c>adr-discovery</c> itself: daily, after MCP changes, on Scan now. On by default (only acts once ADR is installed).</summary>
    public bool AdrRunScansEnabled { get; init; } = true;

    /// <summary>An extra folder searched for <c>adr-discovery.exe</c> and <c>uv.exe</c>; empty for the standard places.</summary>
    public string AdrToolDirectory { get; init; } = "";

    /// <summary>ADR's tenant policy (<c>--policy</c>); empty for none.</summary>
    public string AdrPolicyFile { get; init; } = "";

    /// <summary>ADR Detection on a chat the user picks: sends that transcript to Anthropic. Off by default.</summary>
    public bool AdrDetectionEnabled { get; init; }

    /// <summary>The user's <c>ADR\Detection</c> checkout (after <c>uv sync</c>).</summary>
    public string AdrDetectionCheckout { get; init; } = "";

    // Agent Security (macOS defaults).
    public bool DetectHiddenText { get; init; } = true;
    public bool WarnAgentAboutHiddenText { get; init; }
    public bool DetectSecrets { get; init; } = true;
    public bool DetectSensitivePaths { get; init; } = true;
    public bool WatchMcpServers { get; init; } = true;

    /// <summary>Refuse a tool call that matches the agent policy (Claude Code and Cursor). Off by default: report only.</summary>
    public bool EnforceAgentPolicy { get; init; }

    public bool PushHighFindings { get; init; } = true;
    public bool PushMediumFindings { get; init; }

    /// <summary>Show a brightness bar on the notch whenever the screen's brightness changes. Off by default.</summary>
    public bool BrightnessHud { get; init; }

    /// <summary>Ctrl+Alt+F1 / F2 step the brightness down and up in fine (1/64) steps. Off by default.</summary>
    public bool BrightnessShortcuts { get; init; }

    /// <summary>A red dot on the notch while an app captures the screen (best effort). Off by default.</summary>
    public bool RecordingIndicator { get; init; }

    /// <summary>The first-run welcome was shown.</summary>
    public bool OnboardingDone { get; init; }

    public LightStyle LightStyle { get; init; } = LightStyle.Classic;

    public LightColors LightColors { get; init; } = LightColors.Default;

    /// <summary>A picture behind the notch (a copy in Kannu's skins folder), or null.</summary>
    public string? SkinPath { get; init; }

    /// <summary>How dark the scrim over the skin is, 0 to 0.9, so the lights stay readable.</summary>
    public double SkinScrim { get; init; }

    /// <summary>Keep the PC awake while any agent is working (macOS "Smart caffeinate"). Off by default.</summary>
    public bool CaffeinateSmart { get; init; }

    /// <summary>Keep the PC awake until switched off (the notch's sun button). Ignored while smart is on.</summary>
    public bool CaffeinateManual { get; init; }

    /// <summary>Send the aggregate light to a phone or a webhook (macOS "Mobile notifications"). Off by default.</summary>
    public bool PushEnabled { get; init; }

    public PushProvider PushProvider { get; init; } = PushProvider.Ntfy;

    /// <summary>The ntfy server. The topic, Pushover keys and webhook URL are secrets: Credential Manager, not here.</summary>
    public string NtfyServer { get; init; } = "https://ntfy.sh";

    /// <summary>Also send "no agent active". Off by default, as on macOS.</summary>
    public bool PushOnInactive { get; init; }

    /// <summary>"Still waiting on you" after this many minutes of yellow; 0 is off (the default).</summary>
    public int WaitReminderMinutes { get; init; }

    /// <summary>Windows notifications when an agent needs you or finishes (D3). Off by default.</summary>
    public bool ToastsEnabled { get; init; }

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
            Display = String(root, "display") switch
            {
                "pointer" => NotchDisplay.Pointer,
                "chosen" => NotchDisplay.Chosen,
                _ => NotchDisplay.Primary,
            },
            DisplayDevice = String(root, "displayDevice"),
            HideFromCapture = Bool(root, "hideFromCapture") ?? false,
            HideInFullscreen = Bool(root, "hideInFullscreen") ?? defaults.HideInFullscreen,
            ShortcutsEnabled = Bool(root, "shortcutsEnabled") ?? false,
            OnboardingDone = Bool(root, "onboardingDone") ?? false,
            BrightnessHud = Bool(root, "brightnessHud") ?? false,
            BrightnessShortcuts = Bool(root, "brightnessShortcuts") ?? false,
            RecordingIndicator = Bool(root, "recordingIndicator") ?? false,
            DetectHiddenText = Bool(root, "detectHiddenText") ?? true,
            AdrRunScansEnabled = Bool(root, "adrRunScansEnabled") ?? true,
            AdrToolDirectory = String(root, "adrToolDirectory") ?? "",
            AdrPolicyFile = String(root, "adrPolicyFile") ?? "",
            AdrDetectionEnabled = Bool(root, "adrDetectionEnabled") ?? false,
            AdrDetectionCheckout = String(root, "adrDetectionCheckout") ?? "",
            WarnAgentAboutHiddenText = Bool(root, "warnAgentAboutHiddenText") ?? false,
            DetectSecrets = Bool(root, "detectSecrets") ?? true,
            DetectSensitivePaths = Bool(root, "detectSensitivePaths") ?? true,
            WatchMcpServers = Bool(root, "watchMcpServers") ?? true,
            EnforceAgentPolicy = Bool(root, "enforceAgentPolicy") ?? false,
            PushHighFindings = Bool(root, "pushHighFindings") ?? true,
            PushMediumFindings = Bool(root, "pushMediumFindings") ?? false,
            LightStyle = String(root, "lightStyle") == "minimal" ? LightStyle.Minimal : LightStyle.Classic,
            LightColors = new LightColors(
                Palette(root, "activeColor") ?? LightColors.Default.Active,
                Palette(root, "awaitingColor") ?? LightColors.Default.Awaiting,
                Palette(root, "stoppedColor") ?? LightColors.Default.Stopped).Valid(),
            SkinPath = String(root, "skinPath"),
            SkinScrim = root["skinScrim"] is JsonValue scrim && scrim.TryGetValue<double>(out var d) ? Math.Clamp(d, 0, 0.9) : 0,
            CaffeinateSmart = Bool(root, "caffeinateSmart") ?? false,
            PushEnabled = Bool(root, "pushEnabled") ?? false,
            PushProvider = String(root, "pushProvider") switch
            {
                "pushover" => PushProvider.Pushover,
                "webhook" => PushProvider.Webhook,
                _ => PushProvider.Ntfy,
            },
            NtfyServer = String(root, "ntfyServer") is { Length: > 0 } server ? server : defaults.NtfyServer,
            PushOnInactive = Bool(root, "pushOnInactive") ?? false,
            WaitReminderMinutes = root["waitReminderMinutes"] is JsonValue minutes && minutes.TryGetValue<int>(out var m) ? Math.Clamp(m, 0, 240) : 0,
            ToastsEnabled = Bool(root, "toastsEnabled") ?? false,
            CaffeinateManual = Bool(root, "caffeinateManual") ?? false,
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
        root["display"] = Display.ToString().ToLowerInvariant();
        if (DisplayDevice is { } device) root["displayDevice"] = device;
        root["hideFromCapture"] = HideFromCapture;
        root["hideInFullscreen"] = HideInFullscreen;
        root["shortcutsEnabled"] = ShortcutsEnabled;
        if (OnboardingDone) root["onboardingDone"] = true;
        root["brightnessHud"] = BrightnessHud;
        root["brightnessShortcuts"] = BrightnessShortcuts;
        root["recordingIndicator"] = RecordingIndicator;
        root["detectHiddenText"] = DetectHiddenText;
        root["adrRunScansEnabled"] = AdrRunScansEnabled;
        root["adrToolDirectory"] = AdrToolDirectory;
        root["adrPolicyFile"] = AdrPolicyFile;
        root["adrDetectionEnabled"] = AdrDetectionEnabled;
        root["adrDetectionCheckout"] = AdrDetectionCheckout;
        root["warnAgentAboutHiddenText"] = WarnAgentAboutHiddenText;
        root["detectSecrets"] = DetectSecrets;
        root["detectSensitivePaths"] = DetectSensitivePaths;
        root["watchMcpServers"] = WatchMcpServers;
        root["enforceAgentPolicy"] = EnforceAgentPolicy;
        root["pushHighFindings"] = PushHighFindings;
        root["pushMediumFindings"] = PushMediumFindings;
        root["lightStyle"] = LightStyle == LightStyle.Minimal ? "minimal" : "classic";
        root["activeColor"] = LightColors.Active.ToString().ToLowerInvariant();
        root["awaitingColor"] = LightColors.Awaiting.ToString().ToLowerInvariant();
        root["stoppedColor"] = LightColors.Stopped.ToString().ToLowerInvariant();
        if (SkinPath is { } skin) root["skinPath"] = skin;
        if (SkinScrim > 0) root["skinScrim"] = SkinScrim;
        root["caffeinateSmart"] = CaffeinateSmart;
        root["caffeinateManual"] = CaffeinateManual;
        root["pushEnabled"] = PushEnabled;
        root["pushProvider"] = PushProvider.ToString().ToLowerInvariant();
        root["ntfyServer"] = NtfyServer;
        root["pushOnInactive"] = PushOnInactive;
        root["waitReminderMinutes"] = WaitReminderMinutes;
        root["toastsEnabled"] = ToastsEnabled;
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

    private static PaletteColor? Palette(JsonObject root, string key) =>
        Enum.TryParse<PaletteColor>(String(root, key), ignoreCase: true, out var color) && Enum.IsDefined(color) ? color : null;

    private static bool? Bool(JsonObject root, string key) =>
        root[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
