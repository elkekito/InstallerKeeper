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
    private void ExportCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (Installers.Count == 0)
        {
            MessageBox.Show(
                "No hay aplicaciones en la biblioteca para exportar.",
                "Exportar catálogo",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar catálogo de la biblioteca",
            Filter = "Archivo CSV (*.csv)|*.csv|Archivo JSON (*.json)|*.json|Informe HTML (*.html)|*.html",
            DefaultExt = ".csv",
            FileName = $"Catalogo_Instaladores_{DateTime.Now:yyyyMMdd_HHmm}"
        };

        if (saveDialog.ShowDialog(this) != true)
            return;

        try
        {
            var ext = Path.GetExtension(saveDialog.FileName).ToLowerInvariant();
            switch (ext)
            {
                case ".json":
                    CatalogExportService.ExportToJson(Installers, saveDialog.FileName);
                    break;
                case ".html":
                case ".htm":
                    CatalogExportService.ExportToHtml(Installers, saveDialog.FileName);
                    break;
                case ".csv":
                default:
                    CatalogExportService.ExportToCsv(Installers, saveDialog.FileName);
                    break;
            }

            StatusMessage = $"Catálogo exportado con éxito: {Path.GetFileName(saveDialog.FileName)}";
            MessageBox.Show(
                $"Se ha exportado el catálogo de {Installers.Count} aplicaciones correctamente en:\n{saveDialog.FileName}",
                "Exportación completada",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Error al exportar el catálogo:\n{ex.Message}",
                "Error de exportación",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

}
