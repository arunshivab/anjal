namespace Anjal.Store;

/// <summary>
/// Non-secret settings kept in the database (v1.0.0-rc.7, owner's decision of
/// 27 Sep 2026), so a rebuild from the database backup brings the settings
/// back. Each service calls <see cref="ApplyAsync"/> first thing at start-up:
/// on the first start with an empty table the service's current environment
/// (its <c>/etc/anjal/*.env</c>) is imported once; from then on the stored
/// value of every setting replaces the environment's, and each difference is
/// logged. Secrets - the database connection, passwords, tokens and the KEK -
/// are never stored: they stay in the env files and on the paper custody forms.
/// </summary>
public static class StoredSettings
{
    /// <summary>
    /// True for a setting that must never be stored in the database: the
    /// database connection itself and anything holding a password, token,
    /// secret or key-encryption key.
    /// </summary>
    /// <param name="key">The setting's name.</param>
    /// <returns>True when the setting is secret.</returns>
    public static bool IsSecret(string key)
    {
        System.ArgumentNullException.ThrowIfNull(key);
        string k = key.ToUpperInvariant();
        return k == "ANJAL_POSTGRES"
            || k.StartsWith("ANJAL_POSTGRES_", System.StringComparison.Ordinal)
            || k.Contains("PASSWORD", System.StringComparison.Ordinal)
            || k.Contains("TOKEN", System.StringComparison.Ordinal)
            || k.Contains("SECRET", System.StringComparison.Ordinal)
            || k.Contains("KEK", System.StringComparison.Ordinal);
    }

    /// <summary>True for a name that may be stored: an <c>ANJAL_</c> setting that is not secret.</summary>
    /// <param name="key">The setting's name.</param>
    /// <returns>True when storable.</returns>
    public static bool IsStorable(string key)
    {
        System.ArgumentNullException.ThrowIfNull(key);
        return key.StartsWith("ANJAL_", System.StringComparison.Ordinal) && key.Length > 6 && !IsSecret(key);
    }

    /// <summary>The storable settings in an environment, as rows to import.</summary>
    /// <param name="scope">"server" or "webmail".</param>
    /// <param name="environment">The variables, usually <see cref="System.Environment.GetEnvironmentVariables()"/>.</param>
    /// <returns>One row per storable, non-empty setting, sorted by name.</returns>
    public static IReadOnlyList<SettingRow> FromEnvironment(string scope, System.Collections.IDictionary environment)
    {
        System.ArgumentNullException.ThrowIfNull(scope);
        System.ArgumentNullException.ThrowIfNull(environment);
        var rows = new List<SettingRow>();
        foreach (System.Collections.DictionaryEntry e in environment)
        {
            string key = e.Key?.ToString() ?? string.Empty;
            string value = e.Value?.ToString() ?? string.Empty;
            if (IsStorable(key) && value.Length > 0)
            {
                rows.Add(new SettingRow { Scope = scope, Key = key, Value = value, UpdatedBy = "import" });
            }
        }
        rows.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return rows;
    }

    /// <summary>
    /// Apply the stored settings of <paramref name="scope"/> to this process's
    /// environment, importing the environment once if nothing is stored yet.
    /// Never throws for an unreachable database or a missing table: the
    /// service then runs on its environment, and the log says so.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="scope">"server" or "webmail".</param>
    /// <param name="log">Where to report what happened.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many stored settings were applied, or -1 when the database could not be read.</returns>
    public static async Task<int> ApplyAsync(IMessageStore store, string scope, System.Action<string> log, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(scope);
        System.ArgumentNullException.ThrowIfNull(log);
        IReadOnlyList<SettingRow> rows;
        try
        {
            rows = await store.ListSettingsAsync(scope, ct).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                IReadOnlyList<SettingRow> fromEnv = FromEnvironment(scope, System.Environment.GetEnvironmentVariables());
                int imported = await store.ImportSettingsAsync(fromEnv, ct).ConfigureAwait(false);
                log($"Settings: none stored yet for {scope}; imported {imported} from the environment (secrets excluded). The database is now the source of truth.");
                rows = await store.ListSettingsAsync(scope, ct).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // The service must still start on its environment when the database is unavailable.
        catch (System.Exception ex)
        {
            log($"Settings: could not read them from the database ({ex.Message}); using {EnvironmentName(scope)} only.");
            return -1;
        }
#pragma warning restore CA1031
        int applied = 0;
        foreach (SettingRow row in rows)
        {
            if (!IsStorable(row.Key))
            {
                log($"Settings: ignoring stored {row.Key} - secrets are never taken from the database.");
                continue;
            }
            string? current = System.Environment.GetEnvironmentVariable(row.Key);
            if (current is not null && !string.Equals(current, row.Value, System.StringComparison.Ordinal))
            {
                log($"Settings: {row.Key}: using the database value; {EnvironmentName(scope)} has a different one.");
            }
            System.Environment.SetEnvironmentVariable(row.Key, row.Value);
            applied++;
        }
        var stored = new HashSet<string>(rows.Select(r => r.Key), System.StringComparer.Ordinal);
        foreach (SettingRow envOnly in FromEnvironment(scope, System.Environment.GetEnvironmentVariables()))
        {
            if (!stored.Contains(envOnly.Key))
            {
                log($"Settings: {envOnly.Key} is set only in {EnvironmentName(scope)}, not in the database; the environment value is used. Store it through the admin API to keep it in backups.");
            }
        }
        log($"Settings: {applied} applied from the database ({scope}).");
        return applied;
    }

    /// <summary>
    /// Where the environment came from, in words: the service's settings file on Linux, where the
    /// systemd unit reads it, and the environment it was started with anywhere else (DES-11 F12:
    /// on Windows the messages named a Linux file that does not exist there).
    /// </summary>
    /// <param name="scope">"server" or "webmail".</param>
    /// <returns>The words for the start-up messages.</returns>
    public static string EnvironmentName(string scope) =>
        System.OperatingSystem.IsLinux() ? $"/etc/anjal/{scope}.env" : "the environment this was started with";
}
