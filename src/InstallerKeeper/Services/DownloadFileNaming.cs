using System.IO;
using System.Text.RegularExpressions;
using InstallerKeeper.Models;

namespace InstallerKeeper.Services;

/// <summary>
/// Nombres de archivo de las descargas preparadas. Funciones puras, sin acceso a la interfaz, para poder probarlas.
/// </summary>
public static class DownloadFileNaming
{
    private static readonly Regex FileVersionTokenPattern = new(
        @"(?<![A-Za-z0-9])(?<prefix>[vV]?)(?<version>\d+(?:[._]\d+){1,3}(?:[-+][0-9A-Za-z.-]+)?)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string BuildDownloadFileName(Uri sourceUri, string localPath, string officialVersion, string extension)
    {
        var sourceFileName = Uri.UnescapeDataString(Path.GetFileName(sourceUri.AbsolutePath));
        if (Path.GetExtension(sourceFileName).Equals(extension, StringComparison.OrdinalIgnoreCase))
        {
            var safeSourceFileName = SanitizeFileNamePart(sourceFileName);
            if (!string.IsNullOrWhiteSpace(safeSourceFileName))
                return safeSourceFileName;
        }

        var version = SanitizeFileNamePart(officialVersion);
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("No hay una versión oficial válida para nombrar la descarga.");

        var baseName = SanitizeFileNamePart(Path.GetFileNameWithoutExtension(localPath));
        if (version.Length > 64)
            version = version[..64];
        if (baseName.Length > 120)
            baseName = baseName[..120];
        return $"{baseName}-{version}{extension}";
    }

    public static string BuildApplicationVersionFileName(InstallerEntry entry, string extension)
    {
        var officialVersion = SanitizeFileNamePart(entry.OfficialVersion.Trim().TrimStart('v', 'V'));
        if (string.IsNullOrWhiteSpace(officialVersion))
            throw new InvalidOperationException("No hay una versión oficial válida para nombrar la descarga.");

        var localName = Path.GetFileNameWithoutExtension(entry.LocalFilePath ?? string.Empty);
        var currentVersion = entry.CurrentVersion.Trim().TrimStart('v', 'V');
        var updatedName = ReplaceVersionToken(localName, currentVersion, officialVersion);
        if (updatedName is null)
        {
            var tokens = FileVersionTokenPattern.Matches(localName);
            if (tokens.Count > 0)
            {
                var token = tokens[^1];
                updatedName = $"{localName[..token.Index]}{token.Groups["prefix"].Value}{officialVersion}{localName[(token.Index + token.Length)..]}";
            }
        }

        if (string.IsNullOrWhiteSpace(updatedName))
        {
            var applicationName = string.IsNullOrWhiteSpace(entry.Name) ? localName : entry.Name.Trim();
            updatedName = ReplaceVersionToken(applicationName, currentVersion, officialVersion);
            if (updatedName is null)
            {
                var tokens = FileVersionTokenPattern.Matches(applicationName);
                if (tokens.Count > 0)
                {
                    var token = tokens[^1];
                    updatedName = $"{applicationName[..token.Index]}{token.Groups["prefix"].Value}{officialVersion}{applicationName[(token.Index + token.Length)..]}";
                }
                else
                {
                    updatedName = $"{applicationName} v{officialVersion}";
                }
            }
        }

        return $"{SanitizeFileNamePart(updatedName)}{extension}";
    }


    private static string? ReplaceVersionToken(string fileName, string currentVersion, string officialVersion)
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
            return null;

        var pattern = new Regex(
            $@"(?<![A-Za-z0-9])(?<prefix>[vV]?){Regex.Escape(currentVersion)}(?![A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var match = pattern.Match(fileName);
        return match.Success
            ? $"{fileName[..match.Index]}{match.Groups["prefix"].Value}{officialVersion}{fileName[(match.Index + match.Length)..]}"
            : null;
    }


    public static string SanitizeFileNamePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) || char.IsControl(character) ? '-' : character).ToArray());
        return safe.Trim().TrimEnd('.', ' ');
    }
}