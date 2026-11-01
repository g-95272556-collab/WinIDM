using System.Security.Cryptography;
using System.Text;

namespace WinIDM.Browser;

/// <summary>
/// Vault cryptography primitives. The master key (32 bytes) is wrapped by a KEK derived from the PIN
/// through PBKDF2-HMAC-SHA256. That wrapping is in turn protected by DPAPI (user scope).
/// Entries are encrypted with AES-256-GCM, 12-byte IV, 128-bit tag, and non-empty AAD.
/// </summary>
internal static class VaultCrypto
{
    public const int MasterKeyLength = 32;
    public const int SaltLength = 32;
    public const int NonceLength = 12;
    public const int TagLength = 16;
    public const int MinIterations = 600_000;

    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("winidm.vault.v1");

    public static byte[] NewMasterKey() => RandomNumberGenerator.GetBytes(MasterKeyLength);

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltLength);

    /// <summary>Derive the 32-byte KEK from the PIN. Expensive (PBKDF2).</summary>
    public static byte[] DeriveKek(string pin, byte[] salt, int iterations)
    {
        if (iterations < MinIterations) throw new InvalidDataException("The PBKDF2 iteration count is too low.");
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, 32);
    }

    /// <summary>Encrypt data: the result is nonce || tag || ciphertext.</summary>
    public static byte[] Seal(byte[] key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad)
    {
        if (aad.IsEmpty) throw new ArgumentException("AAD must not be empty.", nameof(aad));
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[] tag = new byte[TagLength];
        byte[] cipher = new byte[plaintext.Length];
        using (var gcm = new AesGcm(key, TagLength))
        {
            gcm.Encrypt(nonce, plaintext, cipher, tag, aad);
        }
        byte[] blob = new byte[NonceLength + TagLength + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceLength);
        cipher.CopyTo(blob, NonceLength + TagLength);
        return blob;
    }

    /// <summary>Decrypt a blob produced by <see cref="Seal"/>. Throws CryptographicException when invalid.</summary>
    public static byte[] Open(byte[] key, ReadOnlySpan<byte> blob, ReadOnlySpan<byte> aad)
    {
        if (aad.IsEmpty) throw new ArgumentException("AAD must not be empty.", nameof(aad));
        if (blob.Length < NonceLength + TagLength) throw new CryptographicException("The blob is too short.");
        ReadOnlySpan<byte> nonce = blob[..NonceLength];
        ReadOnlySpan<byte> tag = blob.Slice(NonceLength, TagLength);
        ReadOnlySpan<byte> cipher = blob[(NonceLength + TagLength)..];
        byte[] plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, TagLength);
        gcm.Decrypt(nonce, cipher, tag, plain, aad);
        return plain;
    }

    /// <summary>Payload text form: version "1:" followed by the base64 blob.</summary>
    public static string SealToPayload(byte[] key, string plaintext, ReadOnlySpan<byte> aad)
    {
        byte[] blob = Seal(key, Encoding.UTF8.GetBytes(plaintext), aad);
        return "1:" + Convert.ToBase64String(blob);
    }

    public static string OpenFromPayload(byte[] key, string payload, ReadOnlySpan<byte> aad)
    {
        if (!payload.StartsWith("1:", StringComparison.Ordinal)) throw new CryptographicException("The payload version is not supported.");
        byte[] blob = Convert.FromBase64String(payload[2..]);
        return Encoding.UTF8.GetString(Open(key, blob, aad));
    }

    /// <summary>Deterministic binding identifier (HMAC) so duplicate origin+user entries can be detected without storing the user name.</summary>
    public static string BindingId(byte[] masterKey, string origin, string username)
    {
        byte[] message = Encoding.UTF8.GetBytes("binding|" + origin + "|" + username);
        using var hmac = new HMACSHA256(masterKey);
        return Convert.ToHexString(hmac.ComputeHash(message)).ToLowerInvariant();
    }

    /// <summary>Keyed HMAC stamp to compare secret values (for example duplicate passwords) without storing them.</summary>
    public static string Fingerprint(byte[] masterKey, string value)
    {
        byte[] message = Encoding.UTF8.GetBytes("fingerprint|" + value);
        using var hmac = new HMACSHA256(masterKey);
        return Convert.ToHexString(hmac.ComputeHash(message));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static byte[] ProtectForUser(byte[] data) =>
        System.Security.Cryptography.ProtectedData.Protect(data, DpapiEntropy, DataProtectionScope.CurrentUser);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static byte[] UnprotectForUser(byte[] data) =>
        System.Security.Cryptography.ProtectedData.Unprotect(data, DpapiEntropy, DataProtectionScope.CurrentUser);
}
