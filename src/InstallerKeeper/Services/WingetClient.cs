using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace InstallerKeeper.Services;

public sealed record WingetPackageDetails(string Id, string Name, string Version, string Publisher, string InstallerUrl, string InstallerSha256, string DetailsText);
public sealed record WingetSearchCandidate(string Name, string Id, string Version, string Source)
{
    public string DisplayText => $"{Name}  ·  {Id}  ·  {Version}";
}

public sealed class WingetClient
{
    private const int MaxCapturedStreamCharacters = 2 * 1024 * 1024;
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromHours(2);
    private static readonly ConcurrentDictionary<string, (WingetPackageDetails Details, DateTime Expiration)> ManifestCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex PackageIdPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._+\-]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AnsiEscapePattern = new(
        "\\x1B\\[[0-?]*[ -/]*[@-~]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static void ClearCache() => ManifestCache.Clear();

    public async Task<WingetPackageDetails> ShowExactAsync(string packageId, bool bypassCache = false, CancellationToken cancellationToken = default)
    {
        var requestedId = packageId.Trim();
        if (!PackageIdPattern.IsMatch(requestedId))
            throw new WingetException("El identificador WinGet contiene caracteres no válidos.");

        if (!bypassCache && ManifestCache.TryGetValue(requestedId, out var cached) && DateTime.UtcNow < cached.Expiration)
        {
            return cached.Details;
        }

        var output = await RunCommandAsync(new[]
        {
            "show", "--id", requestedId, "--exact", "--source", "winget", "--disable-interactivity"
        }, cancellationToken);
        var id = ReadField(output, "Id", "Identificador", "ID");
        if (string.IsNullOrWhiteSpace(id))
            id = ReadIdFromFoundLine(output);
        if (!string.Equals(id, requestedId, StringComparison.OrdinalIgnoreCase))
            throw new WingetException("WinGet no devolvió una coincidencia única con ese identificador exacto.");

        var version = ReadField(output, "Version", "Versión");
        if (string.IsNullOrWhiteSpace(version))
            throw new WingetException("WinGet confirmó el ID del paquete, pero su respuesta no incluyó un campo de versión reconocible.");

        var details = new WingetPackageDetails(
            id,
            ReadField(output, "Name", "Nombre"),
            version,
            ReadField(output, "Publisher", "Editor", "Publicador"),
            ReadField(output, "Installer URL", "URL del instalador", "Dirección URL del instalador"),
            ReadInstallerSha256(output),
            output.Trim());

        ManifestCache[requestedId] = (details, DateTime.UtcNow.Add(DefaultCacheTtl));
        return details;
    }

    public async Task<IReadOnlyList<WingetSearchCandidate>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length is < 2 or > 120 || normalizedQuery.Any(char.IsControl))
            throw new WingetException("Escribe un nombre de al menos 2 caracteres para buscar en WinGet.");

        var output = await RunCommandAsync(new[]
        {
            "search", "--query", normalizedQuery, "--source", "winget", "--disable-interactivity"
        }, cancellationToken);
        return ParseSearchResults(output);
    }

    private static async Task<string> RunCommandAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "winget.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new WingetException("No se pudo iniciar WinGet.");

            var standardOutputTask = ReadBoundedOutputAsync(process.StandardOutput);
            var standardErrorTask = ReadBoundedOutputAsync(process.StandardError);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(standardOutputTask, standardErrorTask);

                if (cancellationToken.IsCancellationRequested)
                    throw;

                throw new WingetException("WinGet no respondió en 45 segundos.");
            }

            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;
            var output = AnsiEscapePattern.Replace(standardOutput.Text, string.Empty);
            var error = AnsiEscapePattern.Replace(standardError.Text, string.Empty);
            if (process.ExitCode != 0)
            {
                var detail = FirstNonEmpty(error.Trim(), output.Trim());
                if (detail.Contains("agreement", StringComparison.OrdinalIgnoreCase)
                    || detail.Contains("acuerdo", StringComparison.OrdinalIgnoreCase))
                {
                    throw new WingetException("WinGet requiere que aceptes sus acuerdos de origen. Abre una terminal y ejecuta `winget source update`; revisa y acepta allí el acuerdo antes de volver a comprobar.");
                }

                throw new WingetException(string.IsNullOrWhiteSpace(detail)
                    ? "WinGet no pudo consultar el origen winget."
                    : FirstLine(detail));
            }

            if (standardOutput.Truncated || standardError.Truncated)
                throw new WingetException("WinGet devolvió una respuesta demasiado extensa (más de 2 Mi caracteres por canal). Reduce la búsqueda e inténtalo de nuevo.");

            // Some WinGet builds write details to stderr, and redirected output
            // may contain UTF-16 NULs or console-only line endings.
            var combinedOutput = string.IsNullOrWhiteSpace(error) ? output : $"{output}\n{error}";
            return CleanConsoleOutput(combinedOutput);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new WingetException("No se encontró WinGet. Instala o habilita el Administrador de paquetes de Windows y vuelve a intentarlo.", ex);
        }
    }

    private static async Task<CapturedOutput> ReadBoundedOutputAsync(StreamReader reader)
    {
        var captured = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) != 0)
        {
            var remaining = MaxCapturedStreamCharacters - captured.Length;
            var charactersToKeep = Math.Min(read, Math.Max(remaining, 0));
            if (charactersToKeep > 0)
                captured.Append(buffer, 0, charactersToKeep);
            if (charactersToKeep < read)
                truncated = true;
        }

        return new CapturedOutput(captured.ToString(), truncated);
    }

    private sealed record CapturedOutput(string Text, bool Truncated);

    private static IReadOnlyList<WingetSearchCandidate> ParseSearchResults(string output)
    {
        var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        for (var headerIndex = 0; headerIndex < lines.Length - 1; headerIndex++)
        {
            var header = lines[headerIndex];
            var tokens = Regex.Matches(header, @"\S+").Cast<Match>().ToArray();
            var name = FindColumn(tokens, "Name", "Nombre");
            var id = FindColumn(tokens, "Id", "ID", "Identificador");
            var version = FindColumn(tokens, "Version", "Versión");
            if (name < 0 || id < 0 || version < 0 || id <= name || version <= id)
                continue;

            var separatorIndex = headerIndex + 1;
            while (separatorIndex < lines.Length && !Regex.IsMatch(lines[separatorIndex], @"^\s*-{3,}\s*$"))
                separatorIndex++;
            if (separatorIndex >= lines.Length)
                continue;

            var matchColumn = FindColumn(tokens, "Match", "Coincidencia");
            var sourceColumn = FindColumn(tokens, "Source", "Origen", "Fuente");
            var versionEnd = matchColumn > version ? tokens[matchColumn].Index : sourceColumn > version ? tokens[sourceColumn].Index : int.MaxValue;
            var idStart = tokens[id].Index;
            var versionStart = tokens[version].Index;
            var candidates = new List<WingetSearchCandidate>();
            for (var rowIndex = separatorIndex + 1; rowIndex < lines.Length; rowIndex++)
            {
                var line = lines[rowIndex];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var candidateId = ReadColumn(line, idStart, versionStart);
                if (!PackageIdPattern.IsMatch(candidateId))
                    continue;

                var candidateName = ReadColumn(line, tokens[name].Index, idStart);
                var candidateVersion = ReadColumn(line, versionStart, versionEnd);
                var candidateSource = sourceColumn >= 0
                    ? ReadColumn(line, tokens[sourceColumn].Index, int.MaxValue)
                    : "winget";
                candidates.Add(new WingetSearchCandidate(candidateName, candidateId, candidateVersion, candidateSource));
            }

            return candidates
                .GroupBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(50)
                .ToArray();
        }

        throw new WingetException("No se pudo interpretar la tabla de resultados de WinGet en este idioma o versión.");
    }

    private static int FindColumn(IReadOnlyList<Match> tokens, params string[] labels) =>
        Enumerable.Range(0, tokens.Count)
            .FirstOrDefault(index => labels.Contains(tokens[index].Value, StringComparer.OrdinalIgnoreCase), -1);

    private static string ReadColumn(string line, int start, int end)
    {
        if (start >= line.Length)
            return string.Empty;

        var boundedEnd = Math.Min(end, line.Length);
        if (boundedEnd <= start)
            return string.Empty;
        return line[start..boundedEnd].Trim();
    }

    private static string ReadField(string output, params string[] labels)
    {
        // WinGet is a console tool and some builds emit carriage-return-only
        // lines when stdout is redirected. Normalize all newline variants.
        var normalizedOutput = CleanConsoleOutput(output);
        foreach (var line in normalizedOutput.Split('\n', StringSplitOptions.None))
        {
            var separator = line.IndexOfAny(':', '：');
            if (separator < 0)
                continue;

            var label = line[..separator].Trim();
            if (labels.Contains(label, StringComparer.OrdinalIgnoreCase)
                || label.Equals("Package Version", StringComparison.OrdinalIgnoreCase)
                || label.Equals("Versión del paquete", StringComparison.OrdinalIgnoreCase))
                return line[(separator + 1)..].Trim();
        }

        return string.Empty;
    }

    private static string ReadInstallerSha256(string output)
    {
        var value = ReadField(output,
            "Installer SHA256", "Installer SHA-256", "Instalador SHA256", "Instalador SHA-256");
        return Regex.IsMatch(value, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)
            ? value.ToUpperInvariant()
            : string.Empty;
    }

    private static string CleanConsoleOutput(string output) =>
        AnsiEscapePattern.Replace(output, string.Empty)
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
            .Replace("\u200B", string.Empty, StringComparison.Ordinal)
            .Replace("\u200E", string.Empty, StringComparison.Ordinal)
            .Replace("\u200F", string.Empty, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u0085', '\n')
            .Replace('\u2028', '\n')
            .Replace('\u2029', '\n');

    private static string ReadIdFromFoundLine(string output)
    {
        foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var openBracket = line.LastIndexOf('[');
            var closeBracket = line.LastIndexOf(']');
            if (openBracket >= 0 && closeBracket > openBracket)
                return line[(openBracket + 1)..closeBracket].Trim();
        }

        return string.Empty;
    }

    private static string FirstLine(string value) =>
        value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}

public sealed class WingetException : Exception
{
    public WingetException(string message) : base(message) { }
    public WingetException(string message, Exception innerException) : base(message, innerException) { }
}
