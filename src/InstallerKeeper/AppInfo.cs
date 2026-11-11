using System.Reflection;

namespace InstallerKeeper;

internal static class AppInfo
{
    public const string Name = "InstallerKeeper";

    // Única fuente de la versión: <InformationalVersion> del .csproj.
    public static readonly string Version = typeof(AppInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    public static readonly string VersionLabel = "Versión " + Version;
}
