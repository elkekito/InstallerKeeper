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

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly SettingsStore _settingsStore = new();
    private readonly InstallerScanner _scanner = new();
    private readonly WingetClient _wingetClient = new();
    private readonly GitHubReleaseClient _githubReleaseClient = new();
    private readonly SafeDownloadClient _downloadClient = new();
    private readonly PackageMetadataReader _metadataReader = new();
    private AppSettings _settings = new();
    private string _statusMessage = "Selecciona una carpeta en Opciones para analizar sus instaladores.";
    private string _statusFilter = "All";
    private DateTime? _lastCheckedAt;
    private InstallerEntry? _selectedEntry;
    private string _currentViewMode = "Table";
    private bool _isInspectorOpen;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<InstallerEntry> Installers { get; } = new();
    public ICollectionView InstallersView { get; }

    public InstallerEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (_selectedEntry == value)
                return;
            _selectedEntry = value;
            OnPropertyChanged();
            if (_isInspectorOpen)
                UpdateInspectorState();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
                return;
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        WindowChromeManager.Attach(this);
        _settings = _settingsStore.Load();
        ThemeManager.Apply(_settings.Theme);
        ApplyLayout(_settings.MainLayout);
        SetViewMode(_settings.ViewMode);
        InstallersView = CollectionViewSource.GetDefaultView(Installers);
        InstallersView.Filter = MatchesSearch;
        DataContext = this;
        UpdateFilterSelection();
    }

}
