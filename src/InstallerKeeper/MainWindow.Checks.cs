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
    private async void CheckAll_Click(object sender, RoutedEventArgs e)
    {
        await CheckAllAsync();
    }

    private async Task CheckAllAsync()
    {
        var entries = Installers.Where(entry => !entry.IsExcluded).ToArray();
        var identifiedEntries = entries.Where(entry => entry.IsIdentified).ToArray();
        if (identifiedEntries.Length == 0)
        {
            StatusMessage = "No hay aplicaciones identificadas para comprobar.";
            return;
        }

        SetDownloadControlsEnabled(false);
        var checkedCount = 0;
        var failedCount = 0;
        var processedCount = 0;
        OperationProgressBar.Visibility = Visibility.Visible;
        OperationProgressBar.IsIndeterminate = false;
        OperationProgressBar.Value = 0;

        try
        {
            using var semaphore = new SemaphoreSlim(4);
            var tasks = identifiedEntries.Select(async entry =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var success = await CheckEntryAsync(entry, bypassCache: false);
                    if (success)
                        Interlocked.Increment(ref checkedCount);
                    else
                        Interlocked.Increment(ref failedCount);
                }
                finally
                {
                    var currentProcessed = Interlocked.Increment(ref processedCount);
                    Dispatcher.Invoke(() =>
                    {
                        OperationProgressBar.Value = currentProcessed * 100d / identifiedEntries.Length;
                        StatusMessage = $"Comprobando en paralelo ({currentProcessed} de {identifiedEntries.Length}): {entry.Name}…";
                        InstallersView.Refresh();
                    });
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            InstallersView.Refresh();
            StatusMessage = $"Versiones oficiales: {checkedCount} comprobaciones correctas; {failedCount} errores; {entries.Count(entry => !entry.IsHidden && !entry.IsIdentified)} pendientes de identificar.";
            MarkChecksFresh(allApplications: true);
            UpdateEmptyState();
        }
        finally
        {
            OperationProgressBar.Visibility = Visibility.Collapsed;
            OperationProgressBar.Value = 0;
            SetDownloadControlsEnabled(true);
        }
    }

    private async Task<bool> CheckEntryAsync(InstallerEntry entry, bool bypassCache = false)
    {
        if (!entry.IsIdentified)
        {
            entry.Status = "Identificación pendiente";
            return false;
        }

        entry.LastCheckError = string.Empty;
        entry.Status = "Comprobando…";
        InstallersView.Refresh();

        var githubRepository = entry.GitHubRepositoryUrl?.Trim();
        var canUseGitHub = !string.IsNullOrWhiteSpace(githubRepository);
        var canUseWinget = entry.WingetIdConfirmed && !string.IsNullOrWhiteSpace(entry.WingetId);
        if (canUseWinget && canUseGitHub)
            return await CheckBothSourcesAsync(entry, entry.WingetId!, githubRepository!, bypassCache);

        if (canUseWinget)
        {
            StatusMessage = $"Comprobando {entry.Name} con WinGet…";
            try
            {
                var details = await _wingetClient.ShowExactAsync(entry.WingetId!, bypassCache);
                ApplyOfficialDetails(entry, details);
                entry.Status = GetOfficialVersionStatus(entry);
                StatusMessage = $"Comprobación completada con WinGet: {entry.Name}.";
                InstallersView.Refresh();
                return true;
            }
            catch (WingetException ex) when (canUseGitHub)
            {
                StatusMessage = $"WinGet no respondió para {entry.Name}; comprobando GitHub Releases…";
                return await CheckGitHubReleaseAsync(entry, githubRepository!, ex.Message);
            }
            catch (WingetException ex)
            {
                return HandleCheckFailure(entry, ex.Message);
            }
        }

        if (canUseGitHub)
        {
            StatusMessage = $"Comprobando {entry.Name} con GitHub Releases…";
            return await CheckGitHubReleaseAsync(entry, githubRepository!, null);
        }

        return HandleCheckFailure(entry, "No hay un ID WinGet confirmado ni un repositorio de GitHub configurado.");
    }

    private async Task<bool> CheckBothSourcesAsync(InstallerEntry entry, string wingetId, string githubRepository, bool bypassCache = false)
    {
        StatusMessage = $"Comparando versiones de {entry.Name} en WinGet y GitHub Releases…";

        async Task<(WingetPackageDetails? Details, string? Error)> QueryWingetAsync()
        {
            try { return (await _wingetClient.ShowExactAsync(wingetId, bypassCache), null); }
            catch (WingetException ex) { return (null, ex.Message); }
        }

        async Task<(GitHubReleaseDetails? Details, string? Error)> QueryGitHubAsync()
        {
            try { return (await _githubReleaseClient.GetLatestReleaseAsync(githubRepository, entry), null); }
            catch (GitHubReleaseException ex) { return (null, ex.Message); }
        }

        var wingetTask = QueryWingetAsync();
        var githubTask = QueryGitHubAsync();
        await Task.WhenAll((Task)wingetTask, (Task)githubTask);
        var (winget, wingetError) = wingetTask.Result;
        var (github, githubError) = githubTask.Result;

        if (winget is null && github is null)
            return HandleCheckFailure(entry, $"WinGet: {wingetError} GitHub Releases: {githubError}");

        if (winget is null)
        {
            ApplyOfficialDetails(entry, github!);
            entry.OfficialDetailsText += $"\n\nNo se pudo consultar WinGet: {wingetError}";
            entry.Status = GetOfficialVersionStatus(entry);
            entry.LastCheckError = wingetError!;
            StatusMessage = $"WinGet no respondió para {entry.Name}; se usó GitHub Releases ({github!.Version}).";
            InstallersView.Refresh();
            return true;
        }

        if (github is null)
        {
            ApplyOfficialDetails(entry, winget);
            entry.OfficialDetailsText += $"\n\nNo se pudo consultar GitHub Releases: {githubError}";
            entry.Status = GetOfficialVersionStatus(entry);
            entry.LastCheckError = githubError!;
            StatusMessage = $"GitHub Releases no respondió para {entry.Name}; se usó WinGet ({winget.Version}).";
            InstallersView.Refresh();
            return true;
        }

        if (!VersionComparer.TryCompare(winget.Version, github.Version, out var comparison))
        {
            ClearOfficialDetails(entry);
            entry.OfficialDetailsText = $"WinGet: {winget.Version}\n{winget.DetailsText}\n\nGitHub Releases: {github.Version}\n{github.DetailsText}\n\nNo se eligió una fuente porque el comparador no pudo determinar cuál versión es más reciente.";
            entry.Status = "Comparación desconocida";
            entry.LastCheckError = "No se pudieron comparar con seguridad las versiones publicadas por WinGet y GitHub Releases.";
            StatusMessage = $"No se pudo decidir qué versión de {entry.Name} es más reciente (WinGet {winget.Version}; GitHub {github.Version}).";
            InstallersView.Refresh();
            return true;
        }

        // Keep WinGet as the tie-breaker; when versions differ, select the newer release.
        var selectedWinget = comparison >= 0;
        if (selectedWinget)
            ApplyOfficialDetails(entry, winget);
        else
            ApplyOfficialDetails(entry, github);

        var selectedName = selectedWinget ? "WinGet" : "GitHub Releases";
        entry.OfficialDetailsText = $"{entry.OfficialDetailsText}\n\nComparación de fuentes: WinGet {winget.Version}; GitHub Releases {github.Version}.\nFuente seleccionada: {selectedName} ({entry.OfficialVersion}).";
        entry.Status = GetOfficialVersionStatus(entry);
        entry.LastCheckError = string.Empty;
        StatusMessage = comparison == 0
            ? $"Ambas fuentes publican {entry.OfficialVersion} para {entry.Name}; se usó WinGet."
            : $"Versión más reciente de {entry.Name}: {entry.OfficialVersion} en {selectedName}.";
        InstallersView.Refresh();
        return true;
    }

    private static void ApplyOfficialDetails(InstallerEntry entry, WingetPackageDetails details)
    {
        entry.OfficialVersion = details.Version;
        entry.OfficialSha256 = details.InstallerSha256;
        entry.OfficialInstallerUrl = details.InstallerUrl;
        entry.OfficialPublisher = details.Publisher;
        entry.OfficialDetailsText = details.DetailsText;
    }

    private static void ApplyOfficialDetails(InstallerEntry entry, GitHubReleaseDetails details)
    {
        entry.OfficialVersion = details.Version;
        entry.OfficialSha256 = details.Sha256;
        entry.OfficialInstallerUrl = details.InstallerUrl;
        entry.OfficialPublisher = string.Empty;
        entry.OfficialDetailsText = details.DetailsText;
    }

    private async Task<bool> CheckGitHubReleaseAsync(InstallerEntry entry, string repositoryUrl, string? wingetError)
    {
        try
        {
            var details = await _githubReleaseClient.GetLatestReleaseAsync(repositoryUrl, entry);
            ApplyOfficialDetails(entry, details);
            entry.LastCheckError = string.Empty;
            entry.Status = GetOfficialVersionStatus(entry);
            StatusMessage = wingetError is null
                ? $"Comprobación completada con GitHub Releases: {entry.Name}."
                : $"Comprobación completada con GitHub Releases: {entry.Name}. WinGet no respondió; se usó la fuente alternativa.\n{wingetError}";
            return true;
        }
        catch (GitHubReleaseException ex)
        {
            var combinedMessage = wingetError is null ? ex.Message : $"WinGet: {wingetError} GitHub: {ex.Message}";
            return HandleCheckFailure(entry, combinedMessage);
        }
        finally
        {
            InstallersView.Refresh();
        }
    }

    private bool HandleCheckFailure(InstallerEntry entry, string errorMessage)
    {
        ClearOfficialDetails(entry);
        entry.Status = "Error de comprobación";
        entry.LastCheckError = errorMessage;
        StatusMessage = $"No se pudo comprobar {entry.Name}: {errorMessage}";
        InstallersView.Refresh();
        return false;
    }

    private async void CheckOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not InstallerEntry entry)
            return;
        SetDownloadControlsEnabled(false);
        try
        {
            await CheckEntryAsync(entry, bypassCache: true);
            MarkChecksFresh(allApplications: false);
            UpdateEmptyState();
        }
        finally
        {
            SetDownloadControlsEnabled(true);
        }
    }

}
