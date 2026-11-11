using System.IO;
using System.Text.Json;
using InstallerKeeper.Models;

namespace InstallerKeeper.Services;

/// <summary>Metadata read from one installer, valid while its size and modification time do not change.</summary>
public sealed class CachedInstaller
{
    public long Length { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public PackageMetadata? Metadata { get; set; }
}

public sealed class ScanCacheDocument
{
    public int FormatVersion { get; set; }
    public string AppVersion { get; set; } = string.Empty;
    public Dictionary<string, CachedInstaller> Files { get; set; } = new();
}

/// <summary>
/// Persists the metadata read from each installer so unchanged files are not inspected again on the next scan.
/// The cache is optional: any read or write problem just means the files are inspected again.
/// </summary>
internal sealed class ScanCacheStore
{
    // Raise when PackageMetadataReader changes what it extracts; AppInfo.Version also invalidates the cache.
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new();

    public string CachePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InstallerKeeper",
        "scan-cache.json");

    public Dictionary<string, CachedInstaller> Load()
    {
        var empty = new Dictionary<string, CachedInstaller>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(CachePath))
                return empty;

            var document = JsonSerializer.Deserialize<ScanCacheDocument>(File.ReadAllText(CachePath), JsonOptions);
            if (document is null || document.FormatVersion != FormatVersion || document.AppVersion != AppInfo.Version)
                return empty;

            return new Dictionary<string, CachedInstaller>(document.Files, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return empty;
        }
    }

    public void Save(Dictionary<string, CachedInstaller> files)
    {
        try
        {
            var directory = Path.GetDirectoryName(CachePath);
            if (directory is null)
                return;

            Directory.CreateDirectory(directory);
            var document = new ScanCacheDocument { FormatVersion = FormatVersion, AppVersion = AppInfo.Version, Files = files };
            var temporaryPath = $"{CachePath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, CachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is only an optimization.
        }
    }
}
