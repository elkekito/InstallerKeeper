using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using InstallerKeeper.Models;

namespace InstallerKeeper.Services;

public sealed record GitHubReleaseDetails(string Version, string InstallerUrl, string Sha256, string DetailsText);

public sealed class GitHubReleaseException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Reads the latest public GitHub release for an explicitly configured repository.</summary>
public sealed class GitHubReleaseClient
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private static readonly Regex RepositoryPartPattern = new("^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Sha256Pattern = new("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task<GitHubReleaseDetails> GetLatestReleaseAsync(
        string repositoryUrl,
        InstallerEntry installer,
        CancellationToken cancellationToken = default)
    {
        var (owner, repository) = ParseRepositoryUrl(repositoryUrl);
        var apiUri = new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases/latest");
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUri);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("InstallerKeeper/1.0");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new GitHubReleaseException("GitHub no encontró un lanzamiento público reciente para ese repositorio.");
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new GitHubReleaseException("GitHub redirigió la consulta. Revisa que la URL del repositorio sea la actual.");
            if (!response.IsSuccessStatusCode)
                throw new GitHubReleaseException($"GitHub respondió con el estado HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
                throw new GitHubReleaseException("La respuesta de GitHub supera el límite de lectura permitido.");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var body = await ReadBoundedAsync(stream, timeout.Token);
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            return ReadRelease(document.RootElement, owner, repository, installer);
        }
        catch (GitHubReleaseException)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubReleaseException("La consulta a GitHub superó el límite de 20 segundos.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or UriFormatException)
        {
            throw new GitHubReleaseException("No se pudo leer el último lanzamiento de GitHub.", ex);
        }
    }

    private static (string Owner, string Repository) ParseRepositoryUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new GitHubReleaseException("Escribe una URL HTTPS de un repositorio público de github.com.");

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2)
            throw new GitHubReleaseException("La URL debe tener el formato https://github.com/propietario/repositorio.");

        var owner = Uri.UnescapeDataString(parts[0]);
        var repository = Uri.UnescapeDataString(parts[1]);
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repository = repository[..^4];
        if (!RepositoryPartPattern.IsMatch(owner) || !RepositoryPartPattern.IsMatch(repository))
            throw new GitHubReleaseException("La URL del repositorio contiene caracteres no válidos.");
        return (owner, repository);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
                return destination.ToArray();
            if (destination.Length + read > MaximumResponseBytes)
                throw new GitHubReleaseException("La respuesta de GitHub supera el límite de lectura permitido.");
            destination.Write(buffer, 0, read);
        }
    }

    private static GitHubReleaseDetails ReadRelease(JsonElement release, string owner, string repository, InstallerEntry installer)
    {
        var version = GetString(release, "tag_name", 256);
        if (string.IsNullOrWhiteSpace(version))
            throw new GitHubReleaseException("El lanzamiento de GitHub no publica una etiqueta de versión.");

        var releaseUrl = GetString(release, "html_url", 2048);
        if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out var parsedReleaseUrl)
            || parsedReleaseUrl.Scheme != Uri.UriSchemeHttps
            || !parsedReleaseUrl.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            releaseUrl = $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases/latest";

        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new GitHubReleaseException("El lanzamiento de GitHub no contiene una lista de instaladores.");

        var requiredExtension = "." + installer.PackageType.Trim().TrimStart('.');
        var candidates = new List<(string Name, string Url, string Sha256)>();
        foreach (var asset in assets.EnumerateArray().Take(128))
        {
            var name = GetString(asset, "name", 256);
            var downloadUrl = GetString(asset, "browser_download_url", 2048);
            if (!Path.GetExtension(name).Equals(requiredExtension, StringComparison.OrdinalIgnoreCase)
                || !IsGitHubReleaseAssetUrl(downloadUrl, owner, repository))
                continue;

            var digest = GetString(asset, "digest", 80);
            var sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : string.Empty;
            if (!Sha256Pattern.IsMatch(sha256))
                sha256 = string.Empty;
            candidates.Add((name, downloadUrl, sha256));
        }

        var selected = SelectAsset(candidates, installer.Architecture, requiredExtension);
        var releaseName = GetString(release, "name", 256);
        var details = $"Fuente: GitHub Releases\nRepositorio: https://github.com/{owner}/{repository}\nLanzamiento: {releaseName}\nEtiqueta: {version}\nArchivo: {selected.Name}\nPágina: {releaseUrl}";
        return new GitHubReleaseDetails(version, selected.Url, selected.Sha256, details);
    }

    private static (string Name, string Url, string Sha256) SelectAsset(
        List<(string Name, string Url, string Sha256)> candidates,
        string architecture,
        string extension)
    {
        if (candidates.Count == 0)
            throw new GitHubReleaseException($"El último lanzamiento no contiene un archivo {extension}. Configura una URL HTTPS directa en Fuente si el repositorio publica el instalador en otro lugar.");
        if (candidates.Count == 1)
            return candidates[0];

        var requestedArchitecture = NormalizeArchitecture(architecture);
        if (requestedArchitecture.Length > 0)
        {
            var exactMatches = candidates
                .Where(candidate => GetAssetArchitecture(candidate.Name) == requestedArchitecture)
                .ToArray();
            if (exactMatches.Length == 1)
                return exactMatches[0];
        }

        throw new GitHubReleaseException($"GitHub publicó {candidates.Count} archivos {extension} posibles. Para evitar elegir uno incorrecto, configura una URL HTTPS directa del instalador en Fuente.");
    }

    private static bool IsGitHubReleaseAssetUrl(string value, string owner, string repository) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.Port == 443
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath.StartsWith($"/{owner}/{repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    private static string GetAssetArchitecture(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        if (Regex.IsMatch(stem, "(?<![a-z0-9])(arm64|aarch64)(?![a-z0-9])", RegexOptions.CultureInvariant)) return "arm64";
        if (Regex.IsMatch(stem, "(?<![a-z0-9])(x64|x86_64|amd64|win64)(?![a-z0-9])", RegexOptions.CultureInvariant)) return "x64";
        if (Regex.IsMatch(stem, "(?<![a-z0-9])(x86|i386|i686|win32)(?![a-z0-9])", RegexOptions.CultureInvariant)) return "x86";
        return string.Empty;
    }

    private static string NormalizeArchitecture(string value)
    {
        var lower = value.ToLowerInvariant();
        if (lower.Contains("arm64", StringComparison.Ordinal) || lower.Contains("aarch64", StringComparison.Ordinal)) return "arm64";
        if (lower.Contains("x64", StringComparison.Ordinal) || lower.Contains("x86_64", StringComparison.Ordinal) || lower.Contains("amd64", StringComparison.Ordinal)) return "x64";
        if (lower.Contains("x86", StringComparison.Ordinal) || lower.Contains("win32", StringComparison.Ordinal)) return "x86";
        return string.Empty;
    }

    private static string GetString(JsonElement element, string propertyName, int maximumLength)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { } value
            || value.Length > maximumLength)
            return string.Empty;

        return value.Trim();
    }
}
