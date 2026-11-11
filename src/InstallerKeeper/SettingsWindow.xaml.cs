using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using InstallerKeeper.Models;
using InstallerKeeper.Services;
using Microsoft.Win32;

namespace InstallerKeeper;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _settingsStore = new();

    public SettingsWindow()
    {
        InitializeComponent();
        WindowChromeManager.Attach(this);
        GeneralNavRadio.IsChecked = true;
        LoadSettings(_settingsStore.Load());
    }

    private void LoadSettings(AppSettings settings)
    {
        FolderPathSummary.Text = settings.InstallerFolderPath;
        IncludeSubdirectoriesCheckBox.IsChecked = settings.IncludeSubdirectories;
        CheckOnStartupCheckBox.IsChecked = settings.CheckOnStartup;
        ArchiveFolderPathTextBox.Text = settings.ArchiveFolderPath;
        StagingFolderPathTextBox.Text = settings.StagingFolderPath;
        SuppressDownloadWarningsCheckBox.IsChecked = settings.SuppressDownloadWarnings;
        AutoPromoteDownloadsCheckBox.IsChecked = settings.AutoPromoteDownloads;
        ReplaceRadioButton.IsChecked = settings.DownloadPolicy == "Replace";
        ArchiveRadioButton.IsChecked = settings.DownloadPolicy == "Archive";
        KeepAllRadioButton.IsChecked = settings.DownloadPolicy == "KeepAll";
        ApplicationVersionNameRadio.IsChecked = settings.DownloadNameFormat != "WinGet";
        WingetNameRadio.IsChecked = settings.DownloadNameFormat == "WinGet";
        MainLayoutComboBox.SelectedItem = MainLayoutComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag?.ToString(), settings.MainLayout));

        var themeLabel = settings.Theme switch
        {
            "Dark" => "Oscuro",
            "System" => "Sistema",
            _ => "Claro"
        };
        ThemeComboBox.SelectedItem = ThemeComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Content?.ToString(), themeLabel));
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Selecciona la carpeta de instaladores",
            Multiselect = false
        };

        if (Directory.Exists(FolderPathSummary.Text))
            dialog.FolderName = FolderPathSummary.Text;

        if (dialog.ShowDialog(this) == true)
        {
            FolderPathSummary.Text = dialog.FolderName;
        }
    }

    private void BrowseArchiveFolder_Click(object sender, RoutedEventArgs e) =>
        BrowseConfiguredFolder(ArchiveFolderPathTextBox, "Selecciona la carpeta base del archivo de versiones");

    private void BrowseStagingFolder_Click(object sender, RoutedEventArgs e) =>
        BrowseConfiguredFolder(StagingFolderPathTextBox, "Selecciona la carpeta temporal de descargas");

    private void ResetArchiveFolder_Click(object sender, RoutedEventArgs e) => ArchiveFolderPathTextBox.Clear();

    private void ResetStagingFolder_Click(object sender, RoutedEventArgs e) => StagingFolderPathTextBox.Clear();

    private void OpenArchiveCleanup_Click(object sender, RoutedEventArgs e)
    {
        var settings = _settingsStore.Load();
        settings.InstallerFolderPath = FolderPathSummary.Text.Trim();
        settings.ArchiveFolderPath = ArchiveFolderPathTextBox.Text.Trim();
        var cleanupWindow = new ArchiveCleanupWindow(settings) { Owner = this };
        cleanupWindow.ShowDialog();
    }

    private void BrowseConfiguredFolder(TextBox target, string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (Directory.Exists(target.Text))
            dialog.FolderName = target.Text;
        if (dialog.ShowDialog(this) == true)
            target.Text = dialog.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var selectedTheme = (ThemeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var settings = _settingsStore.Load();
        settings.InstallerFolderPath = FolderPathSummary.Text.Trim();
        settings.IncludeSubdirectories = IncludeSubdirectoriesCheckBox.IsChecked == true;
        settings.CheckOnStartup = CheckOnStartupCheckBox.IsChecked == true;
        settings.ArchiveFolderPath = ArchiveFolderPathTextBox.Text.Trim();
        settings.StagingFolderPath = StagingFolderPathTextBox.Text.Trim();
        settings.SuppressDownloadWarnings = SuppressDownloadWarningsCheckBox.IsChecked == true;
        settings.AutoPromoteDownloads = AutoPromoteDownloadsCheckBox.IsChecked == true;
        settings.MainLayout = (MainLayoutComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Sidebar"
            ? "Sidebar"
            : "WorkPanel";
        settings.DownloadPolicy = ArchiveRadioButton.IsChecked == true
            ? "Archive"
            : KeepAllRadioButton.IsChecked == true ? "KeepAll" : "Replace";
        settings.DownloadNameFormat = WingetNameRadio.IsChecked == true ? "WinGet" : "ApplicationVersion";
        settings.Theme = selectedTheme switch
        {
            "Oscuro" => "Dark",
            "Sistema" => "System",
            _ => "Light"
        };

        try
        {
            _settingsStore.Save(settings);
            ThemeManager.Apply(settings.Theme);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                $"No se pudieron guardar las opciones.\n\n{ex.Message}",
                "InstallerKeeper",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Section_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string section })
            return;

        GeneralPage.Visibility = section == "General" ? Visibility.Visible : Visibility.Collapsed;
        FoldersPage.Visibility = section == "Folders" ? Visibility.Visible : Visibility.Collapsed;
        DownloadsPage.Visibility = section == "Downloads" ? Visibility.Visible : Visibility.Collapsed;
        InterfacePage.Visibility = section == "Interface" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = section == "About" ? Visibility.Visible : Visibility.Collapsed;

        (SectionTitle.Text, SectionSubtitle.Text) = section switch
        {
            "Folders" => ("Carpetas", $"Elige dónde guardar y buscar los instaladores. · {AppInfo.VersionLabel}"),
            "Downloads" => ("Descargas", $"Configura cómo conservar las versiones descargadas. · {AppInfo.VersionLabel}"),
            "Interface" => ("Interfaz", $"Personaliza el aspecto de InstallerKeeper. · {AppInfo.VersionLabel}"),
            "About" => ("Acerca de", $"Información y datos de InstallerKeeper. · {AppInfo.VersionLabel}"),
            _ => ("General", $"Configura la carpeta y el comportamiento de InstallerKeeper. · {AppInfo.VersionLabel}")
        };
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private void RestoreDownloadWarnings_Click(object sender, RoutedEventArgs e)
    {
        SuppressDownloadWarningsCheckBox.IsChecked = false;
    }

    private void LicenseLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });

        e.Handled = true;
    }
}
