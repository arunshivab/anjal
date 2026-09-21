namespace Anjal.Api;

/// <summary>
/// Metadata for the Anjal.Api module. The module exposes the HTTP/JSON
/// surface that applications use to send mail and manage routing rules.
/// </summary>
public static class ModuleInfo
{
    /// <summary>
    /// The version of this build, read from the compiled assembly, so it is
    /// always the version in Directory.Build.props or the one publish.ps1
    /// stamps - never a string that can go stale. Any "+commit" suffix the
    /// SDK appends is dropped.
    /// </summary>
    public static string Version { get; } =
        (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(ModuleInfo).Assembly)?.InformationalVersion ?? "0.0.0")
            .Split('+')[0];

    /// <summary>The module name.</summary>
    public static string Name => "Anjal.Api";
}
