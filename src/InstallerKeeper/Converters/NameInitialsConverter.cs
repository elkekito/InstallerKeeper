using System.Globalization;
using System.Windows.Data;

namespace InstallerKeeper.Converters;

public sealed class NameInitialsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var words = (value?.ToString() ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (words.Length == 0)
            return "•";
        if (words.Length == 1)
            return words[0][..1].ToUpperInvariant();

        return string.Concat(words[0][..1], words[1][..1]).ToUpperInvariant();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
