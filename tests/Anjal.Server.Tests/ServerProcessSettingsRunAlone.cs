namespace Anjal.Server.Tests;

/// <summary>Tests that change a setting the whole process reads run alone, never beside other tests.</summary>
[CollectionDefinition("ServerProcessSettings", DisableParallelization = true)]
public sealed class ServerProcessSettingsRunAlone
{
}
