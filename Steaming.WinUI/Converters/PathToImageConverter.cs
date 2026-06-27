using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Steaming.WinUI.Converters;

// Converts a local file path (string) to a BitmapImage for Image.Source bindings.
// Empty/missing path → null (Image shows nothing). Keeps the VM free of WinUI image types.
public sealed class PathToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path)) return null;
        try { return new BitmapImage(new Uri(path)); }
        catch { return null; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
