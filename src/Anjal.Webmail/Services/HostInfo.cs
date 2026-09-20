namespace Anjal.Webmail.Services;

/// <summary>
/// The mail host name this webmail belongs to, shown under the wordmark
/// on the sign-in page so a user can tell which server they are signing
/// in to.
/// </summary>
public sealed class HostInfo
{
    /// <summary>Construct.</summary>
    /// <param name="name">Host name, e.g. <c>mail.anjal.co.in</c>.</param>
    public HostInfo(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        this.Name = name;
    }

    /// <summary>The host name.</summary>
    public string Name { get; }
}
