namespace InstallerKeeper.Models;

/// <summary>
/// Raw facts read from an installer container or executable. These are scan signals,
/// not a confirmed application identification.
/// </summary>
public sealed class PackageMetadata
{
    public string ProductName { get; init; } = string.Empty;
    public string ProductVersion { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public string PackageIdentity { get; init; } = string.Empty;
    public string Warning { get; init; } = string.Empty;

    /// <summary>False when reading failed in a way that may not repeat (file locked, partial download), so it must not be cached.</summary>
    public bool IsCacheable { get; init; } = true;
}
