using System.IO;
using System.Text.RegularExpressions;
using InstallerKeeper.Models;

namespace InstallerKeeper.Services;

public sealed class InstallerScanner
{
    private readonly PackageMetadataReader _metadataReader = new();
    private readonly ScanCacheStore _cacheStore = new();
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".zip"
    };

    public static bool IsSupportedExtension(string extension) => SupportedExtensions.Contains(extension);

    private static readonly Regex VersionPattern = new(
        @"(?<!\d)v?(\d+(?:\.\d+){1,3}(?:[-+][0-9A-Za-z.-]+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public IReadOnlyList<InstallerEntry> Scan(string folderPath, bool includeSubdirectories, IEnumerable<string>? excludedFolders = null)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return Array.Empty<InstallerEntry>();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubdirectories,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        var normalizedRootPath = Path.GetFullPath(folderPath);
        var normalizedExcludedFolders = (excludedFolders ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(path => IsInside(normalizedRootPath, path))
            .ToArray();

        var previousCache = _cacheStore.Load();
        var currentCache = new Dictionary<string, CachedInstaller>(StringComparer.OrdinalIgnoreCase);
        var entries = Directory.EnumerateFiles(folderPath, "*", options)
            .Where(path => !IsInInstallerKeeperInternalFolder(folderPath, path))
            .Where(path => !normalizedExcludedFolders.Any(excluded => IsInside(excluded, path)))
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .Select(path => CreateEntry(path, previousCache, currentCache))
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.LocalFilePath, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        // Only files seen in this scan stay cached; save when something was added, changed or removed.
        var cacheUnchanged = previousCache.Count == currentCache.Count
            && currentCache.All(pair => previousCache.TryGetValue(pair.Key, out var old) && ReferenceEquals(old, pair.Value));
        if (!cacheUnchanged)
            _cacheStore.Save(currentCache);

        return entries;
    }

    private PackageMetadata ReadMetadata(
        string filePath,
        IReadOnlyDictionary<string, CachedInstaller> previousCache,
        Dictionary<string, CachedInstaller> currentCache)
    {
        long length;
        long lastWriteTicks;
        string key;
        try
        {
            var info = new FileInfo(filePath);
            length = info.Length;
            lastWriteTicks = info.LastWriteTimeUtc.Ticks;
            key = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return _metadataReader.Read(filePath);
        }

        if (previousCache.TryGetValue(key, out var cached)
            && cached.Metadata is not null
            && cached.Length == length
            && cached.LastWriteUtcTicks == lastWriteTicks)
        {
            currentCache[key] = cached;
            return cached.Metadata;
        }

        var metadata = _metadataReader.Read(filePath);
        if (metadata.IsCacheable)
            currentCache[key] = new CachedInstaller { Length = length, LastWriteUtcTicks = lastWriteTicks, Metadata = metadata };
        return metadata;
    }

    private static bool IsInside(string directoryPath, string filePath)
    {
        var relativePath = Path.GetRelativePath(directoryPath, Path.GetFullPath(filePath));
        return relativePath != "."
            && !Path.IsPathRooted(relativePath)
            && relativePath != ".."
            && !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool IsInInstallerKeeperInternalFolder(string rootPath, string filePath) =>
        Path.GetRelativePath(rootPath, filePath)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals(".InstallerKeeperDownloads", StringComparison.OrdinalIgnoreCase)
                || segment.Equals(".InstallerKeeperArchive", StringComparison.OrdinalIgnoreCase)
                || segment.Equals(".Archive", StringComparison.OrdinalIgnoreCase)
                || segment.Equals(".InstallerKeeperPromotion", StringComparison.OrdinalIgnoreCase));

    private InstallerEntry CreateEntry(
        string filePath,
        IReadOnlyDictionary<string, CachedInstaller> previousCache,
        Dictionary<string, CachedInstaller> currentCache)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var extension = Path.GetExtension(filePath);
        var inferredName = CleanName(VersionPattern.Replace(fileName, " "));
        var inferredVersion = VersionPattern.Match(fileName).Groups[1].Value;
        var metadata = ReadMetadata(filePath, previousCache, currentCache);

        var version = FirstNonEmpty(metadata.ProductVersion, inferredVersion);
        var entry = new InstallerEntry
        {
            Name = FirstNonEmpty(metadata.ProductName, inferredName, fileName),
            CurrentVersion = version,
            Status = string.IsNullOrWhiteSpace(version) ? "Versión desconocida" : "Sin comprobar",
            LocalFilePath = filePath,
            PackageType = extension.TrimStart('.').ToUpperInvariant(),
            Publisher = metadata.Publisher,
            Architecture = metadata.Architecture,
            PackageIdentity = metadata.PackageIdentity,
            ScanWarning = metadata.Warning
        };
        entry.PreferenceKey = InstallerPreferenceKey.Create(entry);
        return entry;
    }

    private static string CleanName(string value)
    {
        var cleaned = Regex.Replace(value, @"[._-]+", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
