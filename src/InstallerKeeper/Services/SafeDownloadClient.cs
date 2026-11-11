using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace InstallerKeeper.Services;

internal sealed record DownloadResult(
    string FilePath,
    long BytesWritten,
    string Sha256,
    bool OfficialHashVerified,
    AuthenticodeVerificationResult Authenticode,
    string? Warning = null);

internal readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes);

/// <summary>
/// Downloads a direct HTTPS link selected from official package metadata or a user-configured override to a staging directory. It never
/// replaces an existing installer or starts the downloaded file.
/// </summary>
internal sealed class SafeDownloadClient
{
    private const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private static readonly TimeSpan DnsResolutionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ResponseHeadersTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ResponseReadTimeout = TimeSpan.FromMinutes(2);

    public async Task<DownloadResult> DownloadAsync(
        string sourceUrl,
        string stagingDirectory,
        string outputFileName,
        string requiredExtension,
        string? expectedSha256 = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        expectedSha256 = string.IsNullOrWhiteSpace(expectedSha256) ? null : expectedSha256.Trim();
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("La fuente configurada no es una URL válida.");

        ValidateHttpsUri(uri);
        if (!Path.GetExtension(uri.AbsolutePath).Equals(requiredExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"La URL debe apuntar directamente a un archivo {requiredExtension}.");
        if (!string.IsNullOrWhiteSpace(expectedSha256)
            && !System.Text.RegularExpressions.Regex.IsMatch(expectedSha256, "^[A-Fa-f0-9]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidOperationException("El SHA-256 oficial configurado no tiene un formato válido.");

        const string unsafeDirectoryMessage = "La carpeta de instaladores, preparación o alguno de sus directorios ascendentes es un punto de reanálisis.";
        InstallerPathGuard.EnsureNoReparsePointInExistingAncestors(stagingDirectory, unsafeDirectoryMessage);
        Directory.CreateDirectory(stagingDirectory);
        InstallerPathGuard.EnsureNoReparsePointInExistingAncestors(stagingDirectory, unsafeDirectoryMessage);
        if ((File.GetAttributes(stagingDirectory) & FileAttributes.Directory) == 0)
            throw new InvalidOperationException("La ruta de preparación no es una carpeta.");

        var destinationPath = GetUniquePath(stagingDirectory, outputFileName);
        var temporaryPath = Path.Combine(stagingDirectory, $".{Guid.NewGuid():N}.download");

        try
        {
            // Se usa el proxy configurado en Windows. Con proxy, la conexión TCP va al proxy y el destino lo decide
            // el proxy; solo se acepta un proxy de red privada si es el host del proxy del sistema, no otros destinos.
            var trustedProxyHost = GetSystemProxyHost(uri);
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                ConnectCallback = (context, token) => ConnectToPublicHostAsync(context, trustedProxyHost, token)
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await SendFollowingHttpsRedirectsAsync(client, uri, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;

            if (!ResponseIdentifiesInstaller(response, requiredExtension))
                throw new InvalidOperationException($"La respuesta final no identifica un instalador {requiredExtension}; comprueba la URL y el nombre enviado por el servidor.");

            if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
                throw new InvalidOperationException("El archivo supera el límite de descarga de 2 GiB.");

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("La URL devolvió una página web, no un instalador directo.");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(ResponseReadTimeout);
            await using var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long bytesWritten = 0;
            long lastReportedPercent = -1;
            long lastReportedMegabytes = -1;
            while (true)
            {
                var read = await input.ReadAsync(buffer, readTimeout.Token);
                readTimeout.CancelAfter(ResponseReadTimeout);
                if (read == 0)
                    break;

                bytesWritten += read;
                if (bytesWritten > MaxDownloadBytes)
                    throw new InvalidOperationException("El archivo supera el límite de descarga de 2 GiB.");

                hasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (totalBytes is > 0)
                {
                    var percent = Math.Clamp(bytesWritten * 100 / totalBytes.Value, 0, 100);
                    if (percent != lastReportedPercent)
                    {
                        progress?.Report(new DownloadProgress(bytesWritten, totalBytes));
                        lastReportedPercent = percent;
                    }
                }
                else
                {
                    var megabytes = bytesWritten / (1024 * 1024);
                    if (megabytes != lastReportedMegabytes)
                    {
                        progress?.Report(new DownloadProgress(bytesWritten, null));
                        lastReportedMegabytes = megabytes;
                    }
                }
            }

            if (bytesWritten == 0)
                throw new InvalidOperationException("La fuente devolvió un archivo vacío.");
            progress?.Report(new DownloadProgress(bytesWritten, totalBytes));

            await output.FlushAsync(cancellationToken);
            await output.DisposeAsync();
            var sha256 = Convert.ToHexString(hasher.GetHashAndReset());
            var officialHashVerified = expectedSha256 is not null;
            if (officialHashVerified
                && !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(sha256),
                    Convert.FromHexString(expectedSha256!.Trim())))
                throw new InvalidOperationException("El SHA-256 descargado no coincide con el hash oficial de la fuente seleccionada. El archivo temporal se ha descartado.");

            var authenticode = await Task.Run(() => AuthenticodeVerifier.Verify(temporaryPath), cancellationToken);
            if (authenticode.Status == AuthenticodeStatus.Invalid)
                throw new InvalidOperationException($"{authenticode.Message} El archivo temporal se ha descartado.");

            File.Move(temporaryPath, destinationPath);
            var warnings = new List<string>();
            if (!officialHashVerified)
                warnings.Add("La fuente oficial seleccionada no proporcionó un SHA-256 aplicable a esta URL.");
            if (authenticode.Status == AuthenticodeStatus.NoEmbeddedSignature)
                warnings.Add("No se encontró una firma Authenticode embebida; revisa el archivo manualmente.");
            if (authenticode.Status == AuthenticodeStatus.RevocationUnavailable)
                warnings.Add("La firma es válida, pero no se confirmó la revocación de toda la cadena. El archivo queda solo en preparación y no debe promocionarse.");
            if (authenticode.Status == AuthenticodeStatus.NotApplicable)
                warnings.Add(authenticode.Message);

            return new DownloadResult(destinationPath, bytesWritten, sha256, officialHashVerified, authenticode,
                warnings.Count == 0 ? null : string.Join(" ", warnings));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw new TimeoutException("La fuente dejó de responder antes de completar la descarga.", ex);
        }
        catch (TimeoutException ex)
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw new TimeoutException("Se agotó el tiempo de espera al resolver el servidor de descarga.", ex);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
    }

    private static async Task<HttpResponseMessage> SendFollowingHttpsRedirectsAsync(
        HttpClient client,
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        for (var redirectCount = 0; ; redirectCount++)
        {
            ValidateHttpsUri(currentUri);
            await EnsurePublicHostAsync(currentUri, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headersTimeout.CancelAfter(ResponseHeadersTimeout);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token);
            if (!IsRedirect(response.StatusCode))
                return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirectCount >= MaxRedirects)
                throw new InvalidOperationException("La URL devolvió una redirección no válida o superó el límite permitido.");

            currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
        }
    }

    private static bool ResponseIdentifiesInstaller(HttpResponseMessage response, string requiredExtension)
    {
        var finalUriExtension = Path.GetExtension(response.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty);
        if (finalUriExtension.Equals(requiredExtension, StringComparison.OrdinalIgnoreCase))
            return true;

        var disposition = response.Content.Headers.ContentDisposition;
        return HasExpectedExtension(disposition?.FileNameStar, requiredExtension)
            || HasExpectedExtension(disposition?.FileName, requiredExtension);
    }

    private static bool HasExpectedExtension(string? fileName, string requiredExtension)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var unquotedName = fileName.Trim().Trim('"');
        string decodedName;
        try
        {
            decodedName = Uri.UnescapeDataString(unquotedName);
        }
        catch (UriFormatException)
        {
            return false;
        }

        var leafName = decodedName[(Math.Max(decodedName.LastIndexOf('/'), decodedName.LastIndexOf('\\')) + 1)..];
        return Path.GetExtension(leafName).Equals(requiredExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateHttpsUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.Port != 443)
            throw new InvalidOperationException("Solo se permiten enlaces HTTPS públicos en el puerto estándar, sin credenciales en la URL.");
    }

    private static async Task EnsurePublicHostAsync(Uri uri, CancellationToken cancellationToken)
    {
        _ = await ResolvePublicHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
    }

    // Host del proxy del sistema que se usa para este destino, o null si no hay proxy o el destino va directo.
    private static string? GetSystemProxyHost(Uri uri)
    {
        var proxyUri = HttpClient.DefaultProxy.GetProxy(uri);
        return proxyUri is null || proxyUri.Host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase)
            ? null
            : proxyUri.Host;
    }

    private static async ValueTask<Stream> ConnectToPublicHostAsync(
        SocketsHttpConnectionContext context,
        string? trustedProxyHost,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = string.Equals(host, trustedProxyHost, StringComparison.OrdinalIgnoreCase)
            ? await Dns.GetHostAddressesAsync(host, cancellationToken)
            : await ResolvePublicHostAddressesAsync(host, cancellationToken);
        SocketException? lastConnectionError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastConnectionError = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException(
            "No se pudo conectar a ninguna de las direcciones públicas validadas.",
            lastConnectionError);
    }

    private static async Task<IPAddress[]> ResolvePublicHostAddressesAsync(
        string host,
        CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(host, out var literalAddress)
            ? new[] { literalAddress }
            : await Dns.GetHostAddressesAsync(host, cancellationToken)
                .WaitAsync(DnsResolutionTimeout, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new InvalidOperationException("La URL no puede apuntar a una dirección privada/local ni resolver a hosts no públicos.");

        return addresses;
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return false;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] != 0
                && bytes[0] != 10
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
                && !(bytes[0] == 198 && bytes[1] is 18 or 19)
                && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
                && !(bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99)
                && !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                && bytes[0] < 224;
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6
            || (bytes[0] & 0xE0) != 0x20)
            return false;

        var isIpv6Documentation = bytes[0] == 0x20 && bytes[1] == 0x01
            && bytes[2] == 0x0D && bytes[3] == 0xB8;
        var isIpv6IetfProtocolAssignments = bytes[0] == 0x20 && bytes[1] == 0x01
            && bytes[2] == 0x00 && (bytes[3] & 0xFE) == 0;
        var isIpv6SixToFour = bytes[0] == 0x20 && bytes[1] == 0x02;
        var isIpv6DocumentationAlternate = bytes[0] == 0x3F && bytes[1] == 0xFF
            && (bytes[2] & 0xF0) == 0;
        return !isIpv6Documentation && !isIpv6IetfProtocolAssignments
            && !isIpv6SixToFour && !isIpv6DocumentationAlternate;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static string GetUniquePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        var suffix = 2;
        while (File.Exists(path))
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            path = Path.Combine(directory, $"{name} ({suffix++}){extension}");
        }

        return path;
    }
}
