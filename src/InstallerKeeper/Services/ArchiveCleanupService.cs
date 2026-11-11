using System.IO;
using System.Text.RegularExpressions;

namespace InstallerKeeper.Services;

public sealed record ArchivedItem
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required long FileSizeBytes { get; init; }
    public required string FormattedSize { get; init; }
    public required DateTime LastModified { get; init; }
    public required string AppGroup { get; init; }
    public required string InferredVersion { get; init; }
    public bool WillBePurged { get; set; }
}

public sealed class ArchiveScanResult
{
    public string ArchiveRootPath { get; init; } = string.Empty;
    public bool Exists { get; init; }
    public IReadOnlyList<ArchivedItem> AllItems { get; init; } = Array.Empty<ArchivedItem>();
    public int TotalFiles => AllItems.Count;
    public long TotalBytes => AllItems.Sum(i => i.FileSizeBytes);
    public string FormattedTotalSize => FormatSize(TotalBytes);

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB";
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0):0.#} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:0} KB";
        return $"{bytes} B";
    }
}

public sealed record ArchivePurgePlan
{
    public required IReadOnlyList<ArchivedItem> ItemsToPurge { get; init; }
    public required IReadOnlyList<ArchivedItem> ItemsToKeep { get; init; }
    public int FilesToPurgeCount => ItemsToPurge.Count;
    public long BytesToFree => ItemsToPurge.Sum(i => i.FileSizeBytes);
    public string FormattedBytesToFree => ArchiveScanResult.FormatSize(BytesToFree);
}

public sealed record ArchivePurgeResult(int DeletedFilesCount, long BytesFreed, IReadOnlyList<string> Errors);

public static class ArchiveCleanupService
{
    private static readonly Regex VersionPattern = new(
        @"(?<!\d)v?(\d+(?:\.\d+){1,3}(?:[-+][0-9A-Za-z.-]+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static ArchiveScanResult ScanArchive(string archiveRootPath)
    {
        if (string.IsNullOrWhiteSpace(archiveRootPath) || !Directory.Exists(archiveRootPath))
        {
            return new ArchiveScanResult
            {
                ArchiveRootPath = archiveRootPath,
                Exists = false,
                AllItems = Array.Empty<ArchivedItem>()
            };
        }

        var fullRoot = Path.GetFullPath(archiveRootPath);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        var items = new List<ArchivedItem>();
        foreach (var file in Directory.EnumerateFiles(fullRoot, "*", options)
            .Where(path => InstallerScanner.IsSupportedExtension(Path.GetExtension(path))))
        {
            try
            {
                var fi = new FileInfo(file);
                var fileName = fi.Name;
                var fileNameNoExt = Path.GetFileNameWithoutExtension(fileName);
                var inferredVersion = VersionPattern.Match(fileNameNoExt).Groups[1].Value;
                var appGroup = CleanAppName(VersionPattern.Replace(fileNameNoExt, " "));
                if (string.IsNullOrWhiteSpace(appGroup))
                    appGroup = fileNameNoExt;

                items.Add(new ArchivedItem
                {
                    FilePath = file,
                    FileName = fileName,
                    FileSizeBytes = fi.Length,
                    FormattedSize = ArchiveScanResult.FormatSize(fi.Length),
                    LastModified = fi.LastWriteTime,
                    AppGroup = appGroup,
                    InferredVersion = string.IsNullOrWhiteSpace(inferredVersion) ? "-" : inferredVersion
                });
            }
            catch
            {
                // Ignorar archivos que no se puedan leer
            }
        }

        return new ArchiveScanResult
        {
            ArchiveRootPath = fullRoot,
            Exists = true,
            AllItems = items.OrderBy(i => i.AppGroup, StringComparer.CurrentCultureIgnoreCase)
                            .ThenByDescending(i => i.LastModified)
                            .ToArray()
        };
    }

    public static ArchivePurgePlan BuildPlan(ArchiveScanResult scanResult, int keepCount)
    {
        if (scanResult.AllItems.Count == 0)
        {
            return new ArchivePurgePlan
            {
                ItemsToPurge = Array.Empty<ArchivedItem>(),
                ItemsToKeep = Array.Empty<ArchivedItem>()
            };
        }

        var purgeList = new List<ArchivedItem>();
        var keepList = new List<ArchivedItem>();

        if (keepCount <= 0)
        {
            // Purgar todos los archivos
            foreach (var item in scanResult.AllItems)
            {
                item.WillBePurged = true;
                purgeList.Add(item);
            }
        }
        else
        {
            var groups = scanResult.AllItems
                .GroupBy(i => i.AppGroup, StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                var sorted = group.OrderByDescending(i => i.LastModified).ToList();
                for (int i = 0; i < sorted.Count; i++)
                {
                    if (i < keepCount)
                    {
                        sorted[i].WillBePurged = false;
                        keepList.Add(sorted[i]);
                    }
                    else
                    {
                        sorted[i].WillBePurged = true;
                        purgeList.Add(sorted[i]);
                    }
                }
            }
        }

        return new ArchivePurgePlan
        {
            ItemsToPurge = purgeList,
            ItemsToKeep = keepList
        };
    }

    public static ArchivePurgeResult ExecutePurge(IReadOnlyList<ArchivedItem> itemsToPurge, string archiveRootPath)
    {
        var fullRoot = Path.GetFullPath(archiveRootPath);
        var rootPrefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        var errors = new List<string>();
        int deletedCount = 0;
        long bytesFreed = 0;

        foreach (var item in itemsToPurge)
        {
            try
            {
                var fullFilePath = Path.GetFullPath(item.FilePath);
                if (!fullFilePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Ruta fuera de la carpeta de archivo omitida: {item.FileName}");
                    continue;
                }

                if (File.Exists(fullFilePath))
                {
                    var length = item.FileSizeBytes;
                    File.Delete(fullFilePath);
                    deletedCount++;
                    bytesFreed += length;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"No se pudo eliminar {item.FileName}: {ex.Message}");
            }
        }

        // Limpiar directorios vacíos dentro de la raíz del archivo
        try
        {
            CleanEmptyDirectories(fullRoot);
        }
        catch
        {
            // Limpieza de carpetas opcional
        }

        return new ArchivePurgeResult(deletedCount, bytesFreed, errors);
    }

    private static void CleanEmptyDirectories(string startLocation)
    {
        foreach (var directory in Directory.GetDirectories(startLocation))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                continue;

            CleanEmptyDirectories(directory);
            if (Directory.GetFiles(directory).Length == 0 && Directory.GetDirectories(directory).Length == 0)
            {
                try { Directory.Delete(directory, false); } catch { }
            }
        }
    }

    private static string CleanAppName(string value)
    {
        var cleaned = Regex.Replace(value, @"[._-]+", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned;
    }
}
