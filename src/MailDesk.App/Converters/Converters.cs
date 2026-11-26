using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MailDesk.App.Converters;

public sealed class DateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset d) return "";
        var now = DateTimeOffset.Now;
        if (d.Date == now.Date) return d.ToString("HH:mm");
        if (d.Year == now.Year) return d.ToString("d MMM");
        return d.ToString("d MMM yyyy");
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ReadToFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? FontWeights.Normal : FontWeights.Bold;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s ? System.IO.Path.GetFileName(s) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>First letter of a display name, for avatar circles.</summary>
public sealed class InitialConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = (value as string)?.Trim();
        if (string.IsNullOrEmpty(s)) return "?";
        var first = s.FirstOrDefault(char.IsLetterOrDigit);
        return first == default ? "?" : char.ToUpperInvariant(first).ToString();
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Stable colour per sender, so the same person always gets the same avatar colour.</summary>
public sealed class AvatarBrushConverter : IValueConverter
{
    private static readonly Brush[] Palette =
    [
        Hex("#E01E5A"), Hex("#36C5F0"), Hex("#2EB67D"), Hex("#ECB22E"), Hex("#4A154B"),
        Hex("#1264A3"), Hex("#E8912D"), Hex("#7C3AED"), Hex("#0E9F6E"), Hex("#C026D3"),
    ];

    private static Brush Hex(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = (value as string) ?? "";
        var hash = 0;
        foreach (var c in s.ToLowerInvariant()) hash = unchecked(hash * 31 + c);
        return Palette[Math.Abs(hash % Palette.Length)];
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
