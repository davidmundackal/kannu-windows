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

namespace Kannu.Core;

/// <summary>
/// The hook body's use of the four checks, as the macOS script sequences them: scanned before the
/// directory lock (the only non-trivial CPU work), then merged into the record being written, carried
/// from the existing file like <c>unattended</c>. A check that throws finds nothing; it never costs the
/// agent its stdout line.
/// </summary>
internal sealed class HookSecurityRun
{
    private bool _hiddenOff, _secretsOff, _pathsOff;
    private HiddenText.Hit? _hiddenHit;
    private List<Secrets.Hit> _secretHits = [];
    private List<SensitivePaths.Hit> _pathHits = [];
    private AgentPolicyCheck.Hit? _policyHit;
    private string _hookEvent = "";
    private string _statusDirectory = "";

    /// <summary>The deny reason when the call is refused, else null.</summary>
    public string? PolicyDeny { get; private set; }

    /// <summary>(context for the model, line for the user) for a new hidden-text sighting the user asked the agent be told of.</summary>
    public (string Agent, string User) Notes { get; private set; } = ("", "");

    public static HookSecurityRun Scan(HookPayload payload, string provider, string hookEvent, string statusDirectory, string home)
    {
        var run = new HookSecurityRun { _hookEvent = hookEvent, _statusDirectory = statusDirectory };

        run._hiddenOff = HookSecurity.MarkerExists(statusDirectory, HookSecurity.HiddenTextOffMarker);
        if (!run._hiddenOff && !HiddenText.SkipEvents.Contains(hookEvent))
        {
            try { run._hiddenHit = HiddenText.Scan(payload.Root, hookEvent); }
            catch (Exception) { run._hiddenHit = null; }
        }

        run._secretsOff = HookSecurity.MarkerExists(statusDirectory, HookSecurity.SecretsOffMarker);
        run._pathsOff = HookSecurity.MarkerExists(statusDirectory, HookSecurity.SensitivePathsOffMarker);
        if (!run._secretsOff && (Secrets.PromptEvents.Contains(hookEvent) || Secrets.ToolEvents.Contains(hookEvent)))
        {
            try { run._secretHits = Secrets.Scan(Secrets.Texts(payload, hookEvent)); }
            catch (Exception) { run._secretHits = []; }
        }
        if (!run._pathsOff && SensitivePaths.Events.Contains(hookEvent))
        {
            try { run._pathHits = SensitivePaths.Scan(payload.Tool, payload.ToolInput, SensitivePaths.EarlyCwd(payload), home); }
            catch (Exception) { run._pathHits = []; }
        }

        // A hit is always recorded; the deny needs the enforce marker and a host whose contract has one.
        if (Secrets.ToolEvents.Contains(hookEvent))
        {
            try
            {
                if (home.Length > 0 && HookSecurity.LoadPolicy(HookSecurity.PolicyPath(home)) is { Rules.Count: > 0 } policy)
                {
                    run._policyHit = AgentPolicyCheck.Match(policy, payload.Tool, AgentPolicyCheck.Commands(payload, hookEvent));
                    if (run._policyHit is { } hit && AgentPolicyCheck.CanDeny(provider, hookEvent)
                        && HookSecurity.MarkerExists(statusDirectory, HookSecurity.PolicyEnforceMarker))
                    {
                        run.PolicyDeny = HookSecurity.PolicyDenial(hit.Matched, hit.Reason);
                    }
                }
            }
            catch (Exception)
            {
                run._policyHit = null;
                run.PolicyDeny = null;
            }
        }
        return run;
    }

    /// <summary>Sets the four lists on <paramref name="target"/>: carried from <paramref name="existing"/>, plus this event's sightings.</summary>
    public void Apply(StatusRecord? existing, StatusRecord target, HookPayload payload, long nowMs)
    {
        var tool = SecurityText.Token(payload.Tool);
        var call = SecurityText.Token(payload.Pick("tool_use_id", "toolUseId"));

        var hidden = _hiddenOff ? [] : Copy(existing?.HiddenText, e => e.Clone());
        if (!_hiddenOff && _hiddenHit is { } hiddenHit)
        {
            try
            {
                if (HiddenText.Record(hidden, hiddenHit, nowMs, tool, call)
                    && HookSecurity.MarkerExists(_statusDirectory, HookSecurity.HiddenTextWarnMarker))
                {
                    Notes = HiddenText.Notes(hiddenHit, tool);
                }
            }
            catch (Exception)
            {
                // A sighting that cannot be recorded is not worth the agent's stdout line.
            }
        }

        var secrets = _secretsOff ? [] : Copy(existing?.Secrets, e => e.Clone());
        if (!_secretsOff)
        {
            foreach (var hit in _secretHits) Secrets.Record(secrets, hit, nowMs, tool, call);
        }

        var policy = Copy(existing?.Policy, e => e.Clone());
        if (_policyHit is { } policyHit) AgentPolicyCheck.Record(policy, policyHit, PolicyDeny is not null, nowMs, tool, call);

        var paths = _pathsOff ? [] : Copy(existing?.SensitivePaths, e => e.Clone());
        if (!_pathsOff)
        {
            var failed = SensitivePaths.FailureEvents.Contains(_hookEvent);
            foreach (var hit in _pathHits) SensitivePaths.Record(paths, hit, nowMs, tool, call, failed);
        }

        target.HiddenText = hidden.Count > 0 ? hidden : null;
        target.Secrets = secrets.Count > 0 ? secrets : null;
        target.Policy = policy.Count > 0 ? policy : null;
        target.SensitivePaths = paths.Count > 0 ? paths : null;
    }

    private static List<T> Copy<T>(List<T>? source, Func<T, T> clone) => source is null ? [] : source.ConvertAll(x => clone(x));
}
