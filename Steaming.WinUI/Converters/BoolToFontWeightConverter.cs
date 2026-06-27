using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Text;
using Windows.UI.Text;

namespace Steaming.WinUI.Converters;

// true → Bold, false → Normal (for the active lyric line).
public sealed class BoolToFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is bool b && b) ? FontWeights.Bold : FontWeights.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
