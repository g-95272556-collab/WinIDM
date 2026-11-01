using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WinIDM.Browser;

/// <summary>
/// Chrome extension package (CRX3). The header carries one or more public keys with an RSA or ECDSA signature
/// over the archive; the extension ID is the first 16 bytes of SHA-256 of the publisher key written as a–p.
/// <see cref="Parse"/> accepts a package only when a signature by the key that owns the ID verifies, so a file that
/// was altered or comes from another publisher is refused.
/// </summary>
public sealed class CrxPackage
{
    private static readonly byte[] Magic = "Cr24"u8.ToArray();
    private static readonly byte[] SignaturePrefix = Encoding.ASCII.GetBytes("CRX3 SignedData\0");

    public required string Id { get; init; }

    /// <summary>The publisher's public key (DER SubjectPublicKeyInfo); written to the manifest as <c>key</c>.</summary>
    public required byte[] PublicKey { get; init; }

    /// <summary>The ZIP archive with the extension files.</summary>
    public required byte[] Zip { get; init; }

    /// <exception cref="InvalidDataException">Not a CRX3 file, no valid signature, or not the expected extension.</exception>
    public static CrxPackage Parse(byte[] crx, string? expectedId = null)
    {
        if (crx.Length < 12 || !crx.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("Not a Chrome extension package (CRX).");
        if (BinaryPrimitives.ReadUInt32LittleEndian(crx.AsSpan(4)) != 3)
            throw new InvalidDataException("Only CRX version 3 packages are supported.");
        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(crx.AsSpan(8));
        if (headerLength > crx.Length - 12) throw new InvalidDataException("The CRX header is truncated.");

        var header = crx.AsSpan(12, (int)headerLength).ToArray();
        var zip = crx.AsSpan(12 + (int)headerLength).ToArray();

        var rsaProofs = new List<(byte[] Key, byte[] Sig)>();
        var ecdsaProofs = new List<(byte[] Key, byte[] Sig)>();
        byte[]? signedData = null;
        foreach (var (field, value) in Protobuf.LengthDelimited(header))
        {
            switch (field)
            {
                case 2: rsaProofs.Add(Proof(value)); break;
                case 3: ecdsaProofs.Add(Proof(value)); break;
                case 10000: signedData = value; break;
            }
        }
        if (signedData is null) throw new InvalidDataException("The CRX header has no signed data.");

        var crxIdBytes = Protobuf.LengthDelimited(signedData).Where(f => f.Field == 1).Select(f => f.Value).FirstOrDefault();
        if (crxIdBytes is not { Length: 16 }) throw new InvalidDataException("The CRX header has no extension ID.");
        var id = IdFromHash(crxIdBytes);
        if (expectedId is not null && !string.Equals(id, expectedId, StringComparison.Ordinal))
            throw new InvalidDataException($"The package is extension {id}, not {expectedId}.");

        var message = SignedMessage(signedData, zip);
        var owner = rsaProofs.FirstOrDefault(p => IdFromPublicKey(p.Key) == id);
        if (owner.Key is not null && VerifyRsa(owner.Key, owner.Sig, message))
            return new CrxPackage { Id = id, PublicKey = owner.Key, Zip = zip };
        var ecOwner = ecdsaProofs.FirstOrDefault(p => IdFromPublicKey(p.Key) == id);
        if (ecOwner.Key is not null && VerifyEcdsa(ecOwner.Key, ecOwner.Sig, message))
            return new CrxPackage { Id = id, PublicKey = ecOwner.Key, Zip = zip };
        throw new InvalidDataException("The package signature does not verify for its extension ID.");
    }

    /// <summary>Chrome extension ID of a public key.</summary>
    public static string IdFromPublicKey(byte[] publicKey) => IdFromHash(SHA256.HashData(publicKey).AsSpan(0, 16).ToArray());

    private static string IdFromHash(byte[] first16)
    {
        var sb = new StringBuilder(32);
        foreach (var b in first16) sb.Append((char)('a' + (b >> 4))).Append((char)('a' + (b & 15)));
        return sb.ToString();
    }

    private static (byte[] Key, byte[] Sig) Proof(byte[] value)
    {
        byte[] key = Array.Empty<byte>(), sig = Array.Empty<byte>();
        foreach (var (f, v) in Protobuf.LengthDelimited(value))
        {
            if (f == 1) key = v;
            else if (f == 2) sig = v;
        }
        return (key, sig);
    }

    private static byte[] SignedMessage(byte[] signedData, byte[] zip)
    {
        var msg = new byte[SignaturePrefix.Length + 4 + signedData.Length + zip.Length];
        SignaturePrefix.CopyTo(msg, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(SignaturePrefix.Length), (uint)signedData.Length);
        signedData.CopyTo(msg, SignaturePrefix.Length + 4);
        zip.CopyTo(msg, SignaturePrefix.Length + 4 + signedData.Length);
        return msg;
    }

    private static bool VerifyRsa(byte[] key, byte[] sig, byte[] message)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(key, out _);
            return rsa.VerifyData(message, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException) { return false; }
    }

    private static bool VerifyEcdsa(byte[] key, byte[] sig, byte[] message)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(key, out _);
            return ec.VerifyData(message, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException) { return false; }
    }

    /// <summary>Just enough protobuf to walk the CRX header: varints are skipped, length-delimited fields returned.</summary>
    internal static class Protobuf
    {
        public static IEnumerable<(int Field, byte[] Value)> LengthDelimited(byte[] data)
        {
            int i = 0;
            while (i < data.Length)
            {
                ulong tag = Varint(data, ref i);
                int field = (int)(tag >> 3), wire = (int)(tag & 7);
                switch (wire)
                {
                    case 0: Varint(data, ref i); break;
                    case 1: i += 8; break;
                    case 5: i += 4; break;
                    case 2:
                        ulong len = Varint(data, ref i);
                        if (len > (ulong)(data.Length - i)) throw new InvalidDataException("Malformed CRX header.");
                        yield return (field, data.AsSpan(i, (int)len).ToArray());
                        i += (int)len;
                        break;
                    default: throw new InvalidDataException("Malformed CRX header.");
                }
                if (i > data.Length) throw new InvalidDataException("Malformed CRX header.");
            }
        }

        public static ulong Varint(byte[] data, ref int i)
        {
            ulong result = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (i >= data.Length) throw new InvalidDataException("Malformed CRX header.");
                byte b = data[i++];
                result |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80) return result;
            }
            throw new InvalidDataException("Malformed CRX header.");
        }
    }
}
