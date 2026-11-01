using WinIDM.Engine;

namespace WinIDM.Engine.Tests;

/// <summary>Share pages of file hosts become direct file links (IDM+ special sources).</summary>
public sealed class SpecialSourcesTests
{
    private const string DriveDirect = "https://drive.usercontent.google.com/download?id=1AbCdEfGhIjKlMnOp&export=download&confirm=t";

    [Theory]
    [InlineData("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOp/view?usp=sharing", DriveDirect)]
    [InlineData("https://drive.google.com/open?id=1AbCdEfGhIjKlMnOp", DriveDirect)]
    [InlineData("https://drive.google.com/uc?export=download&id=1AbCdEfGhIjKlMnOp", DriveDirect)]
    [InlineData("https://www.dropbox.com/s/abc/file.zip?dl=0", "https://www.dropbox.com/s/abc/file.zip?dl=1")]
    [InlineData("https://onedrive.live.com/redir?resid=ABC!123", "https://onedrive.live.com/download?resid=ABC!123")]
    [InlineData("https://example.com/file/d/1AbCdEfGhIjKlMnOp/view", "https://example.com/file/d/1AbCdEfGhIjKlMnOp/view")]
    [InlineData("https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOp", "https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOp")]
    public void Rewrite_GivesTheDirectLink(string input, string expected) =>
        Assert.Equal(expected, SpecialSources.Rewrite(new Uri(input)).AbsoluteUri);
}
