using System.IO;
using System.Windows;
using System.Windows.Media;
using InstallerKeeper.Models;
using InstallerKeeper.Services;

namespace InstallerKeeper;

public sealed partial class ArchiveCleanupWindow : Window
{
    private readonly AppSettings _settings;
    private string _archiveRootPath = string.Empty;
    private string? _archivePathError;
    private ArchiveScanResult? _scanResult;
    private ArchivePurgePlan? _currentPlan;

    public sealed class ItemViewModel
    {
        public required ArchivedItem Item { get; init; }
        public string AppGroup => Item.AppGroup;
        public string FileName => Item.FileName;
        public string InferredVersion => Item.InferredVersion;
        public string FormattedSize => Item.FormattedSize;
        public string WillBePurgedText => Item.WillBePurged ? "A eliminar" : "Conservar";
        public Brush ActionColorBrush => Item.WillBePurged
            ? (ThemeManager.IsDarkTheme ? new SolidColorBrush(Color.FromRgb(255, 120, 120)) : new SolidColorBrush(Color.FromRgb(220, 38, 38)))
            : (ThemeManager.IsDarkTheme ? new SolidColorBrush(Color.FromRgb(101, 219, 155)) : new SolidColorBrush(Color.FromRgb(24, 112, 67)));
        public Brush BadgeBackgroundBrush => Item.WillBePurged
            ? (ThemeManager.IsDarkTheme ? new SolidColorBrush(Color.FromRgb(74, 30, 30)) : new SolidColorBrush(Color.FromRgb(254, 226, 226)))
            : (ThemeManager.IsDarkTheme ? new SolidColorBrush(Color.FromRgb(23, 63, 47)) : new SolidColorBrush(Color.FromRgb(221, 245, 232)));
    }

    public ArchiveCleanupWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Loaded += ArchiveCleanupWindow_Loaded;
    }

    private void ArchiveCleanupWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ResolveArchivePath();
        ScanAndRefresh();
    }

    private void ResolveArchivePath()
    {
        _archivePathError = null;
        var installerRoot = string.IsNullOrWhiteSpace(_settings.InstallerFolderPath)
            ? string.Empty
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(_settings.InstallerFolderPath));

        if (!string.IsNullOrWhiteSpace(_settings.ArchiveFolderPath))
        {
            _archiveRootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_settings.ArchiveFolderPath));
            // Mismo criterio que MainWindow.GetArchiveDirectory: si coincide con la carpeta supervisada, se usa .Archive.
            if (installerRoot.Length > 0 && string.Equals(_archiveRootPath, installerRoot, StringComparison.OrdinalIgnoreCase))
                _archiveRootPath = Path.Combine(_archiveRootPath, ".Archive");
        }
        else if (!string.IsNullOrWhiteSpace(_settings.InstallerFolderPath))
        {
            var defaultPath = Path.Combine(Path.GetFullPath(_settings.InstallerFolderPath), ".Archive");
            var legacyPath = Path.Combine(Path.GetFullPath(_settings.InstallerFolderPath), ".InstallerKeeperArchive");
            _archiveRootPath = !Directory.Exists(defaultPath) && Directory.Exists(legacyPath) ? legacyPath : defaultPath;
        }
        else
        {
            _archiveRootPath = string.Empty;
        }

        if (_archiveRootPath.Length > 0 && installerRoot.Length > 0 && ContainsPath(_archiveRootPath, installerRoot))
        {
            _archivePathError = "La carpeta de archivo contiene la carpeta de instaladores; la purga está desactivada para no borrar la biblioteca. Elige una carpeta de archivo independiente en Opciones.";
            _archiveRootPath = string.Empty;
        }

        ArchiveLocationText.Text = _archivePathError
            ?? (string.IsNullOrWhiteSpace(_archiveRootPath)
                ? "No se ha configurado ninguna carpeta supervisada."
                : _archiveRootPath);
    }

    private static bool ContainsPath(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative == "."
            || (!Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private void ScanAndRefresh()
    {
        if (string.IsNullOrWhiteSpace(_archiveRootPath) || !Directory.Exists(_archiveRootPath))
        {
            TotalSummaryText.Text = "0 archivos (0 B)";
            PurgeSummaryText.Text = "0 archivos a eliminar (0 B)";
            PreviewGrid.ItemsSource = null;
            PurgeButton.IsEnabled = false;
            return;
        }

        _scanResult = ArchiveCleanupService.ScanArchive(_archiveRootPath);
        TotalSummaryText.Text = $"{_scanResult.TotalFiles} archivos ({_scanResult.FormattedTotalSize})";

        RecalculatePlan();
    }

    private void RecalculatePlan()
    {
        if (_scanResult is null || _scanResult.TotalFiles == 0)
        {
            PurgeSummaryText.Text = "0 archivos a eliminar (0 B)";
            PreviewGrid.ItemsSource = null;
            PurgeButton.IsEnabled = false;
            return;
        }

        int keepCount = 1;
        if (Keep2Radio.IsChecked == true) keepCount = 2;
        else if (PurgeAllRadio.IsChecked == true) keepCount = 0;

        _currentPlan = ArchiveCleanupService.BuildPlan(_scanResult, keepCount);

        PurgeSummaryText.Text = $"{_currentPlan.FilesToPurgeCount} archivos ({_currentPlan.FormattedBytesToFree})";

        var viewModels = _scanResult.AllItems
            .Select(item => new ItemViewModel { Item = item })
            .ToArray();

        PreviewGrid.ItemsSource = viewModels;
        PurgeButton.IsEnabled = _currentPlan.FilesToPurgeCount > 0;
    }

    private void PolicyRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        RecalculatePlan();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        ResolveArchivePath();
        ScanAndRefresh();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Purge_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlan is null || _currentPlan.FilesToPurgeCount == 0 || string.IsNullOrWhiteSpace(_archiveRootPath))
            return;

        var confirm = MessageBox.Show(
            $"¿Estás seguro de que deseas eliminar permanentemente {_currentPlan.FilesToPurgeCount} instalador(es) archivado(s) y liberar {_currentPlan.FormattedBytesToFree} de espacio en disco?\n\nEsta acción no se puede deshacer.",
            "Confirmar purga del archivo",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
            return;

        var result = ArchiveCleanupService.ExecutePurge(_currentPlan.ItemsToPurge, _archiveRootPath);

        if (result.Errors.Count > 0)
        {
            MessageBox.Show(
                $"Se eliminaron {result.DeletedFilesCount} archivos ({ArchiveScanResult.FormatSize(result.BytesFreed)} liberados).\n\nOcurrieron errores al eliminar algunos archivos:\n" + string.Join("\n", result.Errors.Take(5)),
                "Purga completada con advertencias",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        else
        {
            MessageBox.Show(
                $"Se han eliminado con éxito {result.DeletedFilesCount} archivos y se han liberado {ArchiveScanResult.FormatSize(result.BytesFreed)} en el disco.",
                "Purga completada con éxito",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        ScanAndRefresh();
    }
}
