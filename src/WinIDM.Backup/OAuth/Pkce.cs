using System.Security.Cryptography;
using System.Text;

namespace WinIDM.Backup;

/// <summary>PKCE utilities (RFC 7636) using the S256 method.</summary>
public static class Pkce
{
    /// <summary>Generate a code_verifier (43 characters) and the matching code_challenge.</summary>
    public static (string Verifier, string Challenge) Create()
    {
        var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return (verifier, Challenge(verifier));
    }

    public static string Challenge(string verifier) =>
        Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
