using System;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Steaming.WinUI.Converters;

// Converts a "#RRGGBB" or "#AARRGGBB" hex string to a SolidColorBrush.
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var hex = (value as string)?.TrimStart('#');
        byte a = 0xFF, r = 0xFF, g = 0xFF, b = 0xFF;
        if (!string.IsNullOrEmpty(hex))
        {
            if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            {
                r = (byte)((rgb >> 16) & 0xFF); g = (byte)((rgb >> 8) & 0xFF); b = (byte)(rgb & 0xFF);
            }
            else if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            {
                a = (byte)((argb >> 24) & 0xFF); r = (byte)((argb >> 16) & 0xFF);
                g = (byte)((argb >> 8) & 0xFF); b = (byte)(argb & 0xFF);
            }
        }
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
