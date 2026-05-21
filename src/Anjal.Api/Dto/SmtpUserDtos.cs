namespace Anjal.Api.Dto;

/// <summary>
/// Request body for creating or updating an SMTP submission user. The
/// password field is the plaintext password; the API hashes it via
/// <see cref="Anjal.Smtp.Pbkdf2Hasher"/> before persisting.
/// </summary>
public sealed class SmtpUserRequest
{
    /// <summary>Unique username (case-insensitive).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Plaintext password. Hashed before persisting.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Domains the user is permitted to send <c>MAIL FROM</c> as. Empty
    /// list means admin authority (any domain). Comparison is
    /// case-insensitive.
    /// </summary>
    public System.Collections.Generic.IList<string> AllowedFromDomains { get; set; }
        = new System.Collections.Generic.List<string>();

    /// <summary>When false, authentication attempts fail regardless of password.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Response body for an SMTP user. Critically, the password hash is
/// never returned by the API - only metadata.
/// </summary>
public sealed class SmtpUserResponse
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The username.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Allowed-from domains.</summary>
    public System.Collections.Generic.IList<string> AllowedFromDomains { get; set; }
        = new System.Collections.Generic.List<string>();

    /// <summary>Whether the user is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>When the user was created or last updated.</summary>
    public System.DateTimeOffset UpdatedAt { get; set; }
}
