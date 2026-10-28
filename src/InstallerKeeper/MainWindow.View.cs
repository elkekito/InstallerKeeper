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
    private void IdentifyOne_Click(object sender, RoutedEventArgs e) => AppOptions_Click(sender, e);

    private void StatusFilter_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string filter)
            return;
        _statusFilter = filter;
        InstallersView.Refresh();
        UpdateFilterSelection();
        UpdateEmptyState();
    }

    private void Applications_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = (sender as ListBox)?.SelectedItem as InstallerEntry
            ?? (sender as DataGrid)?.SelectedItem as InstallerEntry;
        if (selected is not null)
        {
            _selectedEntry = selected;
            OnPropertyChanged(nameof(SelectedEntry));
            if (_isInspectorOpen)
                UpdateInspectorState();
        }
    }

    private void ToggleInspector_Click(object sender, RoutedEventArgs e)
    {
        _isInspectorOpen = !_isInspectorOpen;
        if (_isInspectorOpen && SelectedEntry is null)
        {
            SelectedEntry = ApplicationsGrid?.SelectedItem as InstallerEntry
                ?? ApplicationsCardsList?.SelectedItem as InstallerEntry
                ?? Installers.FirstOrDefault(entry => !entry.IsHidden);
        }
        UpdateInspectorState();
    }

    private void UpdateInspectorState()
    {
        if (InspectorPanel is null || InspectorColumn is null)
            return;

        if (_isInspectorOpen && SelectedEntry is not null)
        {
            InspectorColumn.Width = new GridLength(350);
            InspectorPanel.Visibility = Visibility.Visible;
            if (ToggleInspectorButton is not null)
                ToggleInspectorButton.Background = (System.Windows.Media.Brush)FindResource("SelectedNavBrush");
        }
        else
        {
            _isInspectorOpen = false;
            InspectorColumn.Width = new GridLength(0);
            InspectorPanel.Visibility = Visibility.Collapsed;
            if (ToggleInspectorButton is not null)
                ToggleInspectorButton.Background = System.Windows.Media.Brushes.Transparent;
        }
    }

    private void CloseInspector_Click(object sender, RoutedEventArgs e)
    {
        _isInspectorOpen = false;
        UpdateInspectorState();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
    }

    private void SwitchViewMode_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string mode)
            return;
        SetViewMode(mode);
    }

    private void SetViewMode(string mode)
    {
        _currentViewMode = mode == "Cards" ? "Cards" : "Table";
        if (_settings.ViewMode != _currentViewMode)
        {
            _settings.ViewMode = _currentViewMode;
            try { _settingsStore.Save(_settings); } catch { }
        }

        if (TableViewContainer is not null && CardsViewContainer is not null)
        {
            TableViewContainer.Visibility = _currentViewMode == "Table" ? Visibility.Visible : Visibility.Collapsed;
            CardsViewContainer.Visibility = _currentViewMode == "Cards" ? Visibility.Visible : Visibility.Collapsed;
        }

        if (ViewTableButton is not null && ViewCardsButton is not null)
        {
            ViewTableButton.Background = _currentViewMode == "Table"
                ? (System.Windows.Media.Brush)FindResource("SelectedNavBrush")
                : System.Windows.Media.Brushes.Transparent;
            ViewCardsButton.Background = _currentViewMode == "Cards"
                ? (System.Windows.Media.Brush)FindResource("SelectedNavBrush")
                : System.Windows.Media.Brushes.Transparent;
        }
    }

    private void CopySha256_Click(object sender, RoutedEventArgs e)
    {
        var hash = SelectedEntry?.OfficialSha256;
        if (!string.IsNullOrWhiteSpace(hash))
        {
            Clipboard.SetText(hash);
            StatusMessage = "Hash SHA-256 copiado al portapapeles.";
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedEntry?.LocalFilePath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            Clipboard.SetText(path);
            StatusMessage = "Ruta del archivo copiada al portapapeles.";
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var entry = (sender as FrameworkElement)?.Tag as InstallerEntry ?? SelectedEntry;
        if (entry is null || string.IsNullOrWhiteSpace(entry.LocalFilePath))
            return;
        try
        {
            if (File.Exists(entry.LocalFilePath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{entry.LocalFilePath}\"",
                    UseShellExecute = true
                });
            }
            else if (Directory.Exists(_settings.InstallerFolderPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{_settings.InstallerFolderPath}\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo abrir el Explorador: {ex.Message}";
        }
    }

    private void ApplyLayout(string? layout)
    {
        var useSidebar = string.Equals(layout, "Sidebar", StringComparison.OrdinalIgnoreCase);
        SidebarColumn.Width = new GridLength(useSidebar ? 190 : 0);
        SidebarPanel.Visibility = useSidebar ? Visibility.Visible : Visibility.Collapsed;
        WorkPanelFilters.Visibility = useSidebar ? Visibility.Collapsed : Visibility.Visible;
        BrandSubtitle.Text = useSidebar
            ? "Mantén tus instaladores siempre actualizados"
            : _lastCheckedAt is null
                ? "Biblioteca de instaladores"
                : $"Biblioteca · Última comprobación {GetLastCheckAge()}";
        UpdateFilterSelection();
    }

    private void MarkChecksFresh(bool allApplications)
    {
        _lastCheckedAt = DateTime.Now;
        var checkText = allApplications
            ? "Comprobar todas: resultados vigentes"
            : "Comprobación individual realizada";
        CheckFreshnessText.Text = checkText;
        SidebarFreshnessText.Text = "Hace unos segundos";
        if (!string.Equals(_settings.MainLayout, "Sidebar", StringComparison.OrdinalIgnoreCase))
            BrandSubtitle.Text = $"Biblioteca · Última comprobación {GetLastCheckAge()}";
    }

    private string GetLastCheckAge()
    {
        if (_lastCheckedAt is null)
            return "sin comprobar";
        var elapsed = DateTime.Now - _lastCheckedAt.Value;
        if (elapsed.TotalMinutes < 1)
            return "hace unos segundos";
        if (elapsed.TotalHours < 1)
            return $"hace {(int)elapsed.TotalMinutes} min";
        return $"hace {(int)elapsed.TotalHours} h";
    }

    private void UpdateFilterSelection()
    {
        if (WorkAllFilter is null)
            return;

        var buttons = new[]
        {
            SidebarAllFilter, SidebarUpdatesFilter, SidebarCurrentFilter, SidebarPendingFilter, SidebarHiddenFilter,
            WorkAllFilter, WorkUpdatesFilter, WorkCurrentFilter, WorkPendingFilter, WorkHiddenFilter
        };
        foreach (var button in buttons)
        {
            var selected = string.Equals(button.Tag?.ToString(), _statusFilter, StringComparison.Ordinal);
            button.Background = selected
                ? (System.Windows.Media.Brush)FindResource("SelectedNavBrush")
                : System.Windows.Media.Brushes.Transparent;
            button.Foreground = selected
                ? (System.Windows.Media.Brush)FindResource("PrimaryBrush")
                : (System.Windows.Media.Brush)FindResource(button.Name.StartsWith("Sidebar", StringComparison.Ordinal)
                    ? "TextBrush"
                    : "MutedBrush");
        }
        SidebarAllCountText.Text = Installers.Count(entry => !entry.IsHidden).ToString();
        SidebarUpdatesCountText.Text = Installers.Count(entry => entry.Status == InstallerStatus.NewVersion).ToString();
        SidebarCurrentCountText.Text = Installers.Count(entry => entry.Status == InstallerStatus.UpToDate).ToString();
        SidebarPendingCountText.Text = Installers.Count(entry => !entry.IsHidden && !entry.IsIdentified).ToString();
        SidebarHiddenCountText.Text = Installers.Count(entry => entry.IsHidden).ToString();
        WorkAllCountText.Text = Installers.Count(entry => !entry.IsHidden).ToString();
        WorkUpdatesCountText.Text = Installers.Count(entry => !entry.IsHidden && entry.Status == InstallerStatus.NewVersion).ToString();
        WorkCurrentCountText.Text = Installers.Count(entry => !entry.IsHidden && entry.Status == InstallerStatus.UpToDate).ToString();
        WorkPendingCountText.Text = Installers.Count(entry => !entry.IsHidden && !entry.IsIdentified).ToString();
        WorkHiddenCountText.Text = Installers.Count(entry => entry.IsHidden).ToString();
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (InstallersView is null)
            return;

        InstallersView.Refresh();
        UpdateEmptyState();
    }

    private bool MatchesSearch(object item)
    {
        if (item is not InstallerEntry installer)
            return false;

        var search = SearchBox?.Text?.Trim();
        var matchesSearch = string.IsNullOrEmpty(search) ||
               installer.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               installer.Publisher.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               installer.WingetId?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true;
        if (!matchesSearch)
            return false;

        if (_statusFilter == "Hidden")
            return installer.IsHidden;
        if (installer.IsHidden)
            return false;

        var matchesStatus = _statusFilter switch
        {
            "Updates" => installer.Status == InstallerStatus.NewVersion,
            "Current" => installer.Status == InstallerStatus.UpToDate,
            "Pending" => !installer.IsIdentified,
            _ => true
        };
        if (!matchesStatus)
            return false;
        return true;
    }

    private void UpdateEmptyState()
    {
        if (EmptyStatePanel is null)
            return;

        EmptyStateTitle.Text = Installers.Count == 0
            ? "No se encontraron instaladores"
            : "No hay coincidencias";
        EmptyStatePanel.Visibility = InstallersView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        UpdateFilterSelection();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

}
