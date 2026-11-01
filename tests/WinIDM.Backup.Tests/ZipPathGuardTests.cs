using WinIDM.Backup;

namespace WinIDM.Backup.Tests;

public class ZipPathGuardTests
{
    [Theory]
    [InlineData("../evil.json")]
    [InlineData("a/../../b.json")]
    [InlineData("..\\evil.json")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\x.json")]
    [InlineData("C:evil.json")]
    [InlineData("nested\0null.json")]
    [InlineData("")]
    public void Unsafe_entry_names_are_rejected(string name)
    {
        Assert.Throws<BackupSecurityException>(() => ZipPathGuard.Validate(name));
    }

    [Theory]
    [InlineData("idm_settings.json")]
    [InlineData("download_info_12.json")]
    [InlineData("sub/bookmarks.json")]
    [InlineData("sub\\history.json")]
    public void Safe_entry_names_are_accepted(string name)
    {
        Assert.Equal(name, ZipPathGuard.Validate(name));
    }

    [Fact]
    public void ResolveInside_keeps_result_under_base_directory()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "winidm-zip-base");

        var resolved = ZipPathGuard.ResolveInside(baseDir, "pages/index.mhtml");

        Assert.StartsWith(Path.GetFullPath(baseDir), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveInside_rejects_escape_attempt()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "winidm-zip-base");

        Assert.Throws<BackupSecurityException>(() => ZipPathGuard.ResolveInside(baseDir, "../../outside.txt"));
    }

    [Fact]
    public void BackupNames_validate_rejects_path_like_names()
    {
        Assert.False(BackupNames.IsSafe("../winidm-backup-x.zip"));
        Assert.False(BackupNames.IsSafe("a/b.zip"));
        Assert.True(BackupNames.IsSafe(BackupNames.NewName(DateTimeOffset.UtcNow, encrypted: true)));
    }
}
