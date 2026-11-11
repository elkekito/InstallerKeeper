using System.IO;
using System.Text.Json;
using InstallerKeeper.Models;

namespace InstallerKeeper.Services;

public sealed class SettingsStore
{
    private const int MaxReadAttempts = 3;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(150);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InstallerKeeper",
        "settings.json");

    /// <summary>
    /// True when the last <see cref="Load"/> could not read an existing settings file. Saving is refused
    /// in that state so the defaults returned by <see cref="Load"/> never overwrite the user's real settings.
    /// </summary>
    public bool LoadFailed { get; private set; }

    public AppSettings Load()
    {
        LoadFailed = false;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new AppSettings();

                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                    ?? new AppSettings();
            }
            catch (JsonException)
            {
                // Keep a copy of the unreadable file; only then is it safe to start from defaults.
                LoadFailed = !TryPreserveUnreadableFile();
                return new AppSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < MaxReadAttempts)
                {
                    Thread.Sleep(ReadRetryDelay);
                    continue;
                }

                LoadFailed = true;
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        if (LoadFailed)
            throw new IOException("No se guardará la configuración porque no se pudo leer el archivo existente; así se evita sobrescribir tus opciones. Cierra y vuelve a abrir InstallerKeeper.");

        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("No se pudo determinar la carpeta de configuración.");
        Directory.CreateDirectory(directory);

        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    private bool TryPreserveUnreadableFile()
    {
        try
        {
            File.Copy(SettingsPath, $"{SettingsPath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
