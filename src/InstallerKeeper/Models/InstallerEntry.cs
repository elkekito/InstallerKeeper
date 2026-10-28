namespace InstallerKeeper.Models;

public sealed class InstallerEntry : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _currentVersion = string.Empty;
    private string _officialVersion = string.Empty;
    private string _status = "Sin comprobar";
    private string? _wingetId;
    private bool _wingetIdConfirmed;
    private string? _githubRepositoryUrl;
    private bool _isExcluded;
    private bool _isHidden;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Name { get => _name; set => SetField(ref _name, value); }
    public string CurrentVersion { get => _currentVersion; set => SetField(ref _currentVersion, value); }
    public string OfficialVersion { get => _officialVersion; set => SetField(ref _officialVersion, value); }
    public string Status { get => _status; set => SetField(ref _status, value); }
    public string? LocalFilePath { get; set; }
    public string PackageType { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string PackageIdentity { get; set; } = string.Empty;
    public string PreferenceKey { get; set; } = string.Empty;
    public string ScanWarning { get; set; } = string.Empty;
    public string? WingetId
    {
        get => _wingetId;
        set
        {
            if (!SetField(ref _wingetId, value)) return;
            OnPropertyChanged(nameof(IsIdentified));
            OnPropertyChanged(nameof(IsPendingIdentification));
        }
    }
    public bool WingetIdConfirmed
    {
        get => _wingetIdConfirmed;
        set
        {
            if (!SetField(ref _wingetIdConfirmed, value)) return;
            OnPropertyChanged(nameof(IsIdentified));
            OnPropertyChanged(nameof(IsPendingIdentification));
        }
    }
    public string? GitHubRepositoryUrl
    {
        get => _githubRepositoryUrl;
        set
        {
            if (!SetField(ref _githubRepositoryUrl, value)) return;
            OnPropertyChanged(nameof(IsIdentified));
            OnPropertyChanged(nameof(IsPendingIdentification));
        }
    }
    public bool IsIdentified => WingetIdConfirmed && !string.IsNullOrWhiteSpace(WingetId)
        || !string.IsNullOrWhiteSpace(GitHubRepositoryUrl);
    public bool IsPendingIdentification => !IsIdentified;
    public string LastCheckError { get; set; } = string.Empty;
    public string OfficialSha256 { get; set; } = string.Empty;
    public string OfficialInstallerUrl { get; set; } = string.Empty;
    public string OfficialPublisher { get; set; } = string.Empty;
    public string OfficialDetailsText { get; set; } = string.Empty;
    public string? OfficialSourceUrl { get; set; }
    public string Architecture { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string? DownloadPolicyOverride { get; set; }
    public bool IsExcluded { get => _isExcluded; set => SetField(ref _isExcluded, value); }
    public bool IsHidden { get => _isHidden; set => SetField(ref _isHidden, value); }

    public string FormattedFileSize
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LocalFilePath) || !System.IO.File.Exists(LocalFilePath))
                return "-";
            try
            {
                var length = new System.IO.FileInfo(LocalFilePath).Length;
                if (length >= 1024 * 1024 * 1024)
                    return $"{length / (1024.0 * 1024.0 * 1024.0):0.##} GB";
                if (length >= 1024 * 1024)
                    return $"{length / (1024.0 * 1024.0):0.#} MB";
                if (length >= 1024)
                    return $"{length / 1024.0:0} KB";
                return $"{length} B";
            }
            catch
            {
                return "-";
            }
        }
    }

    public string FormattedFileDate
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LocalFilePath) || !System.IO.File.Exists(LocalFilePath))
                return "-";
            try
            {
                return System.IO.File.GetLastWriteTime(LocalFilePath).ToString("dd/MM/yyyy HH:mm");
            }
            catch
            {
                return "-";
            }
        }
    }

    public string FileNameOnly => string.IsNullOrWhiteSpace(LocalFilePath) ? string.Empty : System.IO.Path.GetFileName(LocalFilePath);

    private bool SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}

