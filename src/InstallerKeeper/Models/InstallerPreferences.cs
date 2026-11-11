namespace InstallerKeeper.Models;

/// <summary>
/// User-controlled association and download settings for a package.
/// Null fields mean no saved override; empty strings are valid explicit values.
/// </summary>
public sealed class InstallerPreferences
{
    public string? Name { get; set; }
    public string? WingetId { get; set; }
    public bool? WingetIdConfirmed { get; set; }
    public string? GitHubRepositoryUrl { get; set; }
    public string? OfficialSourceUrl { get; set; }
    public string? OfficialVersion { get; set; }
    public string? OfficialSha256 { get; set; }
    public string? OfficialInstallerUrl { get; set; }
    public string? OfficialPublisher { get; set; }
    public string? OfficialDetailsText { get; set; }
    public string? Architecture { get; set; }
    public string? Channel { get; set; }
    public string? Notes { get; set; }
    public string? DownloadPolicyOverride { get; set; }
    public bool? IsExcluded { get; set; }
    public bool? IsHidden { get; set; }
}
