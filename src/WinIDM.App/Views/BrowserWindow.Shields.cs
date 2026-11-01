using Microsoft.Web.WebView2.Core;
using WinIDM.App.Services;
using WinIDM.App.ViewModels;

namespace WinIDM.App.Views;

/// <summary>
/// Brave-style privacy shields on top of WebView2: Global Privacy Control.
/// The request-header half of each shield lives in <see cref="ApplyRequestPrivacy"/>.
/// </summary>
public partial class BrowserWindow
{
    // GPC (https://privacycg.github.io/gpc-spec/): the header alone is not enough, pages read the navigator property.
    private const string GpcScript =
        "(function(){try{Object.defineProperty(Navigator.prototype,'globalPrivacyControl'," +
        "{get:function(){return true;},configurable:true,enumerable:true});}catch(e){}})();";

    private async Task WireShieldsAsync(BrowserTabViewModel tab, CoreWebView2 core)
    {
        try
        {
            if (_c.Settings.Bool(Keys.GlobalPrivacyControl))
                await core.AddScriptToExecuteOnDocumentCreatedAsync(GpcScript);
        }
        catch (Exception ex)
        {
            AppLog.Error("Browser shields", ex);
        }
    }
}
