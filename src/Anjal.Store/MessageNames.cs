namespace Anjal.Store;

/// <summary>
/// A message's sender and recipients as they arrived, and when it arrived: what a list put in
/// order by name needs (owner, 9 Oct 2026).
/// </summary>
/// <param name="Id">The message.</param>
/// <param name="FromHeader">Its From header, as stored.</param>
/// <param name="ToHeader">Its To header, as stored.</param>
/// <param name="EnvelopeFrom">The envelope sender, for a message without a From header.</param>
/// <param name="ReceivedAt">When it arrived.</param>
public sealed record MessageNames(System.Guid Id, string FromHeader, string ToHeader, string EnvelopeFrom, System.DateTimeOffset ReceivedAt);
