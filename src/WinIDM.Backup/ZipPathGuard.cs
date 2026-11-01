namespace WinIDM.Backup;

/// <summary>Zip-slip protection: ZIP entries must stay inside the target directory.</summary>
public static class ZipPathGuard
{
    public const int MaxEntries = 10_000;
    private const int MaxNameLength = 512;

    /// <summary>Validate a ZIP entry name and return the same name. Throws <see cref="BackupSecurityException"/> if it is dangerous.</summary>
    public static string Validate(string? entryName)
    {
        if (string.IsNullOrEmpty(entryName))
        {
            throw Unsafe(entryName, "empty name");
        }

        if (entryName.Length > MaxNameLength)
        {
            throw Unsafe(entryName, "name too long");
        }

        if (entryName.IndexOf('\0') >= 0)
        {
            throw Unsafe(entryName, "null character");
        }

        if (entryName.Contains(':'))
        {
            throw Unsafe(entryName, "contains ':'");
        }

        if (entryName[0] is '/' or '\\' || Path.IsPathRooted(entryName))
        {
            throw Unsafe(entryName, "absolute path");
        }

        foreach (var segment in entryName.Split('/', '\\'))
        {
            if (segment == "..")
            {
                throw Unsafe(entryName, "contains '..'");
            }
        }

        return entryName;
    }

    /// <summary>Combine the base directory with the entry name and ensure the result stays inside the base directory.</summary>
    public static string ResolveInside(string baseDirectory, string entryName)
    {
        Validate(entryName);

        var baseFull = Path.GetFullPath(baseDirectory);
        if (!baseFull.EndsWith(Path.DirectorySeparatorChar))
        {
            baseFull += Path.DirectorySeparatorChar;
        }

        var target = Path.GetFullPath(Path.Combine(baseFull, entryName.Replace('\\', '/')));
        if (!target.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase))
        {
            throw Unsafe(entryName, "escapes the target directory");
        }

        return target;
    }

    private static BackupSecurityException Unsafe(string? entryName, string reason) =>
        new($"Dangerous ZIP entry rejected ({reason}): '{entryName}'.");
}
