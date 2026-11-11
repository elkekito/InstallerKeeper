using System.IO;
using InstallerKeeper.Models;

namespace InstallerKeeper.Services;

internal static class InstallerPreferenceKey
{
    public static string Create(InstallerEntry installer)
    {
        var packageType = installer.PackageType.Trim().ToUpperInvariant();
        var publisher = Normalize(installer.Publisher);
        var name = Normalize(installer.Name);

        // Prefer the MSI product family when available, before the per-release ProductCode.
        if (packageType == "MSI" && (publisher.Length > 0 || name.Length > 0))
            return $"MSI:product:{publisher}|{name}";

        if (!string.IsNullOrWhiteSpace(installer.PackageIdentity))
            return $"{packageType}:identity:{installer.PackageIdentity.Trim()}";

        if (publisher.Length > 0 && name.Length > 0)
            return $"{packageType}:product:{publisher}|{name}";

        if (!string.IsNullOrWhiteSpace(installer.LocalFilePath))
        {
            try
            {
                return $"{packageType}:path:{Path.GetFullPath(installer.LocalFilePath).ToUpperInvariant()}";
            }
            catch (ArgumentException)
            {
                // Fall through to a filename key if the local path is malformed.
            }
            catch (IOException)
            {
                // Fall through to a filename key if the local path cannot be normalized.
            }
        }

        return $"{packageType}:name:{name}";
    }

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();
}
