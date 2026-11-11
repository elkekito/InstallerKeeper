using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace InstallerKeeper.Services;

public static class ThemeManager
{
    private const string ThemeRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static string _selectedTheme = "System";

    static ThemeManager() => SystemEvents.UserPreferenceChanged += System_UserPreferenceChanged;

    public static bool IsDarkTheme { get; private set; }

    private static readonly IReadOnlyDictionary<string, string> LightColors = new Dictionary<string, string>
    {
        ["AppBackgroundBrush"] = "#F4F8FC",
        ["PanelBrush"] = "#FFFFFF",
        ["SurfaceBrush"] = "#FFFFFF",
        ["FieldBrush"] = "#FFFFFF",
        ["BorderBrush"] = "#DCE3EC",
        ["TextBrush"] = "#182230",
        ["MutedBrush"] = "#667085",
        ["GridHeaderBrush"] = "#F2F5F8",
        ["GridRowBrush"] = "#FFFFFF",
        ["GridAlternateBrush"] = "#FAFCFE",
        ["NeutralBadgeBrush"] = "#EAF0F6",
        ["PositiveBadgeBrush"] = "#DDF5E8",
        ["PositiveBadgeTextBrush"] = "#187043",
        ["WarningBadgeBrush"] = "#FFF0C2",
        ["WarningBadgeTextBrush"] = "#A64B00",
        ["InfoBadgeBrush"] = "#E1F0FF",
        ["InfoBadgeTextBrush"] = "#087BFA",
        ["BadgeTextBrush"] = "#344054",
        ["SelectedNavBrush"] = "#E1F0FF",
        ["CardBackgroundBrush"] = "#FFFFFF",
        ["CardBorderBrush"] = "#E2E8F0",
        ["CardHoverBrush"] = "#F8FAFC",
        ["SubtleBorderBrush"] = "#E2E8F0",
        ["AccentBrush"] = "#087BFA",
        ["AccentHoverBrush"] = "#0066D6",
        ["DrawerBackgroundBrush"] = "#FFFFFF"
    };

    private static readonly IReadOnlyDictionary<string, string> DarkColors = new Dictionary<string, string>
    {
        ["AppBackgroundBrush"] = "#111D28",
        ["PanelBrush"] = "#172633",
        ["SurfaceBrush"] = "#1B2C3A",
        ["FieldBrush"] = "#182936",
        ["BorderBrush"] = "#304351",
        ["TextBrush"] = "#F4F7FA",
        ["MutedBrush"] = "#A9B7C3",
        ["GridHeaderBrush"] = "#1D2E3C",
        ["GridRowBrush"] = "#172633",
        ["GridAlternateBrush"] = "#192936",
        ["NeutralBadgeBrush"] = "#2B3E4D",
        ["PositiveBadgeBrush"] = "#173F2F",
        ["PositiveBadgeTextBrush"] = "#65DB9B",
        ["WarningBadgeBrush"] = "#4A3517",
        ["WarningBadgeTextBrush"] = "#FFD06A",
        ["InfoBadgeBrush"] = "#123652",
        ["InfoBadgeTextBrush"] = "#45A5FF",
        ["BadgeTextBrush"] = "#E7EDF2",
        ["SelectedNavBrush"] = "#203B52",
        ["CardBackgroundBrush"] = "#172633",
        ["CardBorderBrush"] = "#304351",
        ["CardHoverBrush"] = "#1F3344",
        ["SubtleBorderBrush"] = "#263645",
        ["AccentBrush"] = "#087BFA",
        ["AccentHoverBrush"] = "#3B95FF",
        ["DrawerBackgroundBrush"] = "#14202B"
    };

    public static void Apply(string? theme)
    {
        _selectedTheme = theme is "Dark" or "Light" ? theme : "System";
        IsDarkTheme = _selectedTheme == "Dark" || (_selectedTheme == "System" && IsSystemDarkTheme());
        var colors = IsDarkTheme
            ? DarkColors
            : LightColors;

        foreach (var (key, color) in colors)
            Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

        foreach (Window window in Application.Current.Windows)
            WindowChromeManager.Apply(window, IsDarkTheme);
    }

    private static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ThemeRegistryPath, writable: false);
            var value = key?.GetValue("AppsUseLightTheme") ?? key?.GetValue("SystemUsesLightTheme");
            return value is int lightTheme && lightTheme == 0;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void System_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_selectedTheme != "System" || Application.Current is not { } app)
            return;

        if (app.Dispatcher.CheckAccess())
            Apply("System");
        else
            app.Dispatcher.BeginInvoke(() => Apply("System"));
    }
}
