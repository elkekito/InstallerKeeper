using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using InstallerKeeper.Services;

namespace InstallerKeeper;

public partial class WingetCandidateWindow : Window
{
    private readonly WingetClient _wingetClient;
    private WingetPackageDetails? _verifiedPackageDetails;

    public ObservableCollection<WingetSearchCandidate> Candidates { get; }
    public WingetPackageDetails? ConfirmedPackageDetails { get; private set; }

    public WingetCandidateWindow(IEnumerable<WingetSearchCandidate> candidates, WingetClient wingetClient)
    {
        InitializeComponent();
        WindowChromeManager.Attach(this);
        _wingetClient = wingetClient;
        Candidates = new ObservableCollection<WingetSearchCandidate>(candidates);
        DataContext = this;
        if (Candidates.Count > 0)
            CandidatesGrid.SelectedIndex = 0;
    }

    private void CandidatesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _verifiedPackageDetails = null;
        ConfirmButton.IsEnabled = false;
        VerifyButton.IsEnabled = CandidatesGrid.SelectedItem is WingetSearchCandidate;
        VerifiedDetailsText.Clear();
        SelectionStatusText.Text = CandidatesGrid.SelectedItem is WingetSearchCandidate candidate
            ? $"Seleccionado: {candidate.Name} ({candidate.Id}). Verifica el ID exacto para importar sus datos oficiales."
            : "Selecciona un paquete para verificarlo.";
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (CandidatesGrid.SelectedItem is not WingetSearchCandidate candidate)
            return;

        VerifyButton.IsEnabled = false;
        ConfirmButton.IsEnabled = false;
        CandidatesGrid.IsEnabled = false;
        SelectionStatusText.Text = $"Verificando {candidate.Id} con una consulta exacta de WinGet…";
        try
        {
            var details = await _wingetClient.ShowExactAsync(candidate.Id);
            if (!string.Equals(details.Id, candidate.Id, StringComparison.OrdinalIgnoreCase))
                throw new WingetException("El identificador verificado no coincide con el paquete seleccionado.");

            _verifiedPackageDetails = details;
            VerifiedDetailsText.Text = details.DetailsText;
            SelectionStatusText.Text = $"ID exacto confirmado por WinGet: {details.Id}. Revisa los datos y pulsa «Usar este paquete».";
            ConfirmButton.IsEnabled = true;
        }
        catch (WingetException ex)
        {
            _verifiedPackageDetails = null;
            VerifiedDetailsText.Text = string.Empty;
            SelectionStatusText.Text = $"No se pudo verificar el paquete: {ex.Message}";
        }
        finally
        {
            CandidatesGrid.IsEnabled = true;
            VerifyButton.IsEnabled = CandidatesGrid.SelectedItem is WingetSearchCandidate;
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_verifiedPackageDetails is null)
            return;
        ConfirmedPackageDetails = _verifiedPackageDetails;
        DialogResult = true;
    }
}
