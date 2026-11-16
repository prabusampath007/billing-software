using System.Globalization;
using System.Windows.Data;
using BillingSoftware.Domain;

namespace BillingSoftware.Desktop.Views;

/// <summary>Shows a paise amount as rupees with Indian digit grouping.</summary>
public sealed class PaiseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long p ? Money.FormatPlain(p) : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Shows a GST rate in basis points as a percentage.</summary>
public sealed class RateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int bp ? $"{bp / 100m:0.##}%" : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Shows a quantity in thousandths.</summary>
public sealed class QtyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long q ? Qty.Format(q) : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Shows basis points as a percentage (a margin).</summary>
public sealed class BpConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long bp ? (bp / 100m).ToString("0.0") + "%" : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
