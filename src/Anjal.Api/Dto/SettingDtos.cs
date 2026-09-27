namespace Anjal.Api.Dto;

/// <summary>Body of <c>PUT /api/settings/{scope}/{key}</c>.</summary>
public sealed class SettingRequest
{
    /// <summary>The new value.</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>One stored setting.</summary>
public sealed class SettingResponse
{
    /// <summary>"server" or "webmail".</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>The setting's name.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The stored value.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>When it was last changed.</summary>
    public System.DateTimeOffset UpdatedAt { get; set; }

    /// <summary>"import" for the one-time import, "api" for a change through this API.</summary>
    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>Response of <c>GET /api/settings</c>.</summary>
public sealed class SettingsListResponse
{
    /// <summary>The server's settings.</summary>
    public System.Collections.Generic.List<SettingResponse> Server { get; set; } = new();

    /// <summary>The webmail's settings.</summary>
    public System.Collections.Generic.List<SettingResponse> Webmail { get; set; } = new();

    /// <summary>A reminder of when changes take effect.</summary>
    public string Note { get; set; } = "A change takes effect when that service restarts (sudo systemctl restart anjal-server or anjal-webmail). Secrets are never stored here.";
}
