using System.IO;
using System.Windows;
using System.Windows.Controls;
using InstallerKeeper.Models;
using InstallerKeeper.Services;
using Microsoft.Win32;

namespace InstallerKeeper;

public partial class FirstRunWindow : Window
{
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;

    public FirstRunWindow(SettingsStore settingsStore, AppSettings settings)
    {
        InitializeComponent();
        _settingsStore = settingsStore;
        _settings = settings;
        InstallerFolderTextBox.Text = settings.InstallerFolderPath;
        IncludeSubdirectoriesCheckBox.IsChecked = settings.IncludeSubdirectories;
        WindowChromeManager.Attach(this);
        UpdateFolderState();
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Selecciona la carpeta de instaladores", Multiselect = false };
        if (Directory.Exists(InstallerFolderTextBox.Text))
            dialog.FolderName = InstallerFolderTextBox.Text;
        if (dialog.ShowDialog(this) == true)
            InstallerFolderTextBox.Text = dialog.FolderName;
    }

    private void InstallerFolder_TextChanged(object sender, TextChangedEventArgs e) => UpdateFolderState();

    private void UpdateFolderState()
    {
        var isValid = Directory.Exists(InstallerFolderTextBox.Text.Trim());
        ContinueButton.IsEnabled = isValid;
        ValidationMessage.Text = isValid
            ? "La carpeta está lista para analizar."
            : "Selecciona una carpeta existente para continuar.";
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        var folder = InstallerFolderTextBox.Text.Trim();
        if (!Directory.Exists(folder))
        {
            UpdateFolderState();
            return;
        }

        try
        {
            _settings.InstallerFolderPath = Path.GetFullPath(folder);
            _settings.IncludeSubdirectories = IncludeSubdirectoriesCheckBox.IsChecked == true;
            _settingsStore.Save(_settings);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"No se pudo guardar la carpeta seleccionada: {ex.Message}",
                "Configuración inicial", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
