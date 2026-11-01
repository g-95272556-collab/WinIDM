using System.Buffers.Binary;
using System.Text;
using WinIDM.Backup;

namespace WinIDM.Backup.Tests;

public class BackupCryptoTests
{
    private static readonly byte[] Plain = Encoding.UTF8.GetBytes("PK-sample-zip-test-data");

    [Fact]
    public void Encrypt_then_Decrypt_returns_original_bytes()
    {
        var encrypted = BackupCrypto.Encrypt(Plain, "Secret-123");

        var decrypted = BackupCrypto.Decrypt(encrypted, "Secret-123");

        Assert.Equal(Plain, decrypted);
    }

    [Fact]
    public void Header_has_spec_layout_magic_version_iterations_and_length()
    {
        var encrypted = BackupCrypto.Encrypt(Plain, "password");

        Assert.Equal("1DMCRYPT", Encoding.ASCII.GetString(encrypted, 0, 8));
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16BigEndian(encrypted.AsSpan(8, 2)));
        Assert.Equal(600_000u, BinaryPrimitives.ReadUInt32BigEndian(encrypted.AsSpan(10, 4)));
        Assert.Equal(BackupCrypto.HeaderSize + Plain.Length + BackupCrypto.TagSize, encrypted.Length);
        Assert.Equal(42, BackupCrypto.HeaderSize);
    }

    [Fact]
    public void Each_encryption_uses_fresh_salt_and_nonce()
    {
        var a = BackupCrypto.Encrypt(Plain, "same");
        var b = BackupCrypto.Encrypt(Plain, "same");

        Assert.NotEqual(a.AsSpan(14, 16).ToArray(), b.AsSpan(14, 16).ToArray());
        Assert.NotEqual(a.AsSpan(30, 12).ToArray(), b.AsSpan(30, 12).ToArray());
    }

    [Fact]
    public void Wrong_password_is_rejected_with_authentication_exception()
    {
        var encrypted = BackupCrypto.Encrypt(Plain, "correct");

        Assert.Throws<BackupAuthenticationException>(() => BackupCrypto.Decrypt(encrypted, "wrong"));
    }

    [Fact]
    public void Tampered_ciphertext_fails_tag_check()
    {
        var encrypted = BackupCrypto.Encrypt(Plain, "correct");
        encrypted[BackupCrypto.HeaderSize] ^= 0x01;

        Assert.Throws<BackupAuthenticationException>(() => BackupCrypto.Decrypt(encrypted, "correct"));
    }

    [Fact]
    public void Tampered_tag_fails_tag_check()
    {
        var encrypted = BackupCrypto.Encrypt(Plain, "correct");
        encrypted[^1] ^= 0xFF;

        Assert.Throws<BackupAuthenticationException>(() => BackupCrypto.Decrypt(encrypted, "correct"));
    }

    [Fact]
    public void Wrong_magic_is_rejected_as_format_error()
    {
        var encrypted = BackupCrypto.Encrypt(Plain, "correct");
        encrypted[0] = (byte)'X';

        Assert.Throws<BackupFormatException>(() => BackupCrypto.Decrypt(encrypted, "correct"));
    }

    [Fact]
    public void Unsupported_version_is_rejected_as_format_error()
    {
        var encrypted = BackupCrypto.Encrypt(Plain, "correct");
        encrypted[9] = 0x02;

        Assert.Throws<BackupFormatException>(() => BackupCrypto.Decrypt(encrypted, "correct"));
    }

    [Fact]
    public void Truncated_header_is_rejected_as_format_error()
    {
        var encrypted = BackupCrypto.Encrypt(Plain, "correct");

        Assert.Throws<BackupFormatException>(() => BackupCrypto.Decrypt(encrypted.AsSpan(0, 30), "correct"));
    }

    [Fact]
    public void Missing_password_on_encrypted_file_requires_password()
    {
        var encrypted = BackupCrypto.Encrypt(Plain, "correct");

        Assert.Throws<BackupPasswordRequiredException>(() => BackupCrypto.Decrypt(encrypted, null));
    }

    [Fact]
    public void Password_longer_than_1024_characters_is_refused()
    {
        var tooLong = new string('a', BackupCrypto.MaxPasswordLength + 1);

        Assert.Throws<ArgumentException>(() => BackupCrypto.Encrypt(Plain, tooLong));
    }

    [Fact]
    public void Password_of_exactly_1024_characters_is_accepted()
    {
        var max = new string('a', BackupCrypto.MaxPasswordLength);

        var decrypted = BackupCrypto.Decrypt(BackupCrypto.Encrypt(Plain, max), max);

        Assert.Equal(Plain, decrypted);
    }

    [Fact]
    public void IsEncrypted_detects_magic_only()
    {
        Assert.True(BackupCrypto.IsEncrypted(BackupCrypto.Encrypt(Plain, "x")));
        Assert.False(BackupCrypto.IsEncrypted(Plain));
    }
}
