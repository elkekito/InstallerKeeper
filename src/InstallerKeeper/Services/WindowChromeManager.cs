using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;

namespace InstallerKeeper.Services;

public static class WindowChromeManager
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeLegacy = 19;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;
    private static readonly ConditionalWeakTable<Window, object> AttachedWindows = new();

    public static void Attach(Window window)
    {
        if (AttachedWindows.TryGetValue(window, out _))
        {
            Apply(window, ThemeManager.IsDarkTheme);
            return;
        }

        AttachedWindows.Add(window, new object());
        window.SourceInitialized += (_, _) => Apply(window, ThemeManager.IsDarkTheme);
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Apply(window, ThemeManager.IsDarkTheme);
    }

    public static void Apply(Window window, bool isDark)
    {
        AttachWhenNeeded(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        var darkValue = isDark ? 1 : 0;
        if (SetWindowAttribute(handle, UseImmersiveDarkMode, ref darkValue) != 0)
            SetWindowAttribute(handle, UseImmersiveDarkModeLegacy, ref darkValue);

        var captionColor = isDark ? ColorRef(0x11, 0x1D, 0x28) : unchecked((int)0xFFFFFFFF);
        var textColor = isDark ? ColorRef(0xF4, 0xF7, 0xFA) : unchecked((int)0xFFFFFFFF);
        var borderColor = isDark ? ColorRef(0x30, 0x43, 0x51) : unchecked((int)0xFFFFFFFF);
        SetWindowAttribute(handle, CaptionColor, ref captionColor);
        SetWindowAttribute(handle, TextColor, ref textColor);
        SetWindowAttribute(handle, BorderColor, ref borderColor);
    }

    private static void AttachWhenNeeded(Window window)
    {
        if (!AttachedWindows.TryGetValue(window, out _))
        {
            AttachedWindows.Add(window, new object());
            window.SourceInitialized += (_, _) => Apply(window, ThemeManager.IsDarkTheme);
        }
    }

    private static int ColorRef(byte red, byte green, byte blue) => red | (green << 8) | (blue << 16);

    private static int SetWindowAttribute(IntPtr window, int attribute, ref int value) =>
        DwmSetWindowAttribute(window, attribute, ref value, Marshal.SizeOf<int>());

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
