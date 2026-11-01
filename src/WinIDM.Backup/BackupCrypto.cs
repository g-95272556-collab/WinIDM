using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WinIDM.Backup;

/// <summary>
/// Encrypted backup file format. Matched to decompiled IDM+ code (i/jc.java, i/ic.java, i/hc.java):
/// 42-byte header = magic "1DMCRYPT" (8) + BE uint16 version 0x0001 (2) + BE uint32 PBKDF2 iterations (4)
///                  + salt (16) + nonce (12); followed by AES-256-GCM ciphertext and a 128-bit tag (16).
/// The 42-byte header is used as GCM AAD (associated data), exactly like IDM+.
/// Key = PBKDF2-HMAC-SHA256 (32 bytes) of the UTF-8 password. The whole ZIP is encrypted as a single stream.
/// See <c>src/WinIDM.Backup/FORMAT.md</c> for what has and has not been verified.
/// </summary>
public static class BackupCrypto
{
    public const int HeaderSize = 42;
    public const int SaltSize = 16;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int KeySize = 32;
    public const int Iterations = 600_000;
    public const ushort FormatVersion = 1;
    public const int MaxPasswordLength = 1024;

    /// <summary>Iteration range accepted when reading (same as the limits in IDM+ code: 1000 to 1000000).</summary>
    public const int MinIterations = 1_000;
    public const int MaxIterations = 1_000_000;

    private static readonly byte[] Magic = "1DMCRYPT"u8.ToArray();

    /// <summary>True if the leading bytes are the magic of an encrypted file.</summary>
    public static bool IsEncrypted(ReadOnlySpan<byte> data) =>
        data.Length >= Magic.Length && data[..Magic.Length].SequenceEqual(Magic);

    public static void ValidatePassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length > MaxPasswordLength)
        {
            throw new ArgumentException($"Password exceeds {MaxPasswordLength} characters.", nameof(password));
        }

        if (!IsWellFormedUtf16(password))
        {
            throw new ArgumentException("Password contains an unpaired UTF-16 surrogate.", nameof(password));
        }
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> plain, string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Password cannot be empty.", nameof(password));
        }

        ValidatePassword(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var header = BuildHeader(Iterations, salt, nonce);
        var key = DeriveKey(password, salt, Iterations);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plain, cipher, tag, header);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        var output = new byte[HeaderSize + cipher.Length + TagSize];
        header.CopyTo(output, 0);
        cipher.CopyTo(output, HeaderSize);
        tag.CopyTo(output, HeaderSize + cipher.Length);
        return output;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> data, string? password)
    {
        if (!IsEncrypted(data))
        {
            throw new BackupFormatException("Invalid backup header (1DMCRYPT magic missing).");
        }

        if (data.Length < HeaderSize + TagSize)
        {
            throw new BackupFormatException("Encrypted backup file is too short (corrupt or truncated).");
        }

        var version = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(8, 2));
        if (version != FormatVersion)
        {
            throw new BackupFormatException($"Backup format version {version} is not supported.");
        }

        var iterations = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(10, 4));
        if (iterations < MinIterations || iterations > MaxIterations)
        {
            throw new BackupFormatException($"PBKDF2 iteration count {iterations} is outside the allowed range.");
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new BackupPasswordRequiredException();
        }

        ValidatePassword(password);

        var header = data[..HeaderSize];
        var salt = data.Slice(14, SaltSize).ToArray();
        var nonce = data.Slice(30, NonceSize).ToArray();
        var cipherLength = data.Length - HeaderSize - TagSize;
        var cipher = data.Slice(HeaderSize, cipherLength).ToArray();
        var tag = data.Slice(HeaderSize + cipherLength, TagSize).ToArray();
        var plain = new byte[cipherLength];
        var key = DeriveKey(password, salt, (int)iterations);

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, header);
        }
        catch (CryptographicException ex)
        {
            throw new BackupAuthenticationException("Wrong password or the backup data has been modified.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return plain;
    }

    /// <summary>Build the 42-byte header following the IDM+ layout (magic, version, BE iterations, salt, nonce).</summary>
    internal static byte[] BuildHeader(int iterations, byte[] salt, byte[] nonce)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8, 2), FormatVersion);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(10, 4), (uint)iterations);
        salt.CopyTo(header, 14);
        nonce.CopyTo(header, 30);
        return header;
    }

    internal static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);

    private static bool IsWellFormedUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }
}
