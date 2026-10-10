using System.Globalization;
using System.Net;
using System.Text;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>
/// DES-11 S2 to S5 (owner, 10 Oct 2026): the person is told by mail of every change to how their
/// account is protected, of a sign-in from a network or device not seen before, and of the second
/// step being locked. The mail goes to their own mailbox and to their recovery address. It says
/// what happened, when (in their own time), where (town and country when known), and from which
/// browser - and what to do if it was not them.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The header every security mail carries, so it is delivered even to a full mailbox (DES-11 D2).</summary>
    public const string SecurityMailHeader = "X-Anjal-Security";

    /// <summary>How long the second step stays locked after five wrong codes (DES-11 S3).</summary>
    public static readonly TimeSpan SecondStepLock = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Mail the person about a security event. Never fails the action that caused it: a mail that
    /// cannot be sent is written to the log.
    /// </summary>
    /// <param name="personId">The person's own mailbox.</param>
    /// <param name="subject">The subject, for example "Your password was changed".</param>
    /// <param name="what">What happened, in one or two sentences.</param>
    /// <param name="client">The network address it came from.</param>
    /// <param name="userAgent">The browser's User-Agent.</param>
    /// <param name="links">Lines to add after the details (for example the two sign-in links); may be empty.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many copies went (own mailbox, recovery address).</returns>
    public async Task<int> SecurityMailAsync(Guid personId, string subject, string what, string client, string? userAgent, IReadOnlyList<string> links, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(what);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(links);
        if (await this.GetContextAsync(personId, ct).ConfigureAwait(false) is not { } context)
        {
            return 0;
        }
        MailboxRow mailbox = context.Mailbox;
        MailboxPreferences prefs = MailboxPreferences.Of(mailbox);
        ZonedClock clock = ZonedClock.For(prefs.TimeZone, prefs.DateFormat);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string place = PlaceOf(client);
        string device = DeviceName.Of(userAgent ?? string.Empty);
        var sb = new StringBuilder();
        sb.Append(what).Append("\r\n\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"When: {clock.Time(now)}, {clock.Date(now)}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"Where: {(place.Length > 0 ? place + " (" + client + ")" : client)}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"Device: {device}\r\n\r\n");
        foreach (string line in links)
        {
            sb.Append(line).Append("\r\n");
        }
        if (links.Count > 0)
        {
            sb.Append("\r\n");
        }
        sb.Append("If this was you, nothing more is needed. If it was not you, change your password at once and tell your administrator.\r\n");
        int sent = 0;
        SecurityDocument doc = await this.GetSecurityAsync(personId, ct).ConfigureAwait(false);
        foreach (string to in new[] { mailbox.Address, doc.RecoveryAddress }.Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? error = await this.SendSecurityMailAsync(mailbox, to, subject, sb.ToString(), ct).ConfigureAwait(false);
            if (error is null)
            {
                sent++;
            }
            else
            {
                Console.Error.WriteLine($"[security mail] not sent to one address: {error}");
            }
        }
        return sent;
    }

    /// <summary>
    /// Note a sign-in for the status bar and say whether it is from a network or a device not seen
    /// before (DES-11 S5). The first sign-in ever is never "new".
    /// </summary>
    /// <param name="personId">The person.</param>
    /// <param name="client">The network address.</param>
    /// <param name="userAgent">The browser's User-Agent.</param>
    /// <param name="newNetwork">True when the network was found new (from <see cref="NoteSignInNetworkAsync"/>).</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when the person should be told.</returns>
    public async Task<bool> NoteSignInAsync(Guid personId, string client, string? userAgent, bool newNetwork, DateTimeOffset now, CancellationToken ct = default)
    {
        SecurityDocument doc = await this.GetSecurityAsync(personId, ct).ConfigureAwait(false);
        string device = DeviceName.Of(userAgent ?? string.Empty);
        bool first = doc.LatestSignIn is null && doc.SeenDevices.Count == 0;
        bool newDevice = !first && !doc.SeenDevices.Contains(device, StringComparer.Ordinal);
        if (!doc.SeenDevices.Contains(device, StringComparer.Ordinal))
        {
            doc.SeenDevices.Add(device);
            while (doc.SeenDevices.Count > 30)
            {
                doc.SeenDevices.RemoveAt(0);
            }
        }
        doc.PreviousSignIn = doc.LatestSignIn;
        doc.LatestSignIn = new SignInMark(now, PlaceOf(client), device);
        await this.WriteDocumentAsync(personId, SecurityKind, doc, ct).ConfigureAwait(false);
        return !first && (newNetwork || newDevice);
    }

    /// <summary>Lock the second step for <see cref="SecondStepLock"/> after five wrong codes (DES-11 S3).</summary>
    /// <param name="personId">The person.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Until when it is locked.</returns>
    public async Task<DateTimeOffset> LockSecondStepAsync(Guid personId, DateTimeOffset now, CancellationToken ct = default)
    {
        SecurityDocument doc = await this.GetSecurityAsync(personId, ct).ConfigureAwait(false);
        doc.SecondStepLockedUntil = now + SecondStepLock;
        await this.WriteDocumentAsync(personId, SecurityKind, doc, ct).ConfigureAwait(false);
        return doc.SecondStepLockedUntil.Value;
    }

    /// <summary>True while the second step is locked (DES-11 S3).</summary>
    /// <param name="personId">The person.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether a second step may be tried now.</returns>
    public async Task<bool> SecondStepLockedAsync(Guid personId, DateTimeOffset now, CancellationToken ct = default) =>
        (await this.GetSecurityAsync(personId, ct).ConfigureAwait(false)).SecondStepLockedUntil is DateTimeOffset until && until > now;

    /// <summary>Town and country of a network address, or empty.</summary>
    /// <param name="client">The address.</param>
    /// <returns>For example "Pune, India".</returns>
    public static string PlaceOf(string client) =>
        IPAddress.TryParse(client, out IPAddress? ip) && !IpLocations.IsPrivate(ip) ? IpLocations.Find(ip)?.Text ?? string.Empty : string.Empty;
}
