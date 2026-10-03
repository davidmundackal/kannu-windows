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
using System.Text.RegularExpressions;

namespace Kannu.Core;

/// <summary>
/// Sensitive files (macOS hook v35): after a tool ran, the paths it read or changed, matched against
/// keys, credential and password stores, browser data, shell history, and files that run code at
/// login or configure an agent. The categories and their ids are the macOS ones; the path tables are
/// the Windows locations (plus the dot-files Git Bash, WSL-style tools and the agents keep under the
/// profile). Paths compare case-insensitively with either separator; the match key is the normalised
/// path, '/'-separated and lowercase, while the recorded path keeps the original case (profile folded
/// to <c>~</c>, '\'-separated).
/// </summary>
internal static class SensitivePaths
{
    internal const int MaxEntries = 5;

    internal readonly record struct Hit(string Category, string Access, string Path);

    internal static readonly HashSet<string> Events =
        ["PostToolUse", "postToolUse", "PostToolUseFailure", "postToolUseFailure", "AfterTool"];

    internal static readonly HashSet<string> FailureEvents = ["PostToolUseFailure", "postToolUseFailure"];

    internal static readonly IReadOnlyList<string> Categories =
    [
        "ssh_key", "gpg_key", "cloud_credentials", "token_file", "agent_credentials", "password_store", "keychain",
        "keychain_item", "browser_data", "shell_history", "autorun", "shell_startup", "agent_config", "env_file",
    ];

    internal static readonly HashSet<string> CategorySet = [.. Categories];

    /// <summary>Shell tools, by normalised name. PowerShell and cmd added for Windows agents.</summary>
    internal static readonly HashSet<string> ShellTools =
        ["bash", "shell", "runterminalcmd", "localshell", "execcommand", "runshellcommand", "terminal", "exec", "execute",
         "runcommand", "powershell", "pwsh", "cmd"];

    private static readonly HashSet<string> ListingTools = ["glob", "ls", "listdir", "listdirectory", "filesearch", "globfilesearch"];

    private static readonly string[] WriteWords =
        ["write", "edit", "replace", "patch", "create", "move", "rename", "copy", "delete", "insert", "append", "update", "save"];

    private static readonly string[] PathKeys =
        ["file_path", "filePath", "path", "target_file", "targetFile", "notebook_path", "absolute_path", "filename", "file",
         "source", "destination", "target"];

    private static readonly HashSet<string> RedirectsOut = [">", ">>", ">|", "&>", "&>>", ">&", "1>", "2>"];

    // Command sets, by lowercase name without .exe. The Windows and PowerShell names are added to the
    // macOS ones; mac-only commands (security, launchctl, crontab, ditto) have Windows analogues below.
    private static readonly HashSet<string> CopyCommands =
        ["cp", "mv", "ln", "install", "rsync", "scp", "copy", "move", "xcopy", "copy-item", "cpi", "move-item", "mi"];

    private static readonly HashSet<string> WriteCommands =
        ["tee", "touch", "rm", "truncate", "chmod", "chown", "shred", "unlink", "srm",
         "set-content", "add-content", "out-file", "new-item", "ni", "remove-item", "ri", "del", "erase", "rd", "rmdir",
         "rename-item", "ren", "rename", "tee-object", "clear-content", "icacls"];

    private static readonly HashSet<string> InPlaceCommands = ["sed", "gsed", "perl"];

    // `type` is a read on Windows (cmd's cat), not the POSIX name lookup, so it is not here.
    private static readonly HashSet<string> NameOnlyCommands =
        ["ls", "stat", "file", "du", "cd", "pushd", "popd", "mkdir", "md", "echo", "printf", "which", "realpath", "dirname",
         "basename", "readlink", "test", "[", "[[", "dir", "get-childitem", "gci", "test-path", "get-item", "gi",
         "resolve-path", "rvpa", "split-path", "join-path", "where", "get-command", "gcm", "set-location", "sl", "chdir",
         "push-location", "pop-location", "write-host", "write-output", "attrib"];

    private static readonly string[] PatchMarkers = ["*** Add File: ", "*** Update File: ", "*** Delete File: ", "*** Move to: "];

    private static readonly HashSet<string> WriteOnly = ["autorun", "shell_startup", "agent_config"];

    private static readonly HashSet<string> EnvSafe = ["example", "sample", "template", "dist", "defaults", "schema", "tpl", "tmpl"];

    private const string Roaming = "appdata/roaming/";
    private const string Local = "appdata/local/";

    // Relative to the profile, lowercase, '/'-separated. A directory entry matches the directory itself.
    private static readonly Dictionary<string, string> HomeDirs = new()
    {
        [".ssh"] = "ssh_key", [".gnupg"] = "gpg_key", [Roaming + "gnupg"] = "gpg_key", [".aws"] = "cloud_credentials",
        [".password-store"] = "password_store", [Roaming + "gcloud"] = "cloud_credentials", [".config/gcloud"] = "cloud_credentials",
        [".kube"] = "cloud_credentials",
    };

    private static readonly Dictionary<string, string> HomeFiles = new()
    {
        [".aws/credentials"] = "cloud_credentials", [".kube/config"] = "cloud_credentials",
        [".docker/config.json"] = "cloud_credentials", [".terraform.d/credentials.tfrc.json"] = "cloud_credentials",
        [Roaming + "terraform.d/credentials.tfrc.json"] = "cloud_credentials",
        [Roaming + "gcloud/credentials.db"] = "cloud_credentials", [Roaming + "gcloud/access_tokens.db"] = "cloud_credentials",
        [Roaming + "gcloud/application_default_credentials.json"] = "cloud_credentials",
        [".git-credentials"] = "token_file", [".config/git/credentials"] = "token_file", [".netrc"] = "token_file",
        ["_netrc"] = "token_file", [Roaming + "gh/hosts.yml"] = "token_file", [".config/gh/hosts.yml"] = "token_file",
        [".npmrc"] = "token_file", [".pypirc"] = "token_file", [".gem/credentials"] = "token_file",
        [".cargo/credentials"] = "token_file", [".cargo/credentials.toml"] = "token_file",
        [".codex/auth.json"] = "agent_credentials", [".claude/.credentials.json"] = "agent_credentials",
        [".gemini/oauth_creds.json"] = "agent_credentials", [".qwen/oauth_creds.json"] = "agent_credentials",
        [".local/share/opencode/auth.json"] = "agent_credentials",
        [".gnupg/secring.gpg"] = "gpg_key", [Roaming + "gnupg/secring.gpg"] = "gpg_key",
        [".zsh_history"] = "shell_history", [".bash_history"] = "shell_history", [".zhistory"] = "shell_history",
        [".sh_history"] = "shell_history", [".history"] = "shell_history", [".python_history"] = "shell_history",
        [".node_repl_history"] = "shell_history", [".psql_history"] = "shell_history", [".mysql_history"] = "shell_history",
        [".sqlite_history"] = "shell_history", [".irb_history"] = "shell_history",
        [Roaming + "microsoft/windows/powershell/psreadline/consolehost_history.txt"] = "shell_history",
        [".ssh/authorized_keys"] = "autorun",
        [".zshrc"] = "shell_startup", [".zshenv"] = "shell_startup", [".zprofile"] = "shell_startup",
        [".zlogin"] = "shell_startup", [".zlogout"] = "shell_startup", [".bashrc"] = "shell_startup",
        [".bash_profile"] = "shell_startup", [".bash_login"] = "shell_startup", [".profile"] = "shell_startup",
        [".config/fish/config.fish"] = "shell_startup",
        [".claude.json"] = "agent_config", [".codex/config.toml"] = "agent_config",
        [".config/opencode/opencode.json"] = "agent_config", [".config/opencode/opencode.jsonc"] = "agent_config",
        [Roaming + "claude/claude_desktop_config.json"] = "agent_config",
        [Roaming + "code/user/settings.json"] = "agent_config", [Roaming + "code/user/mcp.json"] = "agent_config",
        [Roaming + "cursor/user/settings.json"] = "agent_config",
    };

    private static readonly (string Prefix, string Category)[] HomePrefixes =
    [
        (".aws/sso/cache/", "cloud_credentials"), (Roaming + "gcloud/legacy_credentials/", "cloud_credentials"),
        (".config/gcloud/legacy_credentials/", "cloud_credentials"),
        (".azure/", "cloud_credentials"), (".config/github-copilot/", "agent_credentials"),
        (Local + "github-copilot/", "agent_credentials"),
        (".gnupg/private-keys-v1.d/", "gpg_key"), (Roaming + "gnupg/private-keys-v1.d/", "gpg_key"),
        (".password-store/", "password_store"),
        (Local + "1password/", "password_store"), (Roaming + "bitwarden/", "password_store"),
        // The Windows credential stores (Credential Manager blobs, DPAPI master keys, CNG/CAPI key stores):
        // the keychain's place on this platform.
        (Roaming + "microsoft/credentials/", "keychain"), (Local + "microsoft/credentials/", "keychain"),
        (Roaming + "microsoft/protect/", "keychain"), (Roaming + "microsoft/crypto/", "keychain"),
        (Local + "google/chrome/user data/", "browser_data"), (Local + "microsoft/edge/user data/", "browser_data"),
        (Local + "bravesoftware/brave-browser/user data/", "browser_data"), (Roaming + "mozilla/firefox/profiles/", "browser_data"),
        (Roaming + "microsoft/windows/start menu/programs/startup/", "autorun"),
        (".kannu/", "agent_config"),
    ];

    private static readonly (string Prefix, string Category)[] RootPrefixes =
    [
        ("c:/programdata/microsoft/windows/start menu/programs/startup/", "autorun"),
    ];

    private static readonly (string Suffix, string Category)[] AnySuffixes =
    [
        ("/.claude/settings.json", "agent_config"), ("/.claude/settings.local.json", "agent_config"),
        ("/.mcp.json", "agent_config"), ("/.cursor/mcp.json", "agent_config"), ("/.cursor/hooks.json", "agent_config"),
        ("/.gemini/settings.json", "agent_config"), ("/.qwen/settings.json", "agent_config"),
        ("/.vscode/settings.json", "agent_config"), ("/.vscode/mcp.json", "agent_config"), ("/.codex/config.toml", "agent_config"),
    ];

    // Private-key containers anywhere (macOS flags these only under ~/.ssh); KeePass databases anywhere.

    // A PowerShell profile: Documents\PowerShell\ or Documents\WindowsPowerShell\, any *profile.ps1,
    // also under a OneDrive-redirected Documents folder.
    private static readonly Regex PowerShellProfile = new(
        "^(?:onedrive[^/]*/)?documents/(?:windows)?powershell/[^/]*profile\\.ps1$", RegexOptions.CultureInvariant);

    private static readonly Regex DriveRoot = new("^[a-zA-Z]:", RegexOptions.CultureInvariant);

    internal static string NormalizeToken(string value) =>
        value.Trim().ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");

    // ---- Path normalisation -------------------------------------------------------------------

    /// <summary>Environment spellings of the profile folders, expanded against the hook's profile.</summary>
    private static (string Name, Func<string, string> Expand)[] Expansions { get; } =
    [
        ("~", h => h), ("$HOME", h => h), ("${HOME}", h => h), ("$env:USERPROFILE", h => h), ("%USERPROFILE%", h => h),
        ("$env:HOME", h => h), ("$USERPROFILE", h => h), ("%HOMEDRIVE%%HOMEPATH%", h => h),
        ("%APPDATA%", h => h + "/AppData/Roaming"), ("$env:APPDATA", h => h + "/AppData/Roaming"), ("$APPDATA", h => h + "/AppData/Roaming"),
        ("%LOCALAPPDATA%", h => h + "/AppData/Local"), ("$env:LOCALAPPDATA", h => h + "/AppData/Local"),
        ("$LOCALAPPDATA", h => h + "/AppData/Local"),
        ("%PROGRAMDATA%", _ => "C:/ProgramData"), ("$env:ProgramData", _ => "C:/ProgramData"),
        ("%ALLUSERSPROFILE%", _ => "C:/ProgramData"), ("$env:ALLUSERSPROFILE", _ => "C:/ProgramData"),
    ];

    /// <summary>
    /// <c>sp_norm</c> for Windows: the absolute, normalised path ('/'-separated, original case) a token
    /// names, or "" when it is not a path. <c>%APPDATA%</c> and friends are expanded from the profile
    /// with the default layout; Git Bash (<c>/c/…</c>) and WSL (<c>/mnt/c/…</c>) drive paths become <c>c:/…</c>.
    /// </summary>
    internal static string Normalize(string token, string cwd, string home)
    {
        var t = token.Trim().Replace("file://", "", StringComparison.OrdinalIgnoreCase);
        if (t.StartsWith('@')) t = t[1..];
        if (t.StartsWith('-') || (t.Contains('=') && !(t.StartsWith('/') || t.StartsWith('~') || t.StartsWith('.')
                                                     || t.StartsWith('\\') || t.StartsWith('$') || t.StartsWith('%')
                                                     || DriveRoot.IsMatch(t))))
        {
            var eq = t.IndexOf('=');
            if (eq < 0) return "";
            t = t[(eq + 1)..];
        }
        if (t.Length == 0 || t.Length > 1024
            || t.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        if (string.Equals(t, "$PROFILE", StringComparison.OrdinalIgnoreCase))
        {
            t = home + "/Documents/PowerShell/Microsoft.PowerShell_profile.ps1";
        }
        else
        {
            foreach (var (name, expand) in Expansions)
            {
                if (t.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                    && (t.Length == name.Length || t[name.Length] is '/' or '\\'))
                {
                    t = expand(home) + t[name.Length..];
                    break;
                }
            }
        }
        t = DrivePath(t.Replace('\\', '/'));
        if (!IsAbsolute(t) && cwd.Length > 0) t = DrivePath(cwd.Replace('\\', '/')) + "/" + t;
        return NormPath(t);
    }

    /// <summary>/c/x (Git Bash), /mnt/c/x (WSL) and /C:/x (a file URL) as c:/x.</summary>
    private static string DrivePath(string t)
    {
        if (t.Length >= 3 && t[0] == '/' && char.IsAsciiLetter(t[1]) && t[2] == ':') return t[1..];
        if (t.StartsWith("/mnt/", StringComparison.Ordinal) && t.Length >= 6 && char.IsAsciiLetter(t[5])
            && (t.Length == 6 || t[6] == '/'))
        {
            return t[5] + ":" + t[6..];
        }
        if (t.Length >= 2 && t[0] == '/' && char.IsAsciiLetter(t[1]) && (t.Length == 2 || t[2] == '/'))
        {
            return t[1] + ":" + t[2..];
        }
        return t;
    }

    private static bool IsAbsolute(string t) => t.StartsWith('/') || DriveRoot.IsMatch(t);

    /// <summary><c>os.path.normpath</c> with a drive or UNC root kept.</summary>
    private static string NormPath(string t)
    {
        string root;
        if (t.StartsWith("//", StringComparison.Ordinal) && !t.StartsWith("///", StringComparison.Ordinal)) root = "//";
        else if (t.StartsWith('/')) root = "/";
        else if (DriveRoot.IsMatch(t)) root = t.Length > 2 && t[2] == '/' ? t[..3] : t[..2];
        else root = "";
        var parts = new List<string>();
        foreach (var part in t[root.Length..].Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part != ".." || (root.Length == 0 && parts.Count == 0) || (parts.Count > 0 && parts[^1] == "..")) parts.Add(part);
            else if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
        }
        var joined = root + string.Join('/', parts);
        return joined.Length == 0 ? "." : joined;
    }

    // ---- Classification ----------------------------------------------------------------------

    /// <summary><c>sp_classify</c>: the category of a normalised path, or "".</summary>
    internal static string Classify(string path, string home)
    {
        var key = path.ToLowerInvariant();
        var homeKey = home.ToLowerInvariant();
        var rel = homeKey.Length > 0 && key.StartsWith(homeKey + "/", StringComparison.Ordinal) ? key[(homeKey.Length + 1)..] : "";
        var slash = key.LastIndexOf('/');
        var name = slash < 0 ? key : key[(slash + 1)..];
        if (rel.Length > 0)
        {
            if (rel.StartsWith(".ssh/", StringComparison.Ordinal) && !rel[5..].Contains('/')
                && ((name.StartsWith("id_", StringComparison.Ordinal) && !name.EndsWith(".pub", StringComparison.Ordinal))
                    || name.Contains('*') || name.EndsWith(".pem", StringComparison.Ordinal)
                    || name.EndsWith(".key", StringComparison.Ordinal) || name.EndsWith(".ppk", StringComparison.Ordinal)))
            {
                return "ssh_key";
            }
            if (HomeDirs.TryGetValue(rel, out var category) || HomeFiles.TryGetValue(rel, out category)) return category;
            if (PowerShellProfile.IsMatch(rel)) return "shell_startup";
            // A directory itself counts too: `copy x "%APPDATA%\…\Startup"` normalises to no slash.
            foreach (var (prefix, c) in HomePrefixes)
            {
                if ((rel + "/").StartsWith(prefix, StringComparison.Ordinal)) return c;
            }
        }
        foreach (var (prefix, c) in RootPrefixes)
        {
            if ((key + "/").StartsWith(prefix, StringComparison.Ordinal)) return c;
        }
        foreach (var (suffix, c) in AnySuffixes)
        {
            if (key.EndsWith(suffix, StringComparison.Ordinal) || key == suffix[1..]) return c;
        }
        if (key.Contains("/.git/hooks/", StringComparison.Ordinal) || key.StartsWith(".git/hooks/", StringComparison.Ordinal)) return "autorun";
        if (name.EndsWith(".kdbx", StringComparison.Ordinal)) return "password_store";
        if (name == ".env" || (name.StartsWith(".env.", StringComparison.Ordinal) && !EnvSafe.Contains(name[5..]))) return "env_file";
        return "";
    }

    // ---- Commands ----------------------------------------------------------------------------

    /// <summary>A flag, not an operand: <c>-x</c> (POSIX/PowerShell) or a short cmd switch such as <c>/y</c>, <c>/s</c>.</summary>
    private static bool IsFlag(string arg) =>
        arg.StartsWith('-') || (arg.Length is >= 2 and <= 4 && arg[0] == '/' && char.IsAsciiLetter(arg[1])
                                && arg.IndexOf('/', 1) < 0);

    private static bool IsRunKey(string arg) =>
        arg.Replace('/', '\\').Contains("\\currentversion\\run", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>sp_simple_command</c>: [(token, access, special)] for one simple command.</summary>
    internal static List<(string Token, string Access, string Special)> SimpleCommand(List<string> raw)
    {
        var out_ = new List<(string, string, string)>();
        var words = ShellWords.StripWrappers(raw);
        if (words.Count == 0) return out_;
        var shown = ShellWords.Basename(words[0]);
        if (shown.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) shown = shown[..^4];
        var name = ShellWords.CommandName(words[0]);
        var args = words.GetRange(1, words.Count - 1);
        var lower = args.ConvertAll(a => a.ToLowerInvariant());

        // Windows analogues of the macOS keychain and launchd checks.
        switch (name)
        {
            case "reg" when lower.Count > 1 && lower[0] == "add" && IsRunKey(args[1]):
                out_.Add(("reg add " + args[1], "write", "autorun"));
                break;
            case "new-itemproperty" or "set-itemproperty" when args.FirstOrDefault(IsRunKey) is { } runKey:
                out_.Add((shown + " " + runKey, "write", "autorun"));
                break;
            case "schtasks" when lower.Any(a => a is "/create" or "-create"):
                out_.Add(("schtasks /create", "write", "autorun"));
                break;
            case "register-scheduledtask" or "new-scheduledtask" or "new-service":
                out_.Add((shown, "write", "autorun"));
                break;
            case "sc" when lower.Count > 0 && lower[0] == "create":
                out_.Add(("sc create", "write", "autorun"));
                break;
            case "cmdkey" when lower.Any(a => a.StartsWith("/list", StringComparison.Ordinal)):
                // Lists which credentials exist, never a password: a lookup, graded below a read.
                out_.Add(("cmdkey /list", "read", "keychain_item"));
                break;
            case "vaultcmd" when lower.Any(a => a.StartsWith("/listcreds", StringComparison.Ordinal)):
                out_.Add(("vaultcmd /listcreds", "read", "keychain"));
                break;
            case "get-storedcredential":
                out_.Add((shown, "read", "keychain"));
                break;
            case "apply_patch":
                out_.AddRange(PatchPaths(args));
                break;
        }

        var plain = new List<string>();
        var redirect = "";
        foreach (var arg in args)
        {
            if (RedirectsOut.Contains(arg)) redirect = "write";
            else if (arg == "<") redirect = "read";
            else if (redirect.Length > 0)
            {
                out_.Add((arg, redirect, ""));
                redirect = "";
            }
            else if (!IsFlag(arg) || arg.Contains('=')) plain.Add(arg);
        }
        if (NameOnlyCommands.Contains(name)) return out_;
        if (WriteCommands.Contains(name))
        {
            out_.AddRange(plain.Select(a => (a, "write", "")));
        }
        else if (name == "robocopy" && plain.Count >= 2)
        {
            out_.Add((plain[0], "read", ""));
            out_.Add((plain[1], "write", ""));
        }
        else if (name == "mklink" && plain.Count >= 2)
        {
            out_.Add((plain[0], "write", ""));
            out_.Add((plain[1], "read", ""));
        }
        else if (CopyCommands.Contains(name) && plain.Count > 0)
        {
            out_.AddRange(plain.Take(plain.Count - 1).Select(a => (a, "read", "")));
            out_.Add((plain[^1], "write", ""));
        }
        else if (InPlaceCommands.Contains(name)
                 && args.Any(a => a.StartsWith("-i", StringComparison.Ordinal) || a.StartsWith("-pi", StringComparison.Ordinal)
                                  || a.StartsWith("--in-place", StringComparison.Ordinal)))
        {
            out_.AddRange(plain.Select(a => (a, "write", "")));
        }
        else
        {
            out_.AddRange(plain.Select(a => (a, "read", "")));
        }
        return out_;
    }

    /// <summary><c>sp_command_paths</c>: a command string, or an argv list (<c>["bash","-lc","…"]</c>).</summary>
    internal static List<(string Token, string Access, string Special)> CommandPaths(JsonElement command)
    {
        var segments = new List<ShellWords.Segment>();
        if (command.ValueKind == JsonValueKind.Array)
        {
            var parts = command.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToList();
            ShellWords.AddSegment(segments, parts, 0);
        }
        else if (command.ValueKind == JsonValueKind.String)
        {
            segments = ShellWords.Segments(command.GetString()!);
        }
        var out_ = new List<(string, string, string)>();
        foreach (var segment in segments)
        {
            // The wrapper's own words are the inline script, not a path; its commands follow it.
            if (segment.IsShellWrapper) continue;
            out_.AddRange(SimpleCommand(segment.Words));
            if (out_.Count > 200) break;
        }
        return out_;
    }

    private static List<(string, string, string)> PatchPaths(IEnumerable<string> texts)
    {
        var out_ = new List<(string, string, string)>();
        foreach (var text in texts)
        {
            if (!text.Contains("*** ", StringComparison.Ordinal)) continue;
            foreach (var line in text.Split('\n').Take(5000))
            {
                var clean = line.TrimEnd('\r');
                foreach (var marker in PatchMarkers)
                {
                    if (clean.StartsWith(marker, StringComparison.Ordinal)) out_.Add((clean[marker.Length..].Trim(), "write", ""));
                }
            }
        }
        return out_;
    }

    /// <summary><c>early_cwd</c>: the payload's cwd, else its first workspace root.</summary>
    internal static string EarlyCwd(HookPayload payload)
    {
        var root = "";
        foreach (var key in new[] { "workspace_roots", "workspacePaths", "workspace_paths" })
        {
            var roots = payload.Get(key);
            if (roots.ValueKind == JsonValueKind.Array && roots.GetArrayLength() > 0)
            {
                root = roots[0].ValueKind == JsonValueKind.String ? roots[0].GetString()! : roots[0].ToString();
                break;
            }
        }
        var cwd = payload.Pick("cwd");
        if (cwd.Length == 0) cwd = root.Trim();
        cwd = cwd.Replace("file://", "", StringComparison.OrdinalIgnoreCase);
        if (cwd.Contains("%3a", StringComparison.OrdinalIgnoreCase))
        {
            try { cwd = Uri.UnescapeDataString(cwd); } catch (UriFormatException) { }
        }
        return cwd.TrimEnd('/', '\\');
    }

    /// <summary><c>scan_paths</c>: distinct (category, access, shown path), at most the cap.</summary>
    internal static List<Hit> Scan(string tool, JsonElement toolInput, string cwd, string home)
    {
        var hits = new List<Hit>();
        var compact = NormalizeToken(tool);
        if (ListingTools.Contains(compact)) return hits;
        if (toolInput.ValueKind == JsonValueKind.String && toolInput.GetString()!.TrimStart().StartsWith('{')
            && HiddenText.TryParseContainer(toolInput.GetString()!) is { ValueKind: JsonValueKind.Object } parsed)
        {
            toolInput = parsed;
        }
        var candidates = new List<(string Token, string Access, string Special)>();
        if (toolInput.ValueKind == JsonValueKind.Object)
        {
            var command = toolInput.TryGetProperty("command", out var c) ? c
                : toolInput.TryGetProperty("cmd", out var cmd) ? cmd : default;
            var hasCommand = command.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
            if (hasCommand && (ShellTools.Contains(compact) || !PathKeys.Any(k => toolInput.TryGetProperty(k, out _))))
            {
                candidates.AddRange(CommandPaths(command));
            }
            else
            {
                var access = WriteWords.Any(w => compact.Contains(w, StringComparison.Ordinal)) ? "write" : "read";
                foreach (var key in PathKeys)
                {
                    if (toolInput.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                    {
                        candidates.Add((s, access, ""));
                    }
                }
                if (toolInput.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Array)
                {
                    candidates.AddRange(paths.EnumerateArray().Take(50)
                        .Where(p => p.ValueKind == JsonValueKind.String).Select(p => (p.GetString()!, "read", "")));
                }
            }
            if (compact.Contains("patch", StringComparison.Ordinal)) candidates.AddRange(PatchPaths(Secrets.Strings(toolInput, 200)));
        }
        else if (toolInput.ValueKind is JsonValueKind.String or JsonValueKind.Array && ShellTools.Contains(compact))
        {
            candidates.AddRange(CommandPaths(toolInput));
        }
        else if (toolInput.ValueKind == JsonValueKind.String && compact.Contains("patch", StringComparison.Ordinal))
        {
            candidates.AddRange(PatchPaths([toolInput.GetString()!]));
        }

        var homeNorm = home.Length > 0 ? Normalize(home, "", "") : "";
        foreach (var (token, access, special) in candidates.Take(400))
        {
            Hit hit;
            if (special.Length > 0)
            {
                hit = new Hit(special, access, SecurityText.PrintableAscii(token, 160));
            }
            else
            {
                var path = Normalize(token, cwd, homeNorm);
                var category = path.Length > 0 ? Classify(path, homeNorm) : "";
                if (category.Length == 0 || (WriteOnly.Contains(category) && access != "write")
                    || (category == "env_file" && access != "read"))
                {
                    continue;
                }
                var underHome = homeNorm.Length > 0 && path.StartsWith(homeNorm + "/", StringComparison.OrdinalIgnoreCase);
                var display = (underHome ? "~/" + path[(homeNorm.Length + 1)..] : path).Replace('/', '\\');
                hit = new Hit(category, access, SecurityText.PrintableAscii(display, 160));
            }
            if (hit.Path.Length > 0 && !hits.Contains(hit))
            {
                hits.Add(hit);
                if (hits.Count >= MaxEntries) break;
            }
        }
        return hits;
    }

    /// <summary>One sighting per (category, access, path); "failed" stays only while every attempt failed.</summary>
    internal static void Record(List<SensitivePathEntry> entries, Hit hit, long nowMs, string tool, string toolUseId, bool failed)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (entry.Category != hit.Category || entry.Access != hit.Access || entry.Path != hit.Path) continue;
            if (!(toolUseId.Length > 0 && entry.ToolUseId == toolUseId)) entry.Events = Math.Min(entry.Events + 1, 999);
            entry.Failed = entry.Failed && failed;
            entry.LastTs = nowMs;
            if (toolUseId.Length > 0) entry.ToolUseId = toolUseId;
            return;
        }
        entries.Add(new SensitivePathEntry
        {
            Category = hit.Category,
            Access = hit.Access,
            Path = hit.Path,
            Tool = tool,
            Failed = failed,
            Events = 1,
            FirstTs = nowMs,
            LastTs = nowMs,
            ToolUseId = toolUseId,
        });
        if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
    }
}
