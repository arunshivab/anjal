using System.Runtime.CompilerServices;

namespace Anjal.Server.Tests;

/// <summary>
/// rc.15 (item 35): the tests never call Have I Been Pwned. New passwords are checked against
/// the downloaded list only (none is present in a test run), as an installation with
/// ANJAL_PWNED_MODE=download would. The online check has its own tests, with a stand-in service.
/// </summary>
internal static class NoOnlinePasswordCheck
{
#pragma warning disable CA2255 // Runs once as the test assembly loads: no test may reach the internet.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Set()
    {
        if (Environment.GetEnvironmentVariable("ANJAL_PWNED_MODE") is null)
        {
            Environment.SetEnvironmentVariable("ANJAL_PWNED_MODE", "download");
        }
    }
}
