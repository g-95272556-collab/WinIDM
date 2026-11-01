using System.Text.RegularExpressions;

namespace WinIDM.Engine;

/// <summary>
/// IDM+ special sources: share pages of file hosts are turned into the link that serves the file itself.
/// <list type="bullet">
/// <item>Google Drive <c>/file/d/ID/view</c>, <c>/open?id=ID</c>, <c>/uc?id=ID</c> → <c>drive.usercontent.google.com/download?id=ID&amp;export=download&amp;confirm=t</c>
/// (confirm=t skips the "cannot scan for viruses" page of large files)</item>
/// <item>Dropbox <c>?dl=0</c> → <c>?dl=1</c></item>
/// <item>OneDrive <c>onedrive.live.com/redir</c> → <c>/download</c></item>
/// </list>
/// Only public links work; private Drive files still need the account cookies a browser download carries.
/// </summary>
public static partial class SpecialSources
{
    [GeneratedRegex(@"^/file/d/([A-Za-z0-9_-]{10,})", RegexOptions.CultureInvariant)]
    private static partial Regex DriveFilePath();

    [GeneratedRegex(@"(?:^|&)id=([A-Za-z0-9_-]{10,})", RegexOptions.CultureInvariant)]
    private static partial Regex IdParameter();

    public static Uri Rewrite(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();

        if (host is "drive.google.com" or "docs.google.com")
        {
            var id = DriveFilePath().Match(uri.AbsolutePath) is { Success: true } m ? m.Groups[1].Value
                : uri.AbsolutePath is "/open" or "/uc" && IdParameter().Match(uri.Query.TrimStart('?')) is { Success: true } q ? q.Groups[1].Value
                : null;
            if (id is not null) return new Uri($"https://drive.usercontent.google.com/download?id={id}&export=download&confirm=t");
        }

        if (host is "www.dropbox.com" or "dropbox.com" && uri.Query.Contains("dl=0", StringComparison.Ordinal))
            return new Uri(uri.AbsoluteUri.Replace("dl=0", "dl=1", StringComparison.Ordinal));

        if (host == "onedrive.live.com" && uri.AbsolutePath.Equals("/redir", StringComparison.OrdinalIgnoreCase))
            return new UriBuilder(uri) { Path = "/download" }.Uri;

        return uri;
    }
}
