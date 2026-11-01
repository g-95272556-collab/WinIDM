using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using WinIDM.App.Services;
using WinIDM.App.ViewModels;

namespace WinIDM.App.Views;

/// <summary>
/// Password integration in the browser: autofill (HTTPS only, only after the user picks an account)
/// and the "Save password?" offer after a form is submitted.
/// </summary>
public partial class BrowserWindow
{
    /// <summary>
    /// Helper script. Runs only on HTTPS pages and in the top frame. The script just sends messages
    /// to the host and fills fields when the host sends a "fill" message (the result of the user's choice).
    /// The origin is always taken from the tab URL by the host, never from a page message.
    /// </summary>
    private const string PasswordAssistScript = """
(function () {
  if (window.top !== window || location.protocol !== 'https:') return;
  var wv = window.chrome && window.chrome.webview;
  if (!wv) return;
  function isPw(el) { return !!el && el.tagName === 'INPUT' && (el.type || '').toLowerCase() === 'password'; }
  function userFieldFor(pw) {
    var scope = pw.form || document;
    var inputs = scope.querySelectorAll('input');
    var found = null;
    for (var i = 0; i < inputs.length; i++) {
      var el = inputs[i];
      if (el === pw) break;
      var t = (el.type || 'text').toLowerCase();
      if ((t === 'text' || t === 'email' || t === 'tel') && !el.disabled) found = el;
    }
    return found;
  }
  function report(root) {
    var pws = (root || document).querySelectorAll('input[type=password]');
    for (var i = 0; i < pws.length; i++) {
      if (pws[i].value) {
        var u = userFieldFor(pws[i]);
        wv.postMessage({ k: 'pw-submit', user: u ? u.value : '', pass: pws[i].value });
        return;
      }
    }
  }
  function setVal(el, v) {
    var d = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
    d.set.call(el, v);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  }
  document.addEventListener('focusin', function (e) { if (isPw(e.target)) wv.postMessage({ k: 'pw-focus' }); }, true);
  document.addEventListener('submit', function (e) { report(e.target); }, true);
  document.addEventListener('click', function (e) {
    var b = e.target && e.target.closest && e.target.closest('button,input[type=submit]');
    if (!b) return;
    var form = b.form || (b.closest && b.closest('form'));
    if (form) report(form);
  }, true);
  wv.addEventListener('message', function (e) {
    var m = e.data;
    if (!m || m.k !== 'fill') return;
    var pw = isPw(document.activeElement) ? document.activeElement : document.querySelector('input[type=password]');
    if (!pw) return;
    var u = userFieldFor(pw);
    if (u) setVal(u, m.user);
    setVal(pw, m.pass);
  });
})();
""";

    private (string Origin, string User, string Pass)? _pendingSave;
    private DateTime _lastPickerAt = DateTime.MinValue;

    /// <summary>Wire the script and message handlers to the tab's WebView2 core (not installed in incognito mode).</summary>
    private async Task WirePasswordAssistAsync(BrowserTabViewModel tab, CoreWebView2 core)
    {
        if (_incognito) return;
        core.WebMessageReceived += (_, e) => OnPasswordWebMessage(tab, core, e);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(PasswordAssistScript);
    }

    private void OnPasswordWebMessage(BrowserTabViewModel tab, CoreWebView2 core, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Accept only from the top frame of this tab's current page.
        if (!SameOrigin(e.Source, core.Source)) return;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(e.WebMessageAsJson); }
        catch (JsonException) { return; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            switch (StringField(root, "k"))
            {
                case "pw-focus":
                    if (DateTime.UtcNow - _lastPickerAt < TimeSpan.FromSeconds(1.5)) return;
                    _lastPickerAt = DateTime.UtcNow;
                    _ = ShowAccountPickerAsync(tab, core);
                    break;
                case "pw-submit":
                    _ = OfferSaveAsync(core.Source, StringField(root, "user") ?? "", StringField(root, "pass") ?? "");
                    break;
            }
        }
    }

    private static string? StringField(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Account picker: the account list for the current page origin. Nothing is filled without a user click.</summary>
    private async Task ShowAccountPickerAsync(BrowserTabViewModel tab, CoreWebView2 core)
    {
        if (tab.View is null || !TryHttpsOrigin(core.Source, out var origin)) return;
        if (!_c.Vault.IsInitialized) return;

        var menu = new ContextMenu
        {
            PlacementTarget = tab.View,
            Placement = PlacementMode.Relative,
            HorizontalOffset = 16,
            VerticalOffset = 16,
        };

        if (!_c.Vault.IsUnlocked)
        {
            var unlock = new MenuItem { Header = "Unlock the password manager to fill…" };
            unlock.Click += async (_, _) =>
            {
                if (await VaultPrompts.UnlockAsync(_c, this)) await ShowAccountPickerAsync(tab, core);
            };
            menu.Items.Add(unlock);
        }
        else
        {
            var accounts = (await _c.Vault.ListAsync())
                .Where(a => string.Equals(a.Origin, origin, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (accounts.Count == 0) return;

            foreach (var account in accounts)
            {
                var username = account.Username;
                var item = new MenuItem { Header = $"Fill: {username}" };
                item.Click += async (_, _) => await FillAsync(core, origin, username);
                menu.Items.Add(item);
            }
        }

        menu.IsOpen = true;
    }

    private async Task FillAsync(CoreWebView2 core, string origin, string username)
    {
        // Re-check the origin: the page may have navigated while the menu was open.
        if (!TryHttpsOrigin(core.Source, out var current) || !string.Equals(current, origin, StringComparison.OrdinalIgnoreCase)) return;
        if (!_c.Vault.IsUnlocked) return;

        var password = await _c.Vault.GetPasswordAsync(origin, username);
        if (password is null) return;
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { k = "fill", user = username, pass = password }));
        StatusText.Text = "Fields filled from the password manager.";
    }

    /// <summary>Offer to save after a form is submitted. No offer if the same credentials are already stored.</summary>
    private async Task OfferSaveAsync(string? pageUrl, string user, string pass)
    {
        if (pass.Length == 0 || user.Length == 0) return;
        if (!TryHttpsOrigin(pageUrl, out var origin)) return;

        if (_c.Vault.IsUnlocked)
        {
            var existing = await _c.Vault.GetPasswordAsync(origin, user);
            if (existing == pass) return;
        }

        _pendingSave = (origin, user, pass);
        SavePromptText.Text = $"Save the password for {user} on {new Uri(origin).Host}?";
        SavePromptBar.Visibility = Visibility.Visible;
    }

    private async void SavePrompt_Save_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingSave is not { } pending) return;
        _pendingSave = null;
        SavePromptBar.Visibility = Visibility.Collapsed;

        if (!_c.Vault.IsInitialized && !await VaultPrompts.SetupAsync(_c, this)) return;
        if (!_c.Vault.IsUnlocked && !await VaultPrompts.UnlockAsync(_c, this)) return;

        try
        {
            await _c.Vault.AddAsync(pending.Origin, pending.User, pending.Pass);
            StatusText.Text = "Password saved.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void SavePrompt_Dismiss_Click(object sender, RoutedEventArgs e)
    {
        _pendingSave = null;
        SavePromptBar.Visibility = Visibility.Collapsed;
    }

    private static bool TryHttpsOrigin(string? url, out string origin)
    {
        origin = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private static bool SameOrigin(string? a, string? b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var ua) && Uri.TryCreate(b, UriKind.Absolute, out var ub)
        && ua.Scheme == ub.Scheme && string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase)
        && ua.Port == ub.Port;
}
