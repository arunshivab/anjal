using Anjal.Acme;
using Anjal.Api.Dto;

namespace Anjal.Api.Endpoints;

/// <summary>
/// Endpoint handlers for <c>/api/acme</c>. The API process is not
/// necessarily the one running renewal, so status is read from the
/// store's <c>status.json</c> and certificate files, and a renewal is
/// requested by dropping the marker the renewal service polls for.
/// </summary>
public sealed class AcmeHandler
{
    private readonly CertificateStore store;

    /// <summary>Construct.</summary>
    /// <param name="store">Certificate store shared with the renewal host.</param>
    public AcmeHandler(CertificateStore store)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    /// <summary><c>GET /api/acme</c> - certificate and renewal status.</summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task GetStatusAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        AcmeStatus? status = this.store.ReadStatus();
        CertificateMetadata? meta = this.store.LoadMetadata();
        using System.Security.Cryptography.X509Certificates.X509Certificate2? cert = this.store.LoadCertificate();

        var response = new AcmeStatusResponse
        {
            Directory = this.store.Directory,
            HasCertificate = cert is not null,
            Subject = cert?.Subject ?? string.Empty,
            NotBefore = cert?.NotBefore.ToUniversalTime(),
            NotAfter = cert?.NotAfter.ToUniversalTime(),
            DaysRemaining = cert is null ? null : (int)System.Math.Floor((cert.NotAfter.ToUniversalTime() - System.DateTime.UtcNow).TotalDays),
            Domains = meta?.Domains ?? System.Array.Empty<string>(),
            KeyType = meta?.KeyType ?? string.Empty,
            IssuedAt = meta?.IssuedAt,
            AcmeDirectoryUrl = status?.DirectoryUrl ?? meta?.DirectoryUrl ?? string.Empty,
            RenewalPending = System.IO.File.Exists(this.store.RenewRequestPath),
            LastAttemptAt = status?.LastAttemptAt,
            LastAttemptSucceeded = status?.LastAttemptSucceeded ?? false,
            LastError = status?.LastError ?? string.Empty,
            ConsecutiveFailures = status?.ConsecutiveFailures ?? 0,
            NextCheckAt = status?.NextCheckAt,
            StatusUpdatedAt = status?.UpdatedAt,
        };
        await ctx.WriteJsonAsync(200, response).ConfigureAwait(false);
    }

    /// <summary><c>POST /api/acme/renew</c> - request an immediate renewal.</summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task RenewAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        try
        {
            this.store.RequestRenewal();
        }
        catch (System.IO.IOException ex)
        {
            await ctx.WriteErrorAsync(500, "io_error", ex.Message).ConfigureAwait(false);
            return;
        }
        catch (System.UnauthorizedAccessException ex)
        {
            await ctx.WriteErrorAsync(500, "io_error", ex.Message).ConfigureAwait(false);
            return;
        }
        await ctx.WriteJsonAsync(202, new AcmeRenewResponse
        {
            Requested = true,
            Marker = this.store.RenewRequestPath,
        }).ConfigureAwait(false);
    }
}
