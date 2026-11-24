namespace Anjal.Webmail.Services;

/// <summary>
/// rc.30 (D-366): runs one piece of work once for a key and hands every later caller for the same key that same work,
/// finished or still running. A streamed page can set a component's parameters again while its first run is still
/// waiting (found on macOS CI, 24 Nov 2026: one click on Show patient details read and logged the details twice, 0.8 s
/// apart). The key is taken before the first wait, so a second run that overlaps the first finds it.
/// </summary>
/// <typeparam name="T">What the work gives.</typeparam>
internal sealed class OnceFor<T>
{
    private string? key;
    private Task<T>? work;

    /// <summary>The work for this key: the one already started for it, or a new one.</summary>
    /// <param name="forKey">What the work is for.</param>
    /// <param name="start">Starts the work; called at most once for each key in a row.</param>
    /// <returns>The work.</returns>
    public Task<T> Run(string forKey, Func<Task<T>> start)
    {
        ArgumentNullException.ThrowIfNull(forKey);
        ArgumentNullException.ThrowIfNull(start);
        if (this.work is not null && string.Equals(this.key, forKey, StringComparison.Ordinal))
        {
            return this.work;
        }
        this.key = forKey;
        this.work = start();
        return this.work;
    }
}
