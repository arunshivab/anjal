using System.Collections.Concurrent;
using Anjal.Store;

namespace Anjal.Mailbox;

/// <summary>
/// rc.25 (DES-15 §5.2): mailboxes' public keys by address, for the Maildir store and the delivery
/// sink. What is known of an address is asked again after a minute - "no key yet" (so mail arriving
/// after a person's first sign-in is sealed) and a key alike (an address deleted and given to a new
/// mailbox has another key).
/// </summary>
public sealed class MailboxKeyDirectory : IMaildirKeyDirectory
{
    private static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(1);
    private readonly IMailboxStore boxes;
    private readonly IMailKeyStore keys;
    private readonly ConcurrentDictionary<string, (byte[]? Key, DateTimeOffset At)> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="MailboxKeyDirectory"/> class.</summary>
    /// <param name="boxes">The mailbox store.</param>
    /// <param name="keys">The key store.</param>
    public MailboxKeyDirectory(IMailboxStore boxes, IMailKeyStore keys)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        ArgumentNullException.ThrowIfNull(keys);
        this.boxes = boxes;
        this.keys = keys;
    }

    /// <inheritdoc/>
    public async Task<byte[]?> PublicKeyAsync(string tenantSlug, string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (this.cache.TryGetValue(address, out (byte[]? Key, DateTimeOffset At) hit) && DateTimeOffset.UtcNow - hit.At < KeptFor)
        {
            return hit.Key;
        }
        byte[]? key = null;
        if (MailboxSink.TrySplitAddress(address, out string local, out string domain) &&
            await this.boxes.GetMailboxAsync(local, domain, ct).ConfigureAwait(false) is MailboxRow box)
        {
            key = (await this.keys.GetPublicKeysAsync(new[] { box.Id }, ct).ConfigureAwait(false)).TryGetValue(box.Id, out byte[]? k) ? k : null;
        }
        this.cache[address] = (key, DateTimeOffset.UtcNow);
        return key;
    }

    /// <summary>Forget what is known of an address (its keys were just made in this process).</summary>
    /// <param name="address">The address.</param>
    public void Forget(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        this.cache.TryRemove(address, out _);
    }
}
