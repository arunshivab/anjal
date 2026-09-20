namespace Anjal.Api.Dto;

/// <summary>One probed component in <c>/healthz</c>.</summary>
public sealed class HealthComponent
{
    /// <summary>Component name: store, maildir, tls.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary><c>ok</c>, <c>degraded</c> or <c>down</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Short human-readable detail.</summary>
    public string Detail { get; set; } = string.Empty;
}

/// <summary>Response body for <c>GET /healthz</c>.</summary>
public sealed class HealthResponse
{
    /// <summary>Worst component status: <c>ok</c>, <c>degraded</c> or <c>down</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>API module version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Seconds since the API server started.</summary>
    public long UptimeSeconds { get; set; }

    /// <summary>When the probes ran (UTC).</summary>
    public System.DateTimeOffset CheckedAt { get; set; }

    /// <summary>Per-component results.</summary>
    public System.Collections.Generic.IList<HealthComponent> Components { get; set; } = new System.Collections.Generic.List<HealthComponent>();
}
