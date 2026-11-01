using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinIDM.Browser;

/// <summary>Decision kind for one network request.</summary>
public enum WebRequestActionKind
{
    Allow,
    Cancel,
    Redirect,
}

/// <summary>GM_webRequest decision for one URL. <see cref="ScriptId"/> is the script that set the rule.</summary>
public sealed record WebRequestDecision(WebRequestActionKind Kind, string? RedirectUrl, string? ScriptId)
{
    public static WebRequestDecision Allowed { get; } = new(WebRequestActionKind.Allow, null, null);
}

/// <summary>
/// GM_webRequest: scripts declare block (cancel) or redirect rules for matching URLs.
/// Rules are active only if the script is enabled, declares @grant GM_webRequest, and the user
/// approves it. URL selection uses @include syntax ('*' glob or /regex/). Rules are
/// kept in memory and reset each time the script runs.
/// The host makes no requests; the app calls <see cref="EvaluateWebRequest"/> and acts on the decision.
/// </summary>
public sealed partial class UserScriptHost
{
    public const int MaxWebRulesPerScript = 200;
    private const int MaxSelectorLength = 2048;

    private readonly Dictionary<string, List<WebRule>> _webRules = new(StringComparer.Ordinal);

    private sealed record WebRule(string Selector, System.Text.RegularExpressions.Regex Pattern, string? RedirectUrl);

    /// <summary>
    /// Set GM_webRequest rules for a script. The whole set is rejected if any single rule is invalid.
    /// </summary>
    public void SetWebRules(string id, JsonElement rules)
    {
        if (rules.ValueKind != JsonValueKind.Array) throw new ArgumentException("Rules must be an array.");
        if (rules.GetArrayLength() > MaxWebRulesPerScript) throw new ArgumentException($"Too many rules (max {MaxWebRulesPerScript}).");

        var parsed = new List<WebRule>(rules.GetArrayLength());
        foreach (var item in rules.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each rule must be an object.");
            if (!item.TryGetProperty("selector", out var selEl) || selEl.ValueKind != JsonValueKind.String)
                throw new ArgumentException("A rule requires a string 'selector'.");
            string selector = selEl.GetString() ?? "";
            if (selector.Length == 0 || selector.Length > MaxSelectorLength) throw new ArgumentException("Invalid selector.");

            var pattern = UrlPatternMatcher.CompileInclude(selector)
                          ?? throw new ArgumentException("Invalid selector regex.");

            if (!item.TryGetProperty("action", out var action)) throw new ArgumentException("A rule requires 'action'.");
            string? redirect = null;
            if (action.ValueKind == JsonValueKind.String && string.Equals(action.GetString(), "cancel", StringComparison.Ordinal))
            {
                redirect = null;
            }
            else if (action.ValueKind == JsonValueKind.Object && action.TryGetProperty("redirect", out var rEl) && rEl.ValueKind == JsonValueKind.String)
            {
                string target = rEl.GetString() ?? "";
                if (target.Length > MaxSelectorLength || !HostUtil.TryGetHttpUri(target, out var uri))
                    throw new ArgumentException("Redirect target must be an http(s) URL.");
                redirect = uri.AbsoluteUri;
            }
            else
            {
                throw new ArgumentException("Unsupported action. Use \"cancel\" or {\"redirect\": \"url\"}.");
            }

            parsed.Add(new WebRule(selector, pattern, redirect));
        }

        lock (_gate)
        {
            _ = FindById(id) ?? throw new KeyNotFoundException("Script not found.");
            if (parsed.Count == 0) _webRules.Remove(id);
            else _webRules[id] = parsed;
        }
    }

    /// <summary>Rule values currently active for a script (for display and tests). Empty if none.</summary>
    public IReadOnlyList<string> ActiveWebRules(string id)
    {
        lock (_gate)
        {
            return _webRules.TryGetValue(id, out var list) ? list.Select(r => r.Selector).ToList() : Array.Empty<string>();
        }
    }

    /// <summary>
    /// Decision value for a request URL. Scripts are checked in installation order, and rules in
    /// the order they appear in the script. The first matching decision is returned.
    /// </summary>
    public WebRequestDecision EvaluateWebRequest(string requestUrl)
    {
        if (!HostUtil.TryGetHttpUri(requestUrl, out var reqUri)) return WebRequestDecision.Allowed;
        string url = reqUri.AbsoluteUri;

        lock (_gate)
        {
            foreach (var rec in _scripts)
            {
                if (!rec.Enabled) continue;
                if (!_webRules.TryGetValue(rec.Id, out var rules) || rules.Count == 0) continue;
                if (!_metas.TryGetValue(rec.Id, out var meta)) continue;
                if (!meta.Grants.Contains(UserScriptPermissions.WebRequest, StringComparer.Ordinal)) continue;
                if (!IsApproved(rec.Id, UserScriptPermissions.WebRequest)) continue;

                foreach (var rule in rules)
                {
                    if (!RuleMatches(rule.Pattern, url)) continue;
                    if (rule.RedirectUrl is null)
                        return new WebRequestDecision(WebRequestActionKind.Cancel, null, rec.Id);
                    // Loop guard: a redirect to the same URL is not applied.
                    if (string.Equals(rule.RedirectUrl, url, StringComparison.Ordinal)) continue;
                    return new WebRequestDecision(WebRequestActionKind.Redirect, rule.RedirectUrl, rec.Id);
                }
            }
        }
        return WebRequestDecision.Allowed;
    }

    private static bool RuleMatches(System.Text.RegularExpressions.Regex pattern, string url)
    {
        try { return pattern.IsMatch(url); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private void ClearWebRules(string id)
    {
        lock (_gate) _webRules.Remove(id);
    }
}
