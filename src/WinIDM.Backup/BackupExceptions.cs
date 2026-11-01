namespace WinIDM.Backup;

/// <summary>Base for all backup/restore errors.</summary>
public class BackupException : Exception
{
    public BackupException(string message) : base(message) { }

    public BackupException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The backup file's header, magic, version, manifest or schema is invalid/corrupt.</summary>
public sealed class BackupFormatException : BackupException
{
    public BackupFormatException(string message) : base(message) { }

    public BackupFormatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Wrong password or the data has been modified (AES-GCM tag failure).</summary>
public sealed class BackupAuthenticationException : BackupException
{
    public BackupAuthenticationException(string message, Exception inner) : base(message, inner) { }

    public BackupAuthenticationException(string message) : base(message) { }
}

/// <summary>The file is encrypted but no password was supplied.</summary>
public sealed class BackupPasswordRequiredException : BackupException
{
    public BackupPasswordRequiredException() : base("This backup file is encrypted. Enter the password.") { }
}

/// <summary>A ZIP entry with a dangerous path (zip-slip, absolute path, ".." or ':').</summary>
public sealed class BackupSecurityException : BackupException
{
    public BackupSecurityException(string message) : base(message) { }
}

/// <summary>Communication error with a remote destination.</summary>
public sealed class BackupTransportException : BackupException
{
    public BackupTransportException(string message, int? statusCode = null) : base(message)
    {
        StatusCode = statusCode;
    }

    public BackupTransportException(string message, Exception inner) : base(message, inner) { }

    public int? StatusCode { get; }
}

/// <summary>Restore partly completed; <see cref="Report"/> describes each item.</summary>
public sealed class BackupPartialRestoreException : BackupException
{
    public BackupPartialRestoreException(RestoreReport report)
        : base("Restore partly completed. Review the report for the items that failed.")
    {
        Report = report;
    }

    public RestoreReport Report { get; }
}
