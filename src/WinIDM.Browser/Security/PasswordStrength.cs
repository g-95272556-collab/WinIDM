namespace WinIDM.Browser;

/// <summary>Password strength check result.</summary>
/// <param name="Score">0 (very weak) to 4 (strong).</param>
/// <param name="EntropyBits">Estimated entropy based on length and character set.</param>
/// <param name="Warnings">Warnings in English; empty if there are no clear issues.</param>
public sealed record PasswordStrengthResult(int Score, double EntropyBits, IReadOnlyList<string> Warnings);

/// <summary>
/// Local password strength check. No password is sent anywhere.
/// The entropy estimate is length x log2(character set size). The list of common passwords is small and
/// is only for basic warnings; it is not a substitute for a breach check.
/// </summary>
public static class PasswordStrength
{
    public const int MinAcceptableLength = 8;

    private static readonly HashSet<string> CommonPasswords = new(StringComparer.Ordinal)
    {
        "password", "password1", "passw0rd", "123456", "12345678", "123456789", "1234567890", "111111",
        "123123", "qwerty", "qwerty123", "abc123", "letmein", "welcome", "admin", "iloveyou", "monkey",
        "dragon", "sunshine", "login", "football", "master", "000000", "trustno1",
    };

    public static PasswordStrengthResult Evaluate(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var warnings = new List<string>();

        if (password.Length == 0)
        {
            warnings.Add("Password is empty.");
            return new PasswordStrengthResult(0, 0, warnings);
        }

        bool lower = false, upper = false, digit = false, symbol = false;
        foreach (char c in password)
        {
            if (char.IsAsciiLetterLower(c)) lower = true;
            else if (char.IsAsciiLetterUpper(c)) upper = true;
            else if (char.IsAsciiDigit(c)) digit = true;
            else symbol = true;
        }

        int pool = 0;
        if (lower) pool += 26;
        if (upper) pool += 26;
        if (digit) pool += 10;
        if (symbol) pool += 33;
        double entropy = password.Length * Math.Log2(Math.Max(pool, 2));

        if (IsCommon(password))
        {
            warnings.Add("This password is too common and easy to guess.");
            return new PasswordStrengthResult(0, Math.Min(entropy, 20), warnings);
        }

        if (password.Length < MinAcceptableLength) warnings.Add($"Too short; use at least {MinAcceptableLength} characters.");
        if (HasLongRepeat(password)) { warnings.Add("Contains a long run of repeated characters."); entropy *= 0.5; }
        if (HasSequentialRun(password)) { warnings.Add("Contains an easy-to-guess character sequence (for example abcd or 1234)."); entropy *= 0.7; }
        if (pool <= 26 && password.Length < 12) warnings.Add("Use a mix of uppercase, lowercase, and digits or symbols.");

        int score = entropy switch
        {
            < 28 => 0,
            < 36 => 1,
            < 60 => 2,
            < 80 => 3,
            _ => 4,
        };
        if (password.Length < MinAcceptableLength && score > 1) score = 1;

        return new PasswordStrengthResult(score, Math.Round(entropy, 1), warnings);
    }

    private static bool IsCommon(string password)
    {
        string lower = password.ToLowerInvariant();
        if (CommonPasswords.Contains(lower)) return true;
        // Simple variants: a common word followed by short digits (for example "password2024").
        foreach (string common in CommonPasswords)
        {
            if (common.Length >= 6 && lower.StartsWith(common, StringComparison.Ordinal) && lower.Length - common.Length <= 4 &&
                lower[common.Length..].All(char.IsAsciiDigit))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasLongRepeat(string s)
    {
        int run = 1;
        for (int i = 1; i < s.Length; i++)
        {
            run = s[i] == s[i - 1] ? run + 1 : 1;
            if (run >= 4) return true;
        }
        return false;
    }

    private static bool HasSequentialRun(string s)
    {
        int run = 1;
        for (int i = 1; i < s.Length; i++)
        {
            int delta = s[i] - s[i - 1];
            if ((delta == 1 || delta == -1) && char.IsAsciiLetterOrDigit(s[i]) && char.IsAsciiLetterOrDigit(s[i - 1]))
                run++;
            else
                run = 1;
            if (run >= 4) return true;
        }
        return false;
    }
}
