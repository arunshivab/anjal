namespace Anjal.Store;

/// <summary>One remembered greylisting triplet.</summary>
public sealed class GreylistRow
{
    /// <summary>The triplet key: client network, sender and recipient.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>When the triplet was first seen.</summary>
    public System.DateTimeOffset FirstSeen { get; set; }

    /// <summary>When the triplet was last seen.</summary>
    public System.DateTimeOffset LastSeen { get; set; }

    /// <summary>Whether a retry has passed, so the sender is no longer delayed.</summary>
    public bool Passed { get; set; }
}

/// <summary>One stored, non-secret setting of a service.</summary>
public sealed class SettingRow
{
    /// <summary>The server scope.</summary>
    public const string ServerScope = "server";

    /// <summary>The webmail scope.</summary>
    public const string WebmailScope = "webmail";

    /// <summary>Which service the setting belongs to: <see cref="ServerScope"/> or <see cref="WebmailScope"/>.</summary>
    public string Scope { get; set; } = ServerScope;

    /// <summary>The setting's name, for example <c>ANJAL_TLS_DEFAULT_MODE</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The value.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>When it was last changed.</summary>
    public System.DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Who changed it: "import" for the one-time import, otherwise the API caller.</summary>
    public string UpdatedBy { get; set; } = string.Empty;
}
