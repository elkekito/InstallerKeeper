using System.Windows;
using System.IO;
using System.Windows.Threading;
using InstallerKeeper.Services;

namespace InstallerKeeper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var settingsStore = new SettingsStore();
        var settings = settingsStore.Load();
        ThemeManager.Apply(settings.Theme);

        if (!Directory.Exists(settings.InstallerFolderPath))
        {
            var firstRun = new FirstRunWindow(settingsStore, settings);
            if (firstRun.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
    }

    // Los manejadores async void de la ventana principal no propagan sus excepciones; sin este
    // manejador, cualquier fallo en ellos cierra la aplicación sin aviso.
    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = TryWriteErrorLog(e.Exception);
        MessageBox.Show(
            "Se produjo un error inesperado. La aplicación sigue abierta y el detalle técnico se guardó en:\n"
                + (logPath ?? "(no se pudo escribir el registro)") + "\n\n" + e.Exception.Message,
            "InstallerKeeper",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static string? TryWriteErrorLog(Exception exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InstallerKeeper");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "errors.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {AppInfo.Version}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
