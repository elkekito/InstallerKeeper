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
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshInstallersAsync();
        if (_settings.CheckOnStartup)
            await CheckAllAsync();
    }

    private async void RefreshApplications_Click(object sender, RoutedEventArgs e)
    {
        SetDownloadControlsEnabled(false);
        try
        {
            WingetClient.ClearCache();
            CheckFreshnessText.Text = "Actualizando lista de archivos";
            await RefreshInstallersAsync();
            StatusMessage = $"Biblioteca actualizada: {Installers.Count} instaladores en la carpeta.";
        }
        finally
        {
            SetDownloadControlsEnabled(true);
        }
    }

    private async Task RefreshInstallersAsync()
    {
        var previousChecks = Installers
            .GroupBy(entry => entry.PreferenceKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => InstallerCheckSnapshot.From(group.Last()), StringComparer.OrdinalIgnoreCase);
        Installers.Clear();
        if (string.IsNullOrWhiteSpace(_settings.InstallerFolderPath) ||
            !Directory.Exists(_settings.InstallerFolderPath))
        {
            StatusMessage = "Selecciona una carpeta válida en Opciones para analizar sus instaladores.";
            UpdateEmptyState();
            return;
        }

        StatusMessage = "Analizando archivos…";
        try
        {
            var folderPath = _settings.InstallerFolderPath;
            var includeSubdirectories = _settings.IncludeSubdirectories;
            var excludedFolders = new[] { _settings.ArchiveFolderPath, _settings.StagingFolderPath };
            var found = await Task.Run(() => _scanner.Scan(folderPath, includeSubdirectories, excludedFolders));

            foreach (var entry in found)
            {
                ApplySavedPreferences(entry);
                if (previousChecks.TryGetValue(entry.PreferenceKey, out var previous) && previous.HasCheckResult)
                    previous.ApplyTo(entry);
                Installers.Add(entry);
            }

            StatusMessage = found.Count == 1
                ? "Se encontró 1 instalador."
                : $"Se encontraron {found.Count} instaladores.";
            UpdateEmptyState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusMessage = "No se pudo analizar la carpeta seleccionada.";
            UpdateEmptyState();
            MessageBox.Show(this,
                $"No se pudo analizar la carpeta seleccionada.\n\n{ex.Message}",
                "InstallerKeeper",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private sealed record InstallerCheckSnapshot(
        string OfficialVersion,
        string OfficialSha256,
        string OfficialInstallerUrl,
        string OfficialPublisher,
        string OfficialDetailsText,
        string Status,
        string LastCheckError)
    {
        public bool HasCheckResult => !string.IsNullOrWhiteSpace(OfficialVersion)
            || !string.IsNullOrWhiteSpace(LastCheckError);

        public static InstallerCheckSnapshot From(InstallerEntry entry) => new(
            entry.OfficialVersion,
            entry.OfficialSha256,
            entry.OfficialInstallerUrl,
            entry.OfficialPublisher,
            entry.OfficialDetailsText,
            entry.Status,
            entry.LastCheckError);

        public void ApplyTo(InstallerEntry entry)
        {
            entry.OfficialVersion = OfficialVersion;
            entry.OfficialSha256 = OfficialSha256;
            entry.OfficialInstallerUrl = OfficialInstallerUrl;
            entry.OfficialPublisher = OfficialPublisher;
            entry.OfficialDetailsText = OfficialDetailsText;
            entry.Status = Status;
            entry.LastCheckError = LastCheckError;
        }
    }
}
