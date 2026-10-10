namespace Anjal.Store.Tests;

/// <summary>
/// The stored-settings test applies settings to the whole process's environment, as a service
/// does at start-up. Run alone, never beside other tests.
/// </summary>
[CollectionDefinition("Stored settings", DisableParallelization = true)]
public sealed class StoredSettingsRunAlone
{
}
