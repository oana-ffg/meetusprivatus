using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Meetu.App;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; init; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// Visible when a value exists and is not empty, for optional text and lists.
public sealed class PresenceToVisibility : IValueConverter
{
    public bool Invert { get; init; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var present = value switch
        {
            null => false,
            string text => text.Length > 0,
            ICollection items => items.Count > 0,
            _ => true
        };
        return present != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
