using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SyncWaveAudio.Views;

/// <summary>
/// Converts a boolean to a Color. Used to assign different waveform colors per device type.
/// </summary>
public class BoolToColorConverter : IValueConverter
{
    public string TrueColor { get; set; } = "#63A4FF";
    public string FalseColor { get; set; } = "#E879F9";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hex = value is true ? TrueColor : FalseColor;
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Color.FromRgb(0x63, 0xA4, 0xFF);
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
