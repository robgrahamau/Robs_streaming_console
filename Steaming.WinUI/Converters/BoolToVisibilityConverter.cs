using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Steaming.WinUI.Converters;

// true → Visible, false → Collapsed. Pass parameter "invert" to flip.
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool b = value is bool v && v;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase)) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
