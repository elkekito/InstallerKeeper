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
    private void AppOptions_Click(object sender, RoutedEventArgs e)
    {
        var selected = (sender as FrameworkElement)?.Tag as InstallerEntry;
        if (selected is null)
            return;

        var previousWingetId = selected.WingetId;
        var wasWingetIdConfirmed = selected.WingetIdConfirmed;
        var previousGitHubRepositoryUrl = selected.GitHubRepositoryUrl;
        var dialog = new AppOptionsWindow(selected);
        if (dialog.ShowDialog() != true)
            return;

        var packageDetails = dialog.ConfirmedPackageDetails;
        var importedDetailsMatch = packageDetails is not null
            && selected.WingetIdConfirmed
            && string.Equals(packageDetails.Id, selected.WingetId, StringComparison.OrdinalIgnoreCase);
        if (importedDetailsMatch)
        {
            selected.Name = string.IsNullOrWhiteSpace(packageDetails!.Name) ? selected.Name : packageDetails.Name;
            selected.OfficialVersion = packageDetails.Version;
            selected.OfficialSha256 = packageDetails.InstallerSha256;
            selected.OfficialInstallerUrl = packageDetails.InstallerUrl;
            selected.OfficialPublisher = packageDetails.Publisher;
            selected.OfficialDetailsText = packageDetails.DetailsText;
            selected.LastCheckError = string.Empty;
            selected.Status = GetOfficialVersionStatus(selected);
        }
        else if (!string.Equals(previousWingetId, selected.WingetId, StringComparison.OrdinalIgnoreCase)
            || wasWingetIdConfirmed != selected.WingetIdConfirmed
            || (!selected.WingetIdConfirmed
                && !string.Equals(previousGitHubRepositoryUrl, selected.GitHubRepositoryUrl, StringComparison.OrdinalIgnoreCase)))
        {
            ClearOfficialDetails(selected);
            selected.Status = "Sin comprobar";
        }

        var key = string.IsNullOrWhiteSpace(selected.PreferenceKey)
            ? InstallerPreferenceKey.Create(selected)
            : selected.PreferenceKey;
        _settings.InstallerPreferences[key] = ToPreferences(selected);

        try
        {
            _settingsStore.Save(_settings);
            StatusMessage = "Las opciones de esta aplicación se han guardado.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this,
                $"No se pudieron guardar las opciones de la aplicación. Los cambios actuales no se volverán a aplicar en futuros análisis.\n\n{ex.Message}",
                "InstallerKeeper",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        InstallersView.Refresh();
        UpdateEmptyState();
    }

    private static void ClearOfficialDetails(InstallerEntry entry)
    {
        entry.OfficialVersion = string.Empty;
        entry.OfficialSha256 = string.Empty;
        entry.OfficialInstallerUrl = string.Empty;
        entry.OfficialPublisher = string.Empty;
        entry.OfficialDetailsText = string.Empty;
    }

    private static string GetOfficialVersionStatus(InstallerEntry entry) =>
        VersionComparer.TryCompare(entry.CurrentVersion, entry.OfficialVersion, out var comparison)
            ? comparison < 0 ? InstallerStatus.NewVersion : comparison > 0 ? "Versión local superior" : InstallerStatus.UpToDate
            : "Comparación desconocida";

    private void ApplySavedPreferences(InstallerEntry entry)
    {
        var key = string.IsNullOrWhiteSpace(entry.PreferenceKey)
            ? InstallerPreferenceKey.Create(entry)
            : entry.PreferenceKey;
        if (!_settings.InstallerPreferences.TryGetValue(key, out var preferences))
            return;

        if (preferences.Name is not null) entry.Name = preferences.Name;
        if (preferences.WingetId is not null) entry.WingetId = preferences.WingetId;
        if (preferences.WingetIdConfirmed.HasValue) entry.WingetIdConfirmed = preferences.WingetIdConfirmed.Value;
        if (preferences.GitHubRepositoryUrl is not null) entry.GitHubRepositoryUrl = preferences.GitHubRepositoryUrl;
        if (preferences.OfficialSourceUrl is not null) entry.OfficialSourceUrl = preferences.OfficialSourceUrl;
        if (preferences.OfficialVersion is not null) entry.OfficialVersion = preferences.OfficialVersion;
        if (preferences.OfficialSha256 is not null) entry.OfficialSha256 = preferences.OfficialSha256;
        if (preferences.OfficialInstallerUrl is not null) entry.OfficialInstallerUrl = preferences.OfficialInstallerUrl;
        if (preferences.OfficialPublisher is not null) entry.OfficialPublisher = preferences.OfficialPublisher;
        if (preferences.OfficialDetailsText is not null) entry.OfficialDetailsText = preferences.OfficialDetailsText;
        if (preferences.Architecture is not null) entry.Architecture = preferences.Architecture;
        if (preferences.Channel is not null) entry.Channel = preferences.Channel;
        if (preferences.Notes is not null) entry.Notes = preferences.Notes;
        if (preferences.DownloadPolicyOverride is not null) entry.DownloadPolicyOverride = preferences.DownloadPolicyOverride;
        if (preferences.IsExcluded.HasValue) entry.IsExcluded = preferences.IsExcluded.Value;
        if (preferences.IsHidden.HasValue) entry.IsHidden = preferences.IsHidden.Value;
    }

    private async void Options_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _settings = _settingsStore.Load();
            ThemeManager.Apply(_settings.Theme);
            ApplyLayout(_settings.MainLayout);
            await RefreshInstallersAsync();
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

}
