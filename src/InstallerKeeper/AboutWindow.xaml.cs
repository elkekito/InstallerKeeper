using System.Windows;
using System.Diagnostics;
using System.Windows.Navigation;
using InstallerKeeper.Services;

namespace InstallerKeeper;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = AppInfo.VersionLabel;
        WindowChromeManager.Attach(this);
    }

    private void LicenseLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });

        e.Handled = true;
    }
}
