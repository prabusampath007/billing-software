using System.Windows;
using System.Windows.Controls;

namespace BillingSoftware.Desktop.Services;

/// <summary>
/// Two-pane responsive layout. On a wide window the panes sit side by side and fill the height;
/// on a narrow window (small laptop, large display scale, small window) they stack and the page scrolls.
/// </summary>
public static class Adaptive
{
    public const double Breakpoint = 900;

    public static bool Apply(ScrollViewer scroller, Grid root, FrameworkElement first, FrameworkElement second,
        ColumnDefinition secondColumn, RowDefinition firstRow, double secondWidth = 360, double breakpoint = Breakpoint, bool secondVisible = true)
    {
        var wide = scroller.ActualWidth >= breakpoint;
        if (wide)
        {
            Grid.SetRow(first, 0); Grid.SetColumn(first, 0); Grid.SetColumnSpan(first, 1);
            Grid.SetRow(second, 0); Grid.SetColumn(second, 1); Grid.SetColumnSpan(second, 1);
            secondColumn.Width = new GridLength(secondWidth);
            firstRow.Height = new GridLength(1, GridUnitType.Star);
            second.Margin = new Thickness(12, 0, 0, 0);
            var vertical = root.Margin.Top + root.Margin.Bottom;
            root.Height = Math.Max(320, scroller.ActualHeight - vertical);
        }
        else
        {
            Grid.SetRow(first, 0); Grid.SetColumn(first, 0); Grid.SetColumnSpan(first, 2);
            Grid.SetRow(second, 1); Grid.SetColumn(second, 0); Grid.SetColumnSpan(second, 2);
            secondColumn.Width = new GridLength(0);
            firstRow.Height = GridLength.Auto;
            second.Margin = new Thickness(0, 12, 0, 0);
            root.Height = double.NaN;
        }
        // A closed side panel gives its room to the list beside it.
        second.Visibility = secondVisible ? Visibility.Visible : Visibility.Collapsed;
        if (!secondVisible) { secondColumn.Width = new GridLength(0); second.Margin = new Thickness(0); }
        return wide;
    }
}
