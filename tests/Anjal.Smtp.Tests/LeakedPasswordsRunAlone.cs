namespace Anjal.Smtp.Tests;

/// <summary>
/// The leaked-password tests change settings every password check reads (ANJAL_PWNED_MODE, the
/// list in use, the online service). Run alone, never beside the other password tests.
/// </summary>
[CollectionDefinition("Leaked passwords", DisableParallelization = true)]
public sealed class LeakedPasswordsRunAlone
{
}
