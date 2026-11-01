using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using WinIDM.Backup;
using WinIDM.Core.Models;

namespace WinIDM.Backup.Tests;

/// <summary>
/// Independent writer and reader following the layout decompiled from IDM+ (i/jc.java, i/ic.java, i/hc.java):
/// a 42-byte header as AAD, PBKDF2-HMAC-SHA256 (computed manually like the Java algorithm), AES-256-GCM.
/// Files generated here are synthetic, NOT real IDM+ samples.
/// </summary>
internal static class IdmLayoutWriter
{
    public static byte[] Encrypt(byte[] plain, string password, int iterations = 600_000)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var header = new byte[42];
        Encoding.ASCII.GetBytes("1DMCRYPT").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8, 2), 1);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(10, 4), iterations);
        salt.CopyTo(header, 14);
        nonce.CopyTo(header, 30);

        var key = Pbkdf2HmacSha256(Encoding.UTF8.GetBytes(password), salt, iterations, 32);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, header);

        return header.Concat(cipher).Concat(tag).ToArray();
    }

    public static byte[] Decrypt(byte[] file, string password)
    {
        var iterations = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(10, 4));
        var salt = file[14..30];
        var nonce = file[30..42];
        var cipherLength = file.Length - 42 - 16;
        var key = Pbkdf2HmacSha256(Encoding.UTF8.GetBytes(password), salt, iterations, 32);
        var plain = new byte[cipherLength];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, file[42..(42 + cipherLength)], file[^16..], plain, file[..42]);
        return plain;
    }

    /// <summary>PBKDF2 written following the algorithm in Pbkdf2HmacSha256.java (INT(i) block BE, U1..Uc, XOR).</summary>
    public static byte[] Pbkdf2HmacSha256(byte[] password, byte[] salt, int iterations, int length)
    {
        using var hmac = new HMACSHA256(password);
        var output = new byte[length];
        var blocks = (length + 31) / 32;
        for (var block = 1; block <= blocks; block++)
        {
            var seed = new byte[salt.Length + 4];
            salt.CopyTo(seed, 0);
            BinaryPrimitives.WriteInt32BigEndian(seed.AsSpan(salt.Length, 4), block);
            var u = hmac.ComputeHash(seed);
            var t = (byte[])u.Clone();
            for (var i = 1; i < iterations; i++)
            {
                u = hmac.ComputeHash(u);
                for (var j = 0; j < t.Length; j++)
                {
                    t[j] ^= u[j];
                }
            }

            var offset = (block - 1) * 32;
            Array.Copy(t, 0, output, offset, Math.Min(32, length - offset));
        }

        return output;
    }
}

public class BackupCryptoCompatTests
{
    private static readonly byte[] Plain = Encoding.UTF8.GetBytes("PK-compat-test-content");

    [Fact]
    public void Backup_crypto_output_decrypts_with_independent_idm_layout_reader()
    {
        var file = BackupCrypto.Encrypt(Plain, "test-password-12");

        Assert.Equal(Plain, IdmLayoutWriter.Decrypt(file, "test-password-12"));
    }

    [Fact]
    public void Independent_idm_layout_file_decrypts_with_backup_crypto()
    {
        var file = IdmLayoutWriter.Encrypt(Plain, "test-password-12");

        Assert.Equal(Plain, BackupCrypto.Decrypt(file, "test-password-12"));
    }

    [Fact]
    public void Iteration_count_of_one_thousand_is_accepted_and_below_it_is_refused()
    {
        var accepted = IdmLayoutWriter.Encrypt(Plain, "test-password-12", iterations: 1_000);
        Assert.Equal(Plain, BackupCrypto.Decrypt(accepted, "test-password-12"));

        var refused = IdmLayoutWriter.Encrypt(Plain, "test-password-12", iterations: 999);
        Assert.Throws<BackupFormatException>(() => BackupCrypto.Decrypt(refused, "test-password-12"));
    }

    [Fact]
    public void Password_with_unpaired_surrogate_is_refused_like_idm_plus()
    {
        Assert.Throws<ArgumentException>(() => BackupCrypto.Encrypt(Plain, "a\uD800b"));
    }

    [Fact]
    public void Password_with_valid_surrogate_pair_is_accepted()
    {
        var password = "test-\U0001F512-password";

        Assert.Equal(Plain, BackupCrypto.Decrypt(BackupCrypto.Encrypt(Plain, password), password));
    }
}

public class IdmBackupInspectorTests
{
    private const string Password = "test-password-12";

    private static byte[] IdmLikeZip() => CloudTest.ZipBytes(
        ("idm_settings.json", Encoding.UTF8.GetBytes("{\"threads\":\"7\"}")),
        ("key_bookmarks.json", Encoding.UTF8.GetBytes("[]")),
        ("key_history.json", Encoding.UTF8.GetBytes("[]")),
        ("download_info_1.json", Encoding.UTF8.GetBytes("<idm><downloadInfo version=\"30257\"/></idm>")),
        ("shortcut_icon_1", new byte[] { 1, 2, 3 }));

    private static byte[] WinIdmBackup(TempDir dir)
    {
        var service = new BackupService(new FakeDownloadRepository(), new FakeSettingsStore(), new FakeDataSource(), dir.Path);
        var path = dir.File("winidm.zip.enc");
        service.CreateAsync(path, BackupCategories.Settings, Password).GetAwaiter().GetResult();
        return File.ReadAllBytes(path);
    }

    [Fact]
    public void Winidm_encrypted_backup_is_authenticated_and_reports_header_fields()
    {
        using var dir = new TempDir();
        var file = WinIdmBackup(dir);

        var report = IdmBackupInspector.Inspect(file, Password, "winidm.zip.enc");

        Assert.Equal(BackupContainerKind.Encrypted1DmCrypt, report.Kind);
        Assert.Equal(InspectAuthStatus.Authenticated, report.Authentication);
        Assert.True(report.ZipReadable);
        Assert.Equal("WinIDM (manifest.json present)", report.LikelySource);
        Assert.Contains(report.Entries, e => e.Name == "manifest.json" && e.Origin == "WinIDM");
        var header = report.Header!;
        Assert.Equal("1DMCRYPT", header.Magic);
        Assert.Equal(1, header.Version);
        Assert.Equal(600_000u, header.Iterations);
        Assert.True(header.IterationsWithinIdmRange);
        Assert.Equal(16, header.SaltLength);
        Assert.Equal(12, header.NonceLength);
        Assert.Equal(file.Length - 42 - 16, header.CiphertextLength);
        Assert.True(header.TagPresent);
    }

    [Fact]
    public void Without_password_structure_is_reported_but_entries_are_not_read()
    {
        using var dir = new TempDir();
        var file = WinIdmBackup(dir);

        var report = IdmBackupInspector.Inspect(file, password: null);

        Assert.Equal(InspectAuthStatus.NotAttempted, report.Authentication);
        Assert.False(report.ZipReadable);
        Assert.Empty(report.Entries);
        Assert.Contains(report.Findings, f => f.Contains("No password supplied", StringComparison.Ordinal));
        Assert.Equal(BackupContainerKind.Encrypted1DmCrypt, report.Kind);
    }

    [Fact]
    public void Wrong_password_is_rejected_without_reading_entries()
    {
        using var dir = new TempDir();
        var file = WinIdmBackup(dir);

        var report = IdmBackupInspector.Inspect(file, "wrong-password-99");

        Assert.Equal(InspectAuthStatus.RejectedOrTampered, report.Authentication);
        Assert.Empty(report.Entries);
    }

    [Fact]
    public void Tampered_ciphertext_fails_authentication()
    {
        using var dir = new TempDir();
        var file = WinIdmBackup(dir);
        file[BackupCrypto.HeaderSize + 1] ^= 0x40;

        var report = IdmBackupInspector.Inspect(file, Password);

        Assert.Equal(InspectAuthStatus.RejectedOrTampered, report.Authentication);
    }

    [Fact]
    public void Unsupported_version_and_out_of_range_iterations_are_flagged()
    {
        var file = IdmLayoutWriter.Encrypt(Encoding.UTF8.GetBytes("x"), Password, iterations: 1_000);
        file[9] = 2;
        var versioned = IdmBackupInspector.Inspect(file, Password);
        Assert.Equal(InspectAuthStatus.UnsupportedHeader, versioned.Authentication);
        Assert.Contains(versioned.Findings, f => f.Contains("Version 2", StringComparison.Ordinal));

        var low = IdmLayoutWriter.Encrypt(Encoding.UTF8.GetBytes("x"), Password, iterations: 1_000);
        BinaryPrimitives.WriteUInt32BigEndian(low.AsSpan(10, 4), 5);
        var lowReport = IdmBackupInspector.Inspect(low, Password);
        Assert.False(lowReport.Header!.IterationsWithinIdmRange);
        Assert.Equal(InspectAuthStatus.UnsupportedHeader, lowReport.Authentication);
    }

    [Fact]
    public void Truncated_files_are_reported_without_throwing()
    {
        var full = IdmLayoutWriter.Encrypt(Encoding.UTF8.GetBytes("content"), Password);

        var shortHeader = IdmBackupInspector.Inspect(full[..30], Password);
        Assert.Null(shortHeader.Header);
        Assert.Contains(shortHeader.Findings, f => f.Contains("Incomplete header", StringComparison.Ordinal));

        var noTag = IdmBackupInspector.Inspect(full[..50], Password);
        Assert.False(noTag.Header!.TagPresent);
        Assert.Contains(noTag.Findings, f => f.Contains("No 16-byte GCM tag", StringComparison.Ordinal));
    }

    [Fact]
    public void Random_bytes_are_unknown()
    {
        var report = IdmBackupInspector.Inspect(CloudTest.RandomBytes(256, seed: 31), Password);

        Assert.Equal(BackupContainerKind.Unknown, report.Kind);
        Assert.Contains(report.Findings, f => f.Contains("Not 1DMCRYPT", StringComparison.Ordinal));
    }

    [Fact]
    public void Plain_zip_with_idm_names_is_listed_and_classified_as_possible_idm()
    {
        var report = IdmBackupInspector.Inspect(IdmLikeZip());

        Assert.Equal(BackupContainerKind.PlainZip, report.Kind);
        Assert.True(report.ZipReadable);
        Assert.Equal(InspectAuthStatus.NotApplicable, report.Authentication);
        Assert.Contains(report.Entries, e => e.Name == "key_bookmarks.json" && e.Origin.StartsWith("IDM+", StringComparison.Ordinal));
        Assert.Contains(report.Entries, e => e.Name == "download_info_1.json" && e.Origin.Contains("XML", StringComparison.Ordinal));
        Assert.StartsWith("Probably IDM+", report.LikelySource, StringComparison.Ordinal);
    }

    [Fact]
    public void Self_generated_idm_layout_file_is_authenticated_and_listed()
    {
        var file = IdmLayoutWriter.Encrypt(IdmLikeZip(), Password);

        var report = IdmBackupInspector.Inspect(file, Password, "test-idm-layout.zip");

        Assert.Equal(BackupContainerKind.Encrypted1DmCrypt, report.Kind);
        Assert.Equal(InspectAuthStatus.Authenticated, report.Authentication);
        Assert.True(report.ZipReadable);
        Assert.Contains(report.Entries, e => e.Name == "shortcut_icon_1" && e.UncompressedBytes == 3);
        Assert.StartsWith("Probably IDM+", report.LikelySource, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsafe_zip_entry_is_reported_and_never_extracted()
    {
        using var dir = new TempDir();
        var zip = CloudTest.ZipBytes(("../outside.txt", Encoding.UTF8.GetBytes("malicious")), ("idm_settings.json", Encoding.UTF8.GetBytes("{}")));
        var path = dir.File("malicious.zip");
        File.WriteAllBytes(path, zip);

        var report = IdmBackupInspector.InspectFile(path);

        var unsafeEntry = Assert.Single(report.Entries, e => !e.PathSafe);
        Assert.Equal("../outside.txt", unsafeEntry.Name);
        Assert.Contains(report.Findings, f => f.Contains("not extracted", StringComparison.Ordinal));
        Assert.Equal(new[] { path }, Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Decrypted_content_that_is_not_zip_is_reported()
    {
        var file = IdmLayoutWriter.Encrypt(Encoding.UTF8.GetBytes("not a zip at all"), Password);

        var report = IdmBackupInspector.Inspect(file, Password);

        Assert.Equal(InspectAuthStatus.Authenticated, report.Authentication);
        Assert.False(report.ZipReadable);
        Assert.Contains(report.Findings, f => f.Contains("not a ZIP", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_file_from_disk_produces_readable_text_summary()
    {
        using var dir = new TempDir();
        var path = dir.File("test.zip.enc");
        File.WriteAllBytes(path, IdmLayoutWriter.Encrypt(IdmLikeZip(), Password));

        var report = IdmBackupInspector.InspectFile(path, Password);
        var text = report.ToText();

        Assert.Contains("Authentication: succeeded", text, StringComparison.Ordinal);
        Assert.Contains("key_bookmarks.json", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_file_throws_file_not_found()
    {
        using var dir = new TempDir();

        Assert.Throws<FileNotFoundException>(() => IdmBackupInspector.InspectFile(dir.File("missing.zip.enc"), Password));
    }
}
