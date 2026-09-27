namespace Anjal.Smtp;

/// <summary>What an outbound session does after EHLO.</summary>
public enum TlsAction
{
    /// <summary>Negotiate STARTTLS, then send.</summary>
    StartTls,

    /// <summary>Send without encryption.</summary>
    Plaintext,

    /// <summary>Do not send; the message is held, retried and finally returned to its sender.</summary>
    Hold,
}

/// <summary>
/// The one place that decides whether an outbound message is encrypted, sent
/// in plain text or held (v1.0.0-rc.7). Both senders use it, so the owner's
/// rule - never send mail unencrypted - cannot be applied in one and missed
/// in the other.
/// </summary>
public static class TlsDecision
{
    /// <summary>Decide what to do once the server's EHLO reply is known.</summary>
    /// <param name="mode">The destination's TLS mode (its policy, or the default).</param>
    /// <param name="offered">Whether the server advertised STARTTLS.</param>
    /// <param name="allowPlaintext">Whether plaintext is permitted at all (<see cref="TlsClientOptions.AllowPlaintext"/>).</param>
    /// <param name="loopback">Whether the destination is this machine (a local relay), where nothing crosses a network.</param>
    /// <returns>The action.</returns>
    public static TlsAction Decide(Anjal.Store.TlsMode mode, bool offered, bool allowPlaintext, bool loopback)
    {
        bool plaintextPermitted = allowPlaintext || loopback;
        if (offered)
        {
            // A Disabled policy may skip encryption only where plaintext is permitted.
            return mode == Anjal.Store.TlsMode.Disabled && plaintextPermitted ? TlsAction.Plaintext : TlsAction.StartTls;
        }
        return mode != Anjal.Store.TlsMode.Required && plaintextPermitted ? TlsAction.Plaintext : TlsAction.Hold;
    }
}
