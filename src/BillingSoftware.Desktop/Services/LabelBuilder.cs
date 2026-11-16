using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using BillingSoftware.Domain;

namespace BillingSoftware.Desktop.Services;

public sealed record LabelSpec(string ShopName, string ItemName, string PriceText, string Code);

/// <summary>Draws EAN-13 bars and lays out sheets of price labels (A4, three across).</summary>
public static class LabelBuilder
{
    public const double A4Width = 793.7, A4Height = 1122.5;       // 96 dpi
    private const double Margin = 28;
    private const int Columns = 3;

    /// <summary>The bars of an EAN-13 code as a drawing: guard bars run a little lower than the rest, as on printed packs.</summary>
    public static FrameworkElement BarcodeVisual(string code, double moduleWidth = 1.6, double height = 40)
    {
        var bits = Ean13.Pattern(code);
        var quiet = 9 * moduleWidth;                                  // blank space each side so scanners find the start
        var width = bits.Length * moduleWidth + 2 * quiet;
        var canvas = new Canvas { Width = width, Height = height + 4, Background = Brushes.White, SnapsToDevicePixels = true };

        bool IsGuard(int i) => i < 3 || (i >= 45 && i < 50) || i >= 92;
        var x = 0;
        while (x < bits.Length)
        {
            if (!bits[x]) { x++; continue; }
            var start = x;
            while (x < bits.Length && bits[x] && IsGuard(x) == IsGuard(start)) x++;
            var guard = IsGuard(start);
            var bar = new Rectangle { Width = (x - start) * moduleWidth, Height = guard ? height + 4 : height, Fill = Brushes.Black };
            Canvas.SetLeft(bar, quiet + start * moduleWidth);
            canvas.Children.Add(bar);
        }
        return canvas;
    }

    /// <summary>"4 006381 333931": the way the digits are printed under the bars.</summary>
    public static string SpacedDigits(string code) => $"{code[0]} {code.Substring(1, 6)} {code.Substring(7, 6)}";

    private static FrameworkElement Label(LabelSpec s, double width)
    {
        var panel = new StackPanel { Width = width, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = s.ShopName, FontSize = 8, Foreground = Brushes.Gray, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = s.ItemName, FontSize = 11, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 30, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = s.PriceText, FontSize = 15, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 1, 0, 2) });
        var bars = BarcodeVisual(s.Code);
        bars.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(bars);
        panel.Children.Add(new TextBlock { Text = SpacedDigits(s.Code), FontSize = 9, FontFamily = new FontFamily("Consolas"), TextAlignment = TextAlignment.Center });
        return new Border { Child = panel, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0.5), Padding = new Thickness(4, 4, 4, 3), SnapsToDevicePixels = true };
    }

    /// <summary>A sheet of labels. The page size is fixed to A4, which tells the printing code to print real pages instead of one roll.</summary>
    public static FlowDocument Build(IReadOnlyList<LabelSpec> labels)
    {
        var doc = new FlowDocument
        {
            PageWidth = A4Width, PageHeight = A4Height, ColumnWidth = A4Width, PagePadding = new Thickness(Margin),
            FontFamily = new FontFamily("Segoe UI"), Background = Brushes.White, Tag = "a4",
        };
        var cellWidth = (A4Width - 2 * Margin) / Columns - 6;
        var table = new Table { CellSpacing = 0 };
        for (var c = 0; c < Columns; c++) table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (var i = 0; i < labels.Count; i += Columns)
        {
            var row = new TableRow();
            for (var c = 0; c < Columns; c++)
            {
                var cell = new TableCell { Padding = new Thickness(3) };
                if (i + c < labels.Count) cell.Blocks.Add(new BlockUIContainer(Label(labels[i + c], cellWidth)));
                else cell.Blocks.Add(new Paragraph());
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        doc.Blocks.Add(table);
        return doc;
    }

    /// <summary>
    /// Labels for a thermal label printer: one label per page, the page being the label itself (for example 50 x 25 mm).
    /// The bars are scaled to fill the width, so the same code works for any label size.
    /// </summary>
    public static FlowDocument BuildRoll(IReadOnlyList<LabelSpec> labels, int widthMm, int heightMm)
    {
        var w = PrintConfig.Px(widthMm); var h = PrintConfig.Px(heightMm);
        var pad = PrintConfig.Px(2);
        var doc = new FlowDocument
        {
            PageWidth = w, PageHeight = h, ColumnWidth = w, PagePadding = new Thickness(pad, PrintConfig.Px(1), pad, PrintConfig.Px(1)),
            FontFamily = new FontFamily("Segoe UI"), Background = Brushes.White, Tag = "label",
        };
        var inner = w - 2 * pad;
        var moduleWidth = inner / (Ean13.Modules + 18);              // 9 modules of quiet space each side
        var textLines = heightMm >= 20 ? 3 : 2;                      // name, price, digits (small labels drop the name's second line)
        var barHeight = Math.Max(14, h - 2 * PrintConfig.Px(1) - textLines * 13 - 6);
        for (var i = 0; i < labels.Count; i++)
        {
            var s = labels[i];
            var panel = new StackPanel { Width = inner, HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(new TextBlock { Text = s.ItemName, FontSize = 8.5, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(new TextBlock { Text = s.PriceText, FontSize = 12, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center });
            var bars = BarcodeVisual(s.Code, moduleWidth, barHeight);
            bars.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(bars);
            panel.Children.Add(new TextBlock { Text = SpacedDigits(s.Code), FontSize = 7.5, FontFamily = new FontFamily("Consolas"), TextAlignment = TextAlignment.Center });
            doc.Blocks.Add(new BlockUIContainer(panel) { BreakPageBefore = i > 0, Margin = new Thickness(0) });
        }
        return doc;
    }

    /// <summary>How many labels fit on one A4 sheet (three across, about eight down).</summary>
    public const int LabelsPerSheet = 24;
}
