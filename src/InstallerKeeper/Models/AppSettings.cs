namespace InstallerKeeper.Models;

public sealed class AppSettings
{
    public string InstallerFolderPath { get; set; } = string.Empty;
    public bool IncludeSubdirectories { get; set; }
    public bool CheckOnStartup { get; set; }
    public string ArchiveFolderPath { get; set; } = string.Empty;
    public string StagingFolderPath { get; set; } = string.Empty;
    public string DownloadPolicy { get; set; } = "Replace";
    public string DownloadNameFormat { get; set; } = "ApplicationVersion";
    public string MainLayout { get; set; } = "WorkPanel";
    public bool SuppressDownloadWarnings { get; set; }
    public bool AutoPromoteDownloads { get; set; }
    public string Theme { get; set; } = "System";
    public string ViewMode { get; set; } = "Table";
    public Dictionary<string, InstallerPreferences> InstallerPreferences { get; set; } = new(StringComparer.Ordinal);
}
