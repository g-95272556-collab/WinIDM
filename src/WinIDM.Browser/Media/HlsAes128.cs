using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WinIDM.Browser;

/// <summary>
/// HLS AES-128 segment decryption (AES-CBC, PKCS7). If no IV is declared in EXT-X-KEY,
/// the IV is the media sequence number as a 128-bit big-endian integer, per RFC 8216.
/// </summary>
public static class HlsAes128
{
    public const int KeyLength = 16;

    public static byte[] DeriveIv(long sequenceNumber)
    {
        var iv = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8), sequenceNumber);
        return iv;
    }

    /// <summary>Decrypt one segment. Throws CryptographicException if the data or key is invalid.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> segment, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (key.Length != KeyLength) throw new ArgumentException("AES-128 key must be 16 bytes.", nameof(key));
        if (iv.Length != 16) throw new ArgumentException("IV must be 16 bytes.", nameof(iv));
        if (segment.Length == 0 || segment.Length % 16 != 0)
            throw new CryptographicException("Segment length is not a multiple of 16 bytes.");

        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes.DecryptCbc(segment, iv, PaddingMode.PKCS7);
    }
}
