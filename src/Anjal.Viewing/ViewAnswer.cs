namespace Anjal.Viewing;

/// <summary>rc.29 (DES-22 §4.2): the viewer's answer to one request. Pictures and files travel beside it.</summary>
public sealed class ViewAnswer
{
    /// <summary>How it went: one of the <see cref="ViewStatus"/> words.</summary>
    public string Status { get; init; } = ViewStatus.Ok;

    /// <summary>What was found, as JSON of the kind's own shape; null when there is nothing to say.</summary>
    public string? Json { get; init; }

    /// <summary>For the log only: the library and the exception's type, never the file's content.</summary>
    public string? Detail { get; init; }
}

/// <summary>rc.29 (DES-22 §5): how a job went. The webmail turns each into words; none carries a file's text.</summary>
public static class ViewStatus
{
    /// <summary>Done.</summary>
    public const string Ok = "ok";

    /// <summary>The file needs a password.</summary>
    public const string NeedsPassword = "needs-password";

    /// <summary>The password given does not open it.</summary>
    public const string WrongPassword = "wrong-password";

    /// <summary>Too large or too complex to show here (a library's limit, or 64 MB).</summary>
    public const string TooLarge = "too-large";

    /// <summary>It took too long and its process was stopped.</summary>
    public const string TooSlow = "too-slow";

    /// <summary>The file is damaged.</summary>
    public const string Damaged = "damaged";

    /// <summary>Anything else that went wrong: "This file could not be shown".</summary>
    public const string Failed = "failed";

    /// <summary>The viewer does not show this kind of file.</summary>
    public const string Unsupported = "unsupported";

    /// <summary>An archive (or a file inside one) Anjal does not open: too many files, too large, a program.</summary>
    public const string Refused = "refused";

    /// <summary>
    /// AC-20: a file from an archive locked with the old ZipCrypto method failed after its password passed the
    /// method's one-byte check - the password is wrong, or the file is damaged; the two cannot be told apart.
    /// </summary>
    public const string WrongOrDamaged = "wrong-or-damaged";

    /// <summary>The person already has as many jobs running as they may, or the queue is full.</summary>
    public const string Busy = "busy";

    /// <summary>The viewer process is not there.</summary>
    public const string Unavailable = "unavailable";
}
