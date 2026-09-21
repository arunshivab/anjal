namespace Anjal.Smtp;

/// <summary>
/// Metadata for the Anjal.Smtp module. The module implements RFC 5321
/// SMTP receiver and sender for server-to-server mail transport.
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
    public static string Name => "Anjal.Smtp";
}
