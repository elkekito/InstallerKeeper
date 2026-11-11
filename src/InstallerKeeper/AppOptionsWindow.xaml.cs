using System.Windows;
using InstallerKeeper.Models;
using InstallerKeeper.Services;

namespace InstallerKeeper;

public partial class AppOptionsWindow : Window
{
    private readonly WingetClient _wingetClient = new();
    private readonly string _originalWingetId;
    private readonly bool _originalWingetIdConfirmed;
    private string? _verifiedWingetId;
    private bool _rejectedWingetMatch;
    private bool _loadingCandidate;

    public WingetPackageDetails? ConfirmedPackageDetails { get; private set; }

    public AppOptionsWindow(InstallerEntry? installer)
    {
        InitializeComponent();
        WindowChromeManager.Attach(this);
        SubtitleText.Text = $"Ajusta cómo InstallerKeeper reconoce y descarga este instalador. · {AppInfo.VersionLabel}";
        _installer = installer;
        _originalWingetId = installer?.WingetId?.Trim() ?? string.Empty;
        _originalWingetIdConfirmed = installer?.WingetIdConfirmed == true;
        InstallerPathText.Text = installer?.LocalFilePath ?? string.Empty;

        if (installer is null)
        {
            return;
        }

        TitleText.Text = installer.Name;
        NameText.Text = installer.Name;
        WingetIdText.Text = installer.WingetId ?? string.Empty;
        WingetVerificationText.Text = installer.WingetIdConfirmed
            ? "ID exacto confirmado"
            : "ID pendiente de confirmar";
        OfficialSourceText.Text = installer.OfficialSourceUrl ?? string.Empty;
        GitHubRepositoryText.Text = installer.GitHubRepositoryUrl ?? string.Empty;
        WingetDetailsText.Text = installer.OfficialDetailsText;
        ArchitectureText.Text = installer.Architecture;
        ChannelText.Text = installer.Channel;
        NotesText.Text = installer.Notes;
        ExcludedCheck.IsChecked = installer.IsExcluded;
        HiddenCheck.IsChecked = installer.IsHidden;
        UseGlobalPolicyRadio.IsChecked = string.IsNullOrWhiteSpace(installer.DownloadPolicyOverride);
        ReplacePolicyRadio.IsChecked = installer.DownloadPolicyOverride == "Replace";
        ArchivePolicyRadio.IsChecked = installer.DownloadPolicyOverride == "Archive";
    }

    private readonly InstallerEntry? _installer;

    private void Section_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string section } || GeneralPage is null)
            return;

        GeneralPage.Visibility = section == "General" ? Visibility.Visible : Visibility.Collapsed;
        OfficialDataPage.Visibility = section == "OfficialData" ? Visibility.Visible : Visibility.Collapsed;
        SourcesPage.Visibility = section == "Sources" ? Visibility.Visible : Visibility.Collapsed;
        DownloadsPage.Visibility = section == "Downloads" ? Visibility.Visible : Visibility.Collapsed;

        (SectionTitle.Text, SubtitleText.Text) = section switch
        {
            "OfficialData" => ("Datos oficiales", $"Manifiesto y datos publicados por la fuente oficial. · {AppInfo.VersionLabel}"),
            "Sources" => ("Fuentes y enlaces", $"Repositorio GitHub y opciones de descarga directa. · {AppInfo.VersionLabel}"),
            "Downloads" => ("Descargas y versiones", $"Política de reemplazo o archivado de versiones. · {AppInfo.VersionLabel}"),
            _ => ("General", $"Ajusta cómo InstallerKeeper reconoce y descarga este instalador. · {AppInfo.VersionLabel}")
        };
    }

    private void ExcludedCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (ExcludedCheck?.IsChecked != true && HiddenCheck is not null)
            HiddenCheck.IsChecked = false;
    }

    private void WingetIdText_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_loadingCandidate)
            return;

        if (WingetIdText is null || WingetVerificationText is null)
            return;

        var currentId = WingetIdText.Text.Trim();
        var matchesSaved = _originalWingetIdConfirmed
            && string.Equals(currentId, _originalWingetId, StringComparison.OrdinalIgnoreCase);
        var matchesVerified = !string.IsNullOrWhiteSpace(_verifiedWingetId)
            && string.Equals(currentId, _verifiedWingetId, StringComparison.OrdinalIgnoreCase);

        if (matchesSaved && !_rejectedWingetMatch)
            WingetVerificationText.Text = "ID exacto confirmado";
        else if (matchesVerified)
            WingetVerificationText.Text = "ID exacto verificado y confirmado.";
        else
        {
            _verifiedWingetId = null;
            ConfirmedPackageDetails = null;
            WingetDetailsText.Text = "Verifica y confirma un ID para importar los datos publicados por WinGet.";
            WingetVerificationText.Text = "ID pendiente de confirmar";
        }
    }

    private async void VerifyWingetId_Click(object sender, RoutedEventArgs e)
    {
        var packageId = WingetIdText.Text.Trim();
        if (string.IsNullOrWhiteSpace(packageId))
        {
            WingetVerificationText.Text = "Escribe un identificador WinGet primero.";
            return;
        }

        WingetVerificationText.Text = "Consultando el origen WinGet…";
        ConfirmedPackageDetails = null;
        try
        {
            var details = await _wingetClient.ShowExactAsync(packageId);
            var productName = string.IsNullOrWhiteSpace(details.Name) ? "(no indicado)" : details.Name;
            var publisher = string.IsNullOrWhiteSpace(details.Publisher) ? "(no indicado)" : details.Publisher;
            var installerUrl = string.IsNullOrWhiteSpace(details.InstallerUrl) ? "(no indicada por WinGet)" : details.InstallerUrl;
            var installerHash = string.IsNullOrWhiteSpace(details.InstallerSha256) ? "(no indicado por WinGet)" : details.InstallerSha256;
            var message = $"Coincidencia exacta encontrada en el origen winget:\n\nNombre: {productName}\nID: {details.Id}\nEditor: {publisher}\nVersión oficial: {details.Version}\nURL del instalador: {installerUrl}\nSHA-256: {installerHash}\n\n¿Confirmas que corresponde a este instalador?";
            var answer = MessageBox.Show(
                this,
                message,
                "Confirmar aplicación",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Yes)
            {
                ConfirmedPackageDetails = details;
                WingetDetailsText.Text = details.DetailsText;
                if (!string.IsNullOrWhiteSpace(details.Name))
                    NameText.Text = details.Name;
                _verifiedWingetId = details.Id;
                _rejectedWingetMatch = false;
                WingetIdText.Text = details.Id;
                WingetVerificationText.Text = "ID exacto verificado y confirmado.";
            }
            else
            {
                _verifiedWingetId = null;
                _rejectedWingetMatch = true;
                WingetVerificationText.Text = "Coincidencia encontrada, pero no confirmada.";
            }
        }
        catch (WingetException ex)
        {
            _verifiedWingetId = null;
            WingetVerificationText.Text = "No se pudo verificar el ID.";
            MessageBox.Show(this, ex.Message, "WinGet", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void SearchWingetCandidates_Click(object sender, RoutedEventArgs e)
    {
        WingetVerificationText.Text = "Buscando coincidencias en el origen winget…";
        try
        {
            var candidates = await _wingetClient.SearchAsync(NameText.Text);
            if (candidates.Count == 0)
            {
                WingetVerificationText.Text = "WinGet no encontró coincidencias para ese nombre.";
                return;
            }

            var candidateWindow = new WingetCandidateWindow(candidates, _wingetClient) { Owner = this };
            if (candidateWindow.ShowDialog() == true && candidateWindow.ConfirmedPackageDetails is { } details)
                ApplyConfirmedPackageDetails(details);
            else
                WingetVerificationText.Text = $"Se encontraron {candidates.Count} coincidencias; no se confirmó ningún paquete.";
        }
        catch (WingetException ex)
        {
            WingetVerificationText.Text = "No se pudo buscar en WinGet.";
            MessageBox.Show(this, ex.Message, "WinGet", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ApplyConfirmedPackageDetails(WingetPackageDetails details)
    {
        _loadingCandidate = true;
        WingetIdText.Text = details.Id;
        if (!string.IsNullOrWhiteSpace(details.Name))
            NameText.Text = details.Name;
        _loadingCandidate = false;
        _verifiedWingetId = details.Id;
        ConfirmedPackageDetails = details;
        WingetDetailsText.Text = details.DetailsText;
        _rejectedWingetMatch = false;
        WingetVerificationText.Text = "ID exacto verificado y confirmado.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_installer is not null)
        {
            _installer.Name = NameText.Text.Trim();
            _installer.WingetId = WingetIdText.Text.Trim();
            _installer.WingetIdConfirmed = _installer.WingetId.Length > 0
                && ((_originalWingetIdConfirmed && !_rejectedWingetMatch && string.Equals(_installer.WingetId, _originalWingetId, StringComparison.OrdinalIgnoreCase))
                    || string.Equals(_installer.WingetId, _verifiedWingetId, StringComparison.OrdinalIgnoreCase));
            _installer.OfficialSourceUrl = OfficialSourceText.Text.Trim();
            _installer.GitHubRepositoryUrl = GitHubRepositoryText.Text.Trim();
            _installer.Architecture = ArchitectureText.Text.Trim();
            _installer.Channel = ChannelText.Text.Trim();
            _installer.Notes = NotesText.Text.Trim();
            _installer.IsExcluded = ExcludedCheck.IsChecked == true;
            _installer.IsHidden = _installer.IsExcluded && HiddenCheck.IsChecked == true;
            _installer.DownloadPolicyOverride = ReplacePolicyRadio.IsChecked == true
                ? "Replace"
                : ArchivePolicyRadio.IsChecked == true ? "Archive" : null;
        }

        DialogResult = true;
    }
}
