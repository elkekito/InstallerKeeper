using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Controls;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using InstallerKeeper.Models;
using InstallerKeeper.Services;

namespace InstallerKeeper;

public partial class MainWindow
{
    private async void DownloadAll_Click(object sender, RoutedEventArgs e)
    {
        var entries = Installers.Where(CanDownload).ToArray();
        if (entries.Length == 0)
        {
            StatusMessage = "No hay aplicaciones listas para descargar. Comprueba las versiones e identifica los paquetes pendientes.";
            return;
        }

        SetDownloadControlsEnabled(false);
        var completed = 0;
        var failures = new List<string>();
        var retainedDownloads = new List<string>();
        var pendingPromotions = new List<(InstallerEntry Entry, DownloadResult Result)>();
        try
        {
            foreach (var entry in entries)
            {
                entry.Status = "Descargando…";
                InstallersView.Refresh();
                StatusMessage = $"Descargando {completed + 1} de {entries.Length}: {entry.Name}…";
                OperationProgressBar.Visibility = Visibility.Visible;
                OperationProgressBar.IsIndeterminate = true;
                DownloadProgressText.Text = string.Empty;
                try
                {
                    var result = await DownloadEntryAsync(entry, CreateDownloadProgress());
                    entry.Status = GetDownloadStatus(result);
                    completed++;
                    OperationProgressBar.IsIndeterminate = false;
                    OperationProgressBar.Value = completed * 100d / entries.Length;
                    if (CanPromoteDownload(result))
                        pendingPromotions.Add((entry, result));
                    else
                    {
                        retainedDownloads.Add($"{entry.Name}: {result.Warning ?? result.Authenticode.Message}");
                        ShowDownloadWarning("Descarga en preparación", BuildDownloadWarningText(result));
                    }
                }
                catch (Exception ex)
                {
                    entry.Status = "Error de descarga";
                    failures.Add($"{entry.Name}: {ex.Message}");
                }
                InstallersView.Refresh();
            }

            StatusMessage = $"Descarga finalizada: {completed} de {entries.Length}; errores: {failures.Count}. Los archivos válidos están pendientes de revisión o promoción.";

            foreach (var (entry, result) in pendingPromotions)
                await OfferPromotionAsync(entry, result);

            if (_settings.SuppressDownloadWarnings && (failures.Count > 0 || retainedDownloads.Count > 0))
            {
                var details = failures.Concat(retainedDownloads);
                StatusMessage = $"Descarga finalizada ({completed}/{entries.Length}). {string.Join(" · ", details)}";
            }

            if (failures.Count > 0 && !_settings.SuppressDownloadWarnings)
                MessageBox.Show(this, string.Join(Environment.NewLine, failures), "Errores de descarga", MessageBoxButton.OK, MessageBoxImage.Error);
            else if (failures.Count > 0)
                StatusMessage = $"Descarga finalizada con {failures.Count} error(es): {string.Join(" · ", failures)}";
        }
        finally
        {
            OperationProgressBar.Visibility = Visibility.Collapsed;
            OperationProgressBar.Value = 0;
            SetDownloadControlsEnabled(true);
        }
    }

    private void ShowDownloadWarning(string title, string message)
    {
        if (_settings.SuppressDownloadWarnings)
        {
            StatusMessage = $"Descarga en preparación: {message.Replace(Environment.NewLine, " ").Trim()}";
            return;
        }

        var dialog = new Window
        {
            Title = title,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = (System.Windows.Media.Brush)FindResource("PanelBrush")
        };
        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
        });
        var suppress = new System.Windows.Controls.CheckBox
        {
            Content = "Ocultar todos los mensajes de descarga y promoción",
            Margin = new Thickness(0, 18, 0, 14)
        };
        content.Children.Add(suppress);
        var accept = new System.Windows.Controls.Button
        {
            Content = "Aceptar",
            IsDefault = true,
            MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 7, 16, 7)
        };
        accept.Click += (_, _) => dialog.Close();
        content.Children.Add(accept);
        dialog.Content = content;
        dialog.ShowDialog();
        if (suppress.IsChecked == true)
        {
            _settings.SuppressDownloadWarnings = true;
            try { _settingsStore.Save(_settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                StatusMessage = $"No se pudo guardar la preferencia de avisos: {ex.Message}";
            }
        }
    }

    private IProgress<DownloadProgress> CreateDownloadProgress() =>
        new Progress<DownloadProgress>(progress =>
        {
            if (progress.TotalBytes is > 0)
            {
                OperationProgressBar.IsIndeterminate = false;
                OperationProgressBar.Value = Math.Clamp(progress.BytesReceived * 100d / progress.TotalBytes.Value, 0, 100);
                DownloadProgressText.Text = $"{OperationProgressBar.Value:0}%";
            }
            else
            {
                OperationProgressBar.IsIndeterminate = true;
                DownloadProgressText.Text = $"{progress.BytesReceived / (1024d * 1024d):0.0} MB";
            }
        });

    private static string BuildDownloadWarningText(DownloadResult result) =>
        $"El paquete quedó en preparación y no se ejecutó.\n\nArchivo: {Path.GetFileName(result.FilePath)}\nTamaño: {result.BytesWritten:N0} bytes\nSHA-256: {result.Sha256}\n{(result.OfficialHashVerified ? "Coincide con el SHA-256 publicado por la fuente oficial.\n" : string.Empty)}Firma: {result.Authenticode.Message}\n{(string.IsNullOrWhiteSpace(result.Warning) ? string.Empty : $"\nAviso: {result.Warning}\n")}No se puede promover hasta completar las comprobaciones de seguridad requeridas.";

    private async void DownloadOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not InstallerEntry entry)
            return;
        if (!CanDownload(entry))
        {
            var reason = entry.IsExcluded
                ? "Esta aplicación está excluida de las comprobaciones. Actívala en Opciones de aplicación para descargarla."
                : entry.Status != InstallerStatus.NewVersion || string.IsNullOrWhiteSpace(entry.OfficialVersion)
                    ? "Primero pulsa «Comprobar todas» y confirma que esta aplicación tiene una nueva versión."
                    : string.IsNullOrWhiteSpace(GetDownloadSourceUrl(entry))
                        ? "La fuente oficial no publicó una URL directa del instalador. Configura una URL HTTPS directa en Opciones de aplicación > Fuente."
                        : "No se encontró la ruta del instalador local. Vuelve a analizar la carpeta de instaladores.";
            StatusMessage = reason;
            return;
        }

        SetDownloadControlsEnabled(false);
        OperationProgressBar.Visibility = Visibility.Visible;
        OperationProgressBar.IsIndeterminate = true;
        OperationProgressBar.Value = 0;
        DownloadProgressText.Text = string.Empty;
        entry.Status = "Descargando…";
        InstallersView.Refresh();
        try
        {
            var result = await DownloadEntryAsync(entry, CreateDownloadProgress());
            entry.Status = GetDownloadStatus(result);
            StatusMessage = $"Descarga guardada para revisión: {Path.GetFileName(result.FilePath)}";
            if (CanPromoteDownload(result))
            {
                await OfferPromotionAsync(entry, result);
            }
            else
            {
                ShowDownloadWarning("Descarga en preparación", BuildDownloadWarningText(result));
            }
        }
        catch (Exception ex)
        {
            entry.Status = "Error de descarga";
            StatusMessage = $"No se pudo descargar {entry.Name}: {ex.Message}";
            if (!_settings.SuppressDownloadWarnings)
                MessageBox.Show(this, ex.Message, "Error de descarga", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            InstallersView.Refresh();
            OperationProgressBar.Visibility = Visibility.Collapsed;
            OperationProgressBar.Value = 0;
            SetDownloadControlsEnabled(true);
        }
    }

    private async Task<DownloadResult> DownloadEntryAsync(InstallerEntry entry, IProgress<DownloadProgress>? progress = null)
    {
        var localPath = entry.LocalFilePath
            ?? throw new InvalidOperationException("No se encontró la ruta del instalador local.");
        var sourceUrl = GetDownloadSourceUrl(entry)
            ?? throw new InvalidOperationException("La fuente oficial no publicó una URL directa y no hay una URL alternativa configurada.");
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri))
            throw new InvalidOperationException("La fuente seleccionada no es una URL válida.");
        var extension = Path.GetExtension(sourceUri.AbsolutePath);
        if (!InstallerScanner.IsSupportedExtension(extension))
            throw new InvalidOperationException($"La URL oficial apunta a un formato no compatible ({extension}).");
        var outputName = _settings.DownloadNameFormat == "WinGet"
            ? DownloadFileNaming.BuildDownloadFileName(sourceUri, localPath, entry.OfficialVersion, extension)
            : DownloadFileNaming.BuildApplicationVersionFileName(entry, extension);
        var hashMatchesConfiguredUrl = SameHttpResource(entry.OfficialInstallerUrl, sourceUrl);
        var expectedSha256 = hashMatchesConfiguredUrl ? entry.OfficialSha256 : null;
        var result = await _downloadClient.DownloadAsync(
            sourceUrl,
            GetStagingDirectory(),
            outputName,
            extension,
            expectedSha256,
            progress);

        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.Warning))
            warnings.Add(result.Warning);
        if (!string.IsNullOrWhiteSpace(entry.OfficialSha256) && !hashMatchesConfiguredUrl)
            warnings.Add("La fuente oficial publicó un hash para otra URL; no se usó para validar esta descarga.");
        if (result.Authenticode.Status is AuthenticodeStatus.Valid or AuthenticodeStatus.RevocationUnavailable)
        {
            if (string.IsNullOrWhiteSpace(result.Authenticode.SignerSubject)
                || string.IsNullOrWhiteSpace(entry.OfficialPublisher))
            {
                warnings.Add("No se pudo comparar exactamente el nombre del firmante con el editor indicado por la fuente oficial.");
            }
            else if (!string.Equals(result.Authenticode.SignerSubject.Trim(), entry.OfficialPublisher.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"El firmante Authenticode ({result.Authenticode.SignerSubject}) no coincide exactamente con el editor oficial ({entry.OfficialPublisher}).");
            }
        }
        var downloadedMetadata = _metadataReader.Read(result.FilePath);
        if (!string.IsNullOrWhiteSpace(downloadedMetadata.ProductVersion)
            && VersionComparer.TryCompare(downloadedMetadata.ProductVersion, entry.OfficialVersion, out var comparison)
            && comparison != 0)
            warnings.Add($"El archivo declara la versión {downloadedMetadata.ProductVersion}; la fuente oficial informó {entry.OfficialVersion}.");

        return result with { Warning = warnings.Count == 0 ? null : string.Join(" ", warnings) };
    }

    private async Task OfferPromotionAsync(InstallerEntry entry, DownloadResult result)
    {
        var policy = GetEffectiveDownloadPolicy(entry);
        var localPath = entry.LocalFilePath!;
        var localExtension = Path.GetExtension(localPath);
        var downloadedExtension = Path.GetExtension(result.FilePath);
        var formatChanged = !localExtension.Equals(downloadedExtension, StringComparison.OrdinalIgnoreCase);
        var targetDirectory = Path.GetDirectoryName(localPath)!;
        var archiveDirectory = policy == "Archive"
            ? GetArchiveDirectory(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(_settings.InstallerFolderPath)),
                targetDirectory)
            : null;
        var downloadFileName = Path.GetFileName(result.FilePath);
        var nameChanged = !Path.GetFileName(localPath).Equals(downloadFileName, StringComparison.OrdinalIgnoreCase);
        var suggestedPath = policy == "KeepAll"
            ? GetUniquePath(targetDirectory, downloadFileName)
            : formatChanged || nameChanged
                ? Path.Combine(targetDirectory, downloadFileName)
                : localPath;
        if (policy != "KeepAll" && !string.Equals(suggestedPath, localPath, StringComparison.OrdinalIgnoreCase)
            && (File.Exists(suggestedPath) || Directory.Exists(suggestedPath)))
            suggestedPath = GetUniquePath(targetDirectory, downloadFileName);
        var formatDescription = formatChanged
            ? $"Formato recibido: {downloadedExtension} (el instalador actual es {localExtension}). La descarga conservará su extensión real.\n"
            : string.Empty;
        var nameDescription = nameChanged
            ? $"Nombre del paquete nuevo: {downloadFileName}\n"
            : string.Empty;
        var backupDescription = policy switch
        {
            "Archive" => $"El archivo anterior se conservará en la carpeta: {archiveDirectory}",
            "Replace" => "El archivo anterior se sustituirá después de guardar la copia nueva.",
            _ => "La versión anterior se conservará en su ubicación actual."
        };
        var warningText = string.IsNullOrWhiteSpace(result.Warning)
            ? "Avisos: ninguno."
            : $"Avisos para revisar: {result.Warning}";
        var verificationText = result.Authenticode.Status == AuthenticodeStatus.Valid
            ? "La firma Authenticode y la revocación de toda la cadena se validaron."
            : "El hash SHA-256 coincide con la fuente oficial; Authenticode no aplica a este ZIP.";
        // Sin confirmación si el usuario ocultó todos los mensajes de descarga y promoción, o si activó la
        // promoción automática y la descarga no tiene avisos. En ambos casos el paquete ya superó
        // CanPromoteDownload (firma y revocación válidas, o ZIP con SHA-256 oficial coincidente).
        var autoPromote = _settings.SuppressDownloadWarnings
            || (_settings.AutoPromoteDownloads && string.IsNullOrWhiteSpace(result.Warning));
        if (!autoPromote)
        {
            var answer = MessageBox.Show(this,
                $"{verificationText}\n\n{formatDescription}{nameDescription}Archivo preparado: {result.FilePath}\nDestino: {suggestedPath}\nPolítica: {policy}\n{backupDescription}\nSHA-256: {result.Sha256}\n{warningText}\n\n¿Promover este instalador ahora? No se ejecutará.",
                "Promover actualización",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                StatusMessage = "La descarga permanece en la carpeta de preparación.";
                return;
            }
        }
        var autoSuffix = autoPromote ? " (automática)" : string.Empty;

        try
        {
            var promotion = await Task.Run(() => PromoteDownloadedFile(entry, result, policy, suggestedPath));
            await RefreshInstallersAsync();
            var preferencesSaveFailed = PreservePreferencesAfterPromotion(entry, promotion.InstalledPath);
            StatusMessage = promotion.ArchivedPreviousPath is null
                ? $"Instalador promovido{autoSuffix}: {Path.GetFileName(promotion.InstalledPath)}"
                : $"Instalador promovido{autoSuffix}; versión anterior archivada en {promotion.ArchivedPreviousPath}";
            if (preferencesSaveFailed && _settings.SuppressDownloadWarnings)
                StatusMessage += ". No se pudieron guardar las opciones asociadas; vuelve a guardarlas desde Opciones de aplicación.";
            if (autoPromote && _settings.SuppressDownloadWarnings && !string.IsNullOrWhiteSpace(result.Warning))
                StatusMessage += $" Avisos: {result.Warning}";
            CheckFreshnessText.Text = "Comprobar todas: resultados vigentes";
            SidebarFreshnessText.Text = "Ahora";
            UpdateEmptyState();
            if (autoPromote && !_settings.SuppressDownloadWarnings)
                MessageBox.Show(this,
                    $"{verificationText}\n\nDescarga completada y promovida automáticamente.\n\n{formatDescription}{nameDescription}Instalador: {promotion.InstalledPath}\n{(promotion.ArchivedPreviousPath is null ? backupDescription : $"Versión anterior archivada en: {promotion.ArchivedPreviousPath}")}\nSHA-256: {result.Sha256}\n{warningText}",
                    "Descarga completada",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            StatusMessage = "No se pudo promover el instalador; se conserva en preparación.";
            StatusMessage += $" Detalle: {ex.Message}";
            if (!_settings.SuppressDownloadWarnings)
                MessageBox.Show(this,
                    $"No se pudo promover el archivo. Revisa que siga en preparación y que Windows no mantenga un bloqueo temporal sobre el archivo actual o el preparado.\n\n{ex.Message}",
                    "Promoción no completada",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
        }
    }

    private PromotionResult PromoteDownloadedFile(InstallerEntry entry, DownloadResult result, string policy, string suggestedPath)
    {
        if (!CanPromoteDownload(result))
            throw new InvalidOperationException("Solo se pueden promover archivos con firma y revocación de cadena confirmadas o ZIP cuyo SHA-256 oficial coincida.");

        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_settings.InstallerFolderPath));
        var stagingPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(GetStagingDirectory()));
        var stagedFilePath = Path.GetFullPath(result.FilePath);
        var localFilePath = Path.GetFullPath(entry.LocalFilePath
            ?? throw new InvalidOperationException("No se encontró la ruta del instalador local."));
        var localDirectory = Path.GetDirectoryName(localFilePath)!;
        var suggestedFullPath = Path.GetFullPath(suggestedPath);
        var stagedExtension = Path.GetExtension(stagedFilePath);
        var rootPrefix = Path.EndsInDirectorySeparator(rootPath)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
        var stagingPrefix = stagingPath + Path.DirectorySeparatorChar;
        if (!stagedFilePath.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(stagedFilePath), stagingPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("El archivo preparado no está directamente dentro de la carpeta de descargas de InstallerKeeper.");
        if (!localFilePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(stagedFilePath)
            || !File.Exists(localFilePath))
            throw new InvalidOperationException("El archivo preparado o el instalador actual ya no está en la carpeta supervisada.");
        if (!string.Equals(Path.GetDirectoryName(suggestedFullPath), localDirectory, StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(suggestedFullPath).Equals(stagedExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("El destino de promoción no coincide con la carpeta supervisada o con el formato descargado.");
        if ((File.GetAttributes(stagedFilePath) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(localFilePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("No se promoverán archivos que sean puntos de reanálisis.");
        const string unsafeDirectoryMessage = "No se promoverá el archivo porque una carpeta de instaladores, preparación, destino o sus directorios ascendentes es un punto de reanálisis.";
        InstallerPathGuard.EnsureNoReparsePointInExistingAncestors(stagingPath, unsafeDirectoryMessage);
        InstallerPathGuard.EnsureNoReparsePointInExistingAncestors(rootPath, unsafeDirectoryMessage);
        InstallerPathGuard.EnsureNoReparsePointInExistingAncestors(localDirectory, unsafeDirectoryMessage);
        InstallerPathGuard.EnsureNoReparsePointInExistingAncestors(Path.GetDirectoryName(suggestedFullPath)!, unsafeDirectoryMessage);
        VerifyStagedFileHash(stagedFilePath, result.Sha256);
        var destinationChanged = !string.Equals(suggestedFullPath, localFilePath, StringComparison.OrdinalIgnoreCase);
        var promotionTempPath = Path.Combine(localDirectory, $".InstallerKeeper-promo-{Guid.NewGuid():N}{stagedExtension}");
        try
        {
            File.Copy(stagedFilePath, promotionTempPath, overwrite: false);
            VerifyStagedFileHash(promotionTempPath, result.Sha256);
            if (policy == "KeepAll")
            {
                File.Move(promotionTempPath, suggestedFullPath);
                TryDeleteFile(stagedFilePath);
                return new PromotionResult(suggestedFullPath, null);
            }

            if (policy == "Archive")
            {
                var archiveDirectory = GetArchiveDirectory(rootPath, localDirectory);
                InstallerPathGuard.EnsureNoReparsePointInExistingAncestors(archiveDirectory,
                    "La carpeta de archivo o uno de sus directorios ascendentes es un punto de reanálisis.");
                Directory.CreateDirectory(archiveDirectory);
                InstallerPathGuard.EnsureNoReparsePointInExistingAncestors(archiveDirectory,
                    "La carpeta de archivo o uno de sus directorios ascendentes es un punto de reanálisis.");
                var archivePath = GetUniquePath(archiveDirectory, Path.GetFileName(localFilePath));
                if (destinationChanged)
                {
                    CopyFileVerified(localFilePath, archivePath);
                    try
                    {
                        File.Delete(localFilePath);
                        File.Move(promotionTempPath, suggestedFullPath);
                    }
                    catch (Exception moveException) when (moveException is IOException or UnauthorizedAccessException)
                    {
                        if (File.Exists(localFilePath))
                        {
                            TryDeleteFile(archivePath);
                            throw;
                        }
                        try
                        {
                            CopyFileVerified(archivePath, localFilePath);
                            TryDeleteFile(archivePath);
                        }
                        catch (Exception restoreException) when (restoreException is IOException or UnauthorizedAccessException)
                        {
                            throw new InvalidOperationException(
                                $"No se pudo completar el cambio de formato ni restaurar el instalador anterior. Se conserva en: {archivePath}",
                                new AggregateException(moveException, restoreException));
                        }
                        throw;
                    }

                    TryDeleteFile(stagedFilePath);
                    return new PromotionResult(suggestedFullPath, archivePath);
                }

                var localBackupPath = Path.Combine(localDirectory, $".InstallerKeeper-previous-{Guid.NewGuid():N}.bak");
                File.Replace(promotionTempPath, localFilePath, localBackupPath, ignoreMetadataErrors: true);
                try
                {
                    CopyFileVerified(localBackupPath, archivePath);
                    TryDeleteFile(localBackupPath);
                    TryDeleteFile(stagedFilePath);
                    return new PromotionResult(localFilePath, archivePath);
                }
                catch (Exception archiveException) when (archiveException is IOException or UnauthorizedAccessException)
                {
                    var failedNewPath = Path.Combine(localDirectory, $".InstallerKeeper-unarchived-{Guid.NewGuid():N}.tmp");
                    try
                    {
                        File.Replace(localBackupPath, localFilePath, failedNewPath, ignoreMetadataErrors: true);
                        TryDeleteFile(failedNewPath);
                    }
                    catch (Exception restoreException) when (restoreException is IOException or UnauthorizedAccessException)
                    {
                        throw new InvalidOperationException(
                            $"No se pudo archivar el instalador anterior ni restaurarlo. Se conserva en: {localBackupPath}",
                            new AggregateException(archiveException, restoreException));
                    }
                    throw;
                }
            }

            var replaceBackupPath = Path.Combine(localDirectory, $".InstallerKeeper-replaced-{Guid.NewGuid():N}.bak");
            if (destinationChanged)
            {
                File.Move(localFilePath, replaceBackupPath);
                try
                {
                    File.Move(promotionTempPath, suggestedFullPath);
                }
                catch (Exception moveException) when (moveException is IOException or UnauthorizedAccessException)
                {
                    try { File.Move(replaceBackupPath, localFilePath); }
                    catch (Exception restoreException) when (restoreException is IOException or UnauthorizedAccessException)
                    {
                        throw new InvalidOperationException("No se pudo completar el cambio de nombre ni restaurar el instalador anterior.",
                            new AggregateException(moveException, restoreException));
                    }
                    throw;
                }
            }
            else
            {
                File.Replace(promotionTempPath, localFilePath, replaceBackupPath, ignoreMetadataErrors: true);
            }

            TryDeleteFile(replaceBackupPath);
            TryDeleteFile(stagedFilePath);
            return new PromotionResult(suggestedFullPath, null);
        }
        finally
        {
            TryDeleteFile(promotionTempPath);
        }
    }

    private string GetArchiveDirectory(string rootPath, string localDirectory)
    {
        var configuredPath = string.IsNullOrWhiteSpace(_settings.ArchiveFolderPath)
            ? Path.Combine(rootPath, ".Archive")
            : Path.GetFullPath(_settings.ArchiveFolderPath);
        if (string.Equals(Path.TrimEndingDirectorySeparator(configuredPath), rootPath, StringComparison.OrdinalIgnoreCase))
            configuredPath = Path.Combine(configuredPath, ".Archive");

        var relativeDirectory = Path.GetRelativePath(rootPath, localDirectory);
        return relativeDirectory == "." ? configuredPath : Path.Combine(configuredPath, relativeDirectory);
    }

    private static void VerifyStagedFileHash(string filePath, string expectedHash)
    {
        byte[] actualHash;
        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            actualHash = SHA256.HashData(stream);
        byte[] expectedBytes;
        try { expectedBytes = Convert.FromHexString(expectedHash); }
        catch (FormatException ex) { throw new InvalidOperationException("El SHA-256 registrado para el archivo preparado no es válido.", ex); }
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedBytes))
            throw new InvalidOperationException("El archivo preparado cambió después de validarse; no se promoverá.");
    }

    private static void CopyFileVerified(string sourcePath, string destinationPath)
    {
        var expectedHash = ComputeFileSha256(sourcePath);
        var destinationCreated = false;
        try
        {
            File.Copy(sourcePath, destinationPath, overwrite: false);
            destinationCreated = true;
            var copiedHash = ComputeFileSha256(destinationPath);
            if (!string.Equals(copiedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("La copia de seguridad no coincide con el archivo original.");
        }
        catch
        {
            if (destinationCreated)
                TryDeleteFile(destinationPath);
            throw;
        }
    }

    private static string ComputeFileSha256(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string GetEffectiveDownloadPolicy(InstallerEntry entry) =>
        entry.DownloadPolicyOverride is "Replace" or "Archive"
            ? entry.DownloadPolicyOverride
            : _settings.DownloadPolicy is "Replace" or "Archive" or "KeepAll"
                ? _settings.DownloadPolicy
                : "Replace";

    private static bool CanPromoteDownload(DownloadResult result) =>
        result.Authenticode.Status == AuthenticodeStatus.Valid
        || (result.Authenticode.Status == AuthenticodeStatus.NotApplicable && result.OfficialHashVerified);

    private static string GetUniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        var suffix = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            candidate = Path.Combine(directory,
                $"{Path.GetFileNameWithoutExtension(fileName)} ({suffix++}){Path.GetExtension(fileName)}");
        }
        return candidate;
    }

    private sealed record PromotionResult(string InstalledPath, string? ArchivedPreviousPath);

    private static bool SameHttpResource(string first, string? second) =>
        Uri.TryCreate(first, UriKind.Absolute, out var firstUri)
        && Uri.TryCreate(second, UriKind.Absolute, out var secondUri)
        && firstUri.Scheme == Uri.UriSchemeHttps
        && secondUri.Scheme == Uri.UriSchemeHttps
        && firstUri.Equals(secondUri);

    private static string GetDownloadStatus(DownloadResult result)
    {
        if (result.OfficialHashVerified && result.Authenticode.Status == AuthenticodeStatus.Valid
            && string.IsNullOrWhiteSpace(result.Warning))
            return "Descargado · verificado";
        if (result.OfficialHashVerified && string.IsNullOrWhiteSpace(result.Warning))
            return "Descargado · hash válido";
        return "Descargado · revisar";
    }

    private static string? GetDownloadSourceUrl(InstallerEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.OfficialSourceUrl)
            ? entry.OfficialSourceUrl
            : entry.OfficialInstallerUrl;

    private bool CanDownload(InstallerEntry entry) =>
        !entry.IsExcluded
        && entry.Status == InstallerStatus.NewVersion
        && !string.IsNullOrWhiteSpace(entry.OfficialVersion)
        && !string.IsNullOrWhiteSpace(GetDownloadSourceUrl(entry))
        && !string.IsNullOrWhiteSpace(entry.LocalFilePath);

    private string GetStagingDirectory()
    {
        if (string.IsNullOrWhiteSpace(_settings.StagingFolderPath))
            return Path.Combine(Path.GetTempPath(), "InstallerKeeper", "Downloads");

        return Path.GetFullPath(_settings.StagingFolderPath);
    }

    private static InstallerPreferences ToPreferences(InstallerEntry entry) => new()
    {
        Name = entry.Name,
        WingetId = entry.WingetId,
        WingetIdConfirmed = entry.WingetIdConfirmed,
        GitHubRepositoryUrl = entry.GitHubRepositoryUrl,
        OfficialSourceUrl = entry.OfficialSourceUrl,
        OfficialVersion = entry.OfficialVersion,
        OfficialSha256 = entry.OfficialSha256,
        OfficialInstallerUrl = entry.OfficialInstallerUrl,
        OfficialPublisher = entry.OfficialPublisher,
        OfficialDetailsText = entry.OfficialDetailsText,
        Architecture = entry.Architecture,
        Channel = entry.Channel,
        Notes = entry.Notes,
        DownloadPolicyOverride = entry.DownloadPolicyOverride,
        IsExcluded = entry.IsExcluded,
        IsHidden = entry.IsHidden
    };

    private bool PreservePreferencesAfterPromotion(InstallerEntry source, string installedPath)
    {
        var promoted = Installers.FirstOrDefault(entry =>
            string.Equals(entry.LocalFilePath, installedPath, StringComparison.OrdinalIgnoreCase));
        if (promoted is null)
            return false;

        var newKey = string.IsNullOrWhiteSpace(promoted.PreferenceKey)
            ? InstallerPreferenceKey.Create(promoted)
            : promoted.PreferenceKey;
        promoted.PreferenceKey = newKey;
        promoted.Name = source.Name;
        promoted.WingetId = source.WingetId;
        promoted.WingetIdConfirmed = source.WingetIdConfirmed;
        promoted.GitHubRepositoryUrl = source.GitHubRepositoryUrl;
        promoted.OfficialSourceUrl = source.OfficialSourceUrl;
        promoted.OfficialVersion = source.OfficialVersion;
        promoted.CurrentVersion = source.OfficialVersion;
        promoted.OfficialSha256 = source.OfficialSha256;
        promoted.OfficialInstallerUrl = source.OfficialInstallerUrl;
        promoted.OfficialPublisher = source.OfficialPublisher;
        promoted.OfficialDetailsText = source.OfficialDetailsText;
        promoted.Architecture = source.Architecture;
        promoted.Channel = source.Channel;
        promoted.Notes = source.Notes;
        promoted.DownloadPolicyOverride = source.DownloadPolicyOverride;
        promoted.IsExcluded = source.IsExcluded;
        promoted.IsHidden = source.IsHidden;
        promoted.Status = InstallerStatus.UpToDate;

        _settings.InstallerPreferences[newKey] = ToPreferences(promoted);
        try
        {
            _settingsStore.Save(_settings);
            InstallersView.Refresh();
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            if (!_settings.SuppressDownloadWarnings)
                MessageBox.Show(this,
                    $"El instalador se promovió, pero no se pudieron guardar sus opciones asociadas. Se volverán a aplicar si guardas las opciones de esta aplicación.\n\n{ex.Message}",
                    "Opciones no guardadas",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            InstallersView.Refresh();
            return true;
        }
    }


    private void SetDownloadControlsEnabled(bool enabled)
    {
        RefreshListButton.IsEnabled = enabled;
        DownloadAllButton.IsEnabled = enabled;
        CheckAllButton.IsEnabled = enabled;
        OptionsButton.IsEnabled = enabled;
        ApplicationsGrid.IsEnabled = enabled;
        SearchBox.IsEnabled = enabled;
        WorkPanelFilters.IsEnabled = enabled;
        SidebarPanel.IsEnabled = enabled;
        SidebarOptionsButton.IsEnabled = enabled;
    }

}
