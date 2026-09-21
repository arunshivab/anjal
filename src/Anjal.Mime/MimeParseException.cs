namespace Anjal.Mime;

/// <summary>
/// A message could not be parsed within Anjal's safety limits: nesting
/// deeper than <see cref="MimeParser.MaxNestingDepth"/> or more parts than
/// <see cref="MimeParser.MaxParts"/>. Derives from
/// <see cref="System.FormatException"/> so every existing caller that
/// handles malformed input handles this too; the raw message is still
/// delivered, it is simply not parsed for display or scoring.
/// </summary>
public sealed class MimeParseException : System.FormatException
{
    /// <summary>Construct.</summary>
    public MimeParseException()
    {
    }

    /// <summary>Construct with a message.</summary>
    /// <param name="message">What was wrong.</param>
    public MimeParseException(string message)
        : base(message)
    {
    }

    /// <summary>Construct with a message and cause.</summary>
    /// <param name="message">What was wrong.</param>
    /// <param name="innerException">The cause.</param>
    public MimeParseException(string message, System.Exception innerException)
        : base(message, innerException)
    {
    }
}
