using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinIDM.App.Services;

/// <summary>
/// Browser cosmetic filtering: selector validation, CSS building, and the element picker script.
/// Selectors are only accepted when they contain safe characters only. CSS is sent as JSON, never by string concatenation.
/// </summary>
public static class BrowserCosmetics
{
    public const int MaxSelectorLength = 300;

    private static readonly Regex SafeSelector = new(@"^[A-Za-z0-9_\-\.#\s>:\(\),\*\[\]=""'+~]+$", RegexOptions.Compiled);

    /// <summary>CSS safe for a hide rule: no curly braces, semicolons, or comments.</summary>
    public static bool IsSafeSelector(string? selector) =>
        !string.IsNullOrWhiteSpace(selector)
        && selector.Length <= MaxSelectorLength
        && SafeSelector.IsMatch(selector)
        && !selector.Contains("/*", StringComparison.Ordinal);

    /// <summary>Combine filter CSS (from the engine) with manual rules for one host.</summary>
    public static string BuildStyleSheet(string engineCss, IEnumerable<string> manualSelectors)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(engineCss)) parts.Add(engineCss);
        foreach (var s in manualSelectors.Where(IsSafeSelector)) parts.Add(s + "{display:none!important}");
        return string.Join("\n", parts);
    }

    /// <summary>
    /// Document-start script: install the style as soon as the root element exists. Runs before the page renders.
    /// </summary>
    public static string DocumentStyleScript(string css) =>
        "(function(){var css=" + JsonSerializer.Serialize(css) + ";" +
        "function add(){var s=document.createElement('style');s.textContent=css;(document.head||document.documentElement).appendChild(s);}" +
        "if(document.documentElement){add();}else{var mo=new MutationObserver(function(_,o){if(document.documentElement){o.disconnect();add();}});mo.observe(document,{childList:true});}" +
        "})();";

    /// <summary>Inject a single hide rule into the page currently displayed.</summary>
    public static string ImmediateStyleScript(string selector) =>
        "(function(){var s=document.createElement('style');s.textContent=" +
        JsonSerializer.Serialize(selector + "{display:none!important}") +
        ";(document.head||document.documentElement).appendChild(s);})();";

    /// <summary>
    /// Page-side element picker. Hover highlights the element; a click sends the generated selector through
    /// <c>chrome.webview</c> with k=hide-pick; Esc cancels (k=hide-pick-cancel). No network calls are made.
    /// </summary>
    public const string PickerScript = """
(function () {
  if (window.top !== window) return;
  if (window.__winidmPicker) { window.__winidmPicker.stop(); }
  var wv = window.chrome && window.chrome.webview;
  if (!wv) return;
  var box = document.createElement('div');
  box.style.cssText = 'position:fixed;pointer-events:none;border:2px solid #E5484D;background:rgba(229,72,77,.15);z-index:2147483647;display:none;box-sizing:border-box';
  (document.documentElement || document.body).appendChild(box);
  var last = null;
  function safeClass(c) { return /^[A-Za-z][\w-]*$/.test(c); }
  function build(el) {
    if (el.id && /^[A-Za-z][\w-]*$/.test(el.id)) {
      var byId = '#' + el.id;
      if (document.querySelectorAll(byId).length === 1) return byId;
    }
    var parts = [];
    var cur = el;
    while (cur && cur.nodeType === 1 && cur !== document.documentElement && parts.length < 6) {
      var part = cur.tagName.toLowerCase();
      var classes = (cur.getAttribute('class') || '').split(/\s+/).filter(safeClass).slice(0, 2);
      if (classes.length) part += '.' + classes.join('.');
      var parent = cur.parentElement;
      if (parent) {
        var tag = cur.tagName;
        var same = Array.prototype.filter.call(parent.children, function (c) { return c.tagName === tag; });
        if (same.length > 1) part += ':nth-of-type(' + (same.indexOf(cur) + 1) + ')';
      }
      parts.unshift(part);
      var candidate = parts.join(' > ');
      if (document.querySelectorAll(candidate).length === 1) return candidate;
      cur = parent;
    }
    return parts.join(' > ');
  }
  function onMove(e) {
    var t = e.target;
    if (!t || t === box || t.nodeType !== 1) return;
    var r = t.getBoundingClientRect();
    box.style.display = 'block';
    box.style.left = r.left + 'px'; box.style.top = r.top + 'px';
    box.style.width = r.width + 'px'; box.style.height = r.height + 'px';
    last = t;
  }
  function stop() {
    document.removeEventListener('mousemove', onMove, true);
    document.removeEventListener('click', onClick, true);
    document.removeEventListener('keydown', onKey, true);
    box.remove();
    window.__winidmPicker = null;
  }
  function onClick(e) {
    e.preventDefault(); e.stopPropagation();
    var target = last;
    stop();
    if (target) wv.postMessage({ k: 'hide-pick', selector: build(target) });
  }
  function onKey(e) {
    if (e.key === 'Escape') { stop(); wv.postMessage({ k: 'hide-pick-cancel' }); }
  }
  window.__winidmPicker = { stop: stop };
  document.addEventListener('mousemove', onMove, true);
  document.addEventListener('click', onClick, true);
  document.addEventListener('keydown', onKey, true);
})();
""";
}
