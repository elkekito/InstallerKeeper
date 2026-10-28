using System.IO;

namespace InstallerKeeper.Services;

internal static class InstallerPathGuard
{
    public static void EnsureNoReparsePointInExistingAncestors(string directoryPath, string errorMessage)
    {
        var currentPath = Path.GetFullPath(directoryPath);
        while (!string.IsNullOrWhiteSpace(currentPath))
        {
            if (Directory.Exists(currentPath)
                && (File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(errorMessage);

            var parent = Directory.GetParent(currentPath);
            if (parent is null || string.Equals(parent.FullName, currentPath, StringComparison.OrdinalIgnoreCase))
                break;

            currentPath = parent.FullName;
        }
    }
}
