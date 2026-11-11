using System.Numerics;
using System.Text.RegularExpressions;

namespace InstallerKeeper.Services;

public static class VersionComparer
{
    private const int MaxVersionTextLength = 256;

    private static readonly Regex VersionPattern = new(
        @"^v?(?<core>\d+(?:\.\d+)*)(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryCompare(string? left, string? right, out int comparison)
    {
        comparison = 0;
        if ((left?.Length ?? 0) > MaxVersionTextLength
            || (right?.Length ?? 0) > MaxVersionTextLength)
            return false;

        var leftMatch = VersionPattern.Match((left ?? string.Empty).Trim());
        var rightMatch = VersionPattern.Match((right ?? string.Empty).Trim());
        if (!leftMatch.Success || !rightMatch.Success)
            return false;

        var leftParts = leftMatch.Groups["core"].Value.Split('.');
        var rightParts = rightMatch.Groups["core"].Value.Split('.');
        if ((leftMatch.Groups["pre"].Success && leftParts.Length < 3)
            || (rightMatch.Groups["pre"].Success && rightParts.Length < 3))
            return false;
        var coreLength = Math.Max(leftParts.Length, rightParts.Length);
        for (var index = 0; index < coreLength; index++)
        {
            var leftPart = index < leftParts.Length ? BigInteger.Parse(leftParts[index]) : BigInteger.Zero;
            var rightPart = index < rightParts.Length ? BigInteger.Parse(rightParts[index]) : BigInteger.Zero;
            comparison = leftPart.CompareTo(rightPart);
            if (comparison != 0)
                return true;
        }

        var leftPreRelease = leftMatch.Groups["pre"].Value;
        var rightPreRelease = rightMatch.Groups["pre"].Value;
        if (leftPreRelease.Length == 0 || rightPreRelease.Length == 0)
        {
            comparison = leftPreRelease.Length == rightPreRelease.Length
                ? 0
                : leftPreRelease.Length == 0 ? 1 : -1;
            return true;
        }

        var leftIdentifiers = leftPreRelease.Split('.');
        var rightIdentifiers = rightPreRelease.Split('.');
        var identifierCount = Math.Min(leftIdentifiers.Length, rightIdentifiers.Length);
        for (var index = 0; index < identifierCount; index++)
        {
            var leftIsNumber = BigInteger.TryParse(leftIdentifiers[index], out var leftNumber);
            var rightIsNumber = BigInteger.TryParse(rightIdentifiers[index], out var rightNumber);
            if (leftIsNumber && rightIsNumber)
                comparison = leftNumber.CompareTo(rightNumber);
            else if (leftIsNumber != rightIsNumber)
                comparison = leftIsNumber ? -1 : 1;
            else
                comparison = string.Compare(leftIdentifiers[index], rightIdentifiers[index], StringComparison.OrdinalIgnoreCase);

            if (comparison != 0)
                return true;
        }

        comparison = leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
        return true;
    }
}
