using System.Security.Cryptography;

namespace WinIDM.Browser;

/// <summary>Character sets used by the password generator.</summary>
public sealed record PasswordGeneratorOptions(bool Lowercase = true, bool Uppercase = true, bool Digits = true, bool Symbols = true);

/// <summary>
/// Password generator based on a cryptographic RNG. Each selected set is guaranteed
/// to be represented at least once, and selection is free of modulo bias.
/// </summary>
public static class PasswordGenerator
{
    public const int MinLength = 8;
    public const int MaxLength = 256;

    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Digits = "0123456789";

    // Punctuation that is safe for most forms: no quotes, backslash, or spaces.
    private const string Symbols = "!@#$%^&*()-_=+[]{};:,.<>?";

    public static string Generate(int length = 20, PasswordGeneratorOptions? options = null)
    {
        if (length < MinLength || length > MaxLength)
            throw new ArgumentOutOfRangeException(nameof(length), $"Length must be {MinLength} to {MaxLength}.");

        options ??= new PasswordGeneratorOptions();
        var sets = new List<string>();
        if (options.Lowercase) sets.Add(Lower);
        if (options.Uppercase) sets.Add(Upper);
        if (options.Digits) sets.Add(Digits);
        if (options.Symbols) sets.Add(Symbols);
        if (sets.Count == 0) throw new ArgumentException("At least one character set is required.", nameof(options));
        if (length < sets.Count) throw new ArgumentException("Length is too short for all selected sets.", nameof(length));

        string all = string.Concat(sets);
        var chars = new char[length];

        // Guarantee one character from each set, then fill the rest from the combined sets.
        int pos = 0;
        foreach (string set in sets) chars[pos++] = set[RandomNumberGenerator.GetInt32(set.Length)];
        for (; pos < length; pos++) chars[pos] = all[RandomNumberGenerator.GetInt32(all.Length)];

        // Fisher-Yates so the positions of the guaranteed set characters cannot be predicted.
        for (int i = chars.Length - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }
}
