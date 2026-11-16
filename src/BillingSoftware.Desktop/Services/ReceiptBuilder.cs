using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BillingSoftware.Application;
using BillingSoftware.Domain;

namespace BillingSoftware.Desktop.Services;

/// <summary>Builds the 80 mm thermal receipt. Reprints are clearly marked DUPLICATE (decision D1/D9).</summary>
public static class ReceiptBuilder
{
    /// <summary>The printable width of the thermal roll (80 mm or 58 mm paper, chosen in Settings).</summary>
    public static double PaperWidth => PrintConfig.PaperWidthPx;

    public static readonly FontFamily TamilFont = new("Nirmala UI, Latha, Segoe UI");

    public static FlowDocument Build(SaleDetailDto sale, CompanyDto? company, bool duplicate)
    {
        var doc = new FlowDocument
        {
            PageWidth = PaperWidth,
            PagePadding = new Thickness(8, 8, 8, 8),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            ColumnWidth = PaperWidth,
            Background = Brushes.White,
        };

        void Center(string text, double size = 11, bool bold = false) =>
            doc.Blocks.Add(new Paragraph(new Run(text) { FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal })
            { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 1) });
        void Plain(string text, bool bold = false) =>
            doc.Blocks.Add(new Paragraph(new Run(text) { FontWeight = bold ? FontWeights.Bold : FontWeights.Normal }) { Margin = new Thickness(0, 0, 0, 1) });
        void Rule() => doc.Blocks.Add(new Paragraph(new Run(new string('-', PrintConfig.RuleChars))) { Margin = new Thickness(0), FontSize = 9, Foreground = Brushes.Gray });

        var composition = sale.CgstPaise + sale.SgstPaise + sale.IgstPaise == 0 && sale.InvoiceNumber.StartsWith("BOS", StringComparison.Ordinal);
        Center(company?.Name ?? "Shop", 15, true);
        if (!string.IsNullOrWhiteSpace(company?.Address)) Center(company!.Address!);
        if (!string.IsNullOrWhiteSpace(company?.Phone)) Center("Ph: " + company!.Phone);
        if (!string.IsNullOrWhiteSpace(company?.Gstin)) Center("GSTIN: " + company!.Gstin);
        Center(composition ? "BILL OF SUPPLY" : "TAX INVOICE", 12, true);
        if (duplicate) Center("*** DUPLICATE COPY ***", 12, true);
        if (sale.Status == SaleStatus.Voided) Center("*** VOIDED ***", 12, true);
        Rule();
        Plain($"Bill: {sale.InvoiceNumber}", true);
        Plain($"Date: {sale.SaleDate:dd-MMM-yyyy HH:mm}    By: {sale.CashierName}");
        if (!string.IsNullOrWhiteSpace(sale.CustomerName)) Plain("Customer: " + sale.CustomerName);
        if (!string.IsNullOrWhiteSpace(sale.CustomerGstin)) Plain("Customer GSTIN: " + sale.CustomerGstin);
        Rule();

        var table = new Table { CellSpacing = 0 };
        foreach (var w in new[] { 3.6, 1.1, 1.6, 1.7 }) table.Columns.Add(new TableColumn { Width = new GridLength(w, GridUnitType.Star) });
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        TableRow Row(string a, string b, string c, string d, bool bold = false, double size = 11, bool tamil = false)
        {
            var row = new TableRow();
            TableCell Cell(string t, TextAlignment al, bool inTamil = false)
            {
                var run = new Run(t) { FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, FontSize = size };
                if (inTamil) run.FontFamily = TamilFont;                        // Windows' Tamil font; printing is as a picture, so any installed font prints
                return new TableCell(new Paragraph(run) { TextAlignment = al, Margin = new Thickness(0) });
            }
            row.Cells.Add(Cell(a, TextAlignment.Left, tamil));
            row.Cells.Add(Cell(b, TextAlignment.Right));
            row.Cells.Add(Cell(c, TextAlignment.Right));
            row.Cells.Add(Cell(d, TextAlignment.Right));
            return row;
        }
        group.Rows.Add(Row("Item", "Qty", "Rate", "Amount", true, 10));
        foreach (var l in sale.Lines)
        {
            var hasTamil = !string.IsNullOrWhiteSpace(l.TamilName);
            group.Rows.Add(Row(hasTamil ? l.TamilName!.Trim() : l.ProductName, Qty.Format(l.QtyMilli), Money.FormatPlain(l.UnitPricePaise), Money.FormatPlain(l.LineTotalPaise + 0), tamil: hasTamil));
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(l.HsnCode)) meta.Add("HSN " + l.HsnCode);
            if (l.GstRateBp > 0) meta.Add($"GST {l.GstRateBp / 100m:0.##}%");
            if (l.DiscountPaise > 0) meta.Add("Disc " + Money.FormatPlain(l.DiscountPaise));
            if (meta.Count > 0) group.Rows.Add(Row("  " + string.Join("  ", meta), "", "", "", false, 9));
        }
        doc.Blocks.Add(table);
        Rule();

        Table Totals()
        {
            var t = new Table { CellSpacing = 0 };
            t.Columns.Add(new TableColumn { Width = new GridLength(2, GridUnitType.Star) });
            t.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            var g = new TableRowGroup();
            t.RowGroups.Add(g);
            void Add(string label, long paise, bool bold = false, double size = 11)
            {
                var r = new TableRow();
                r.Cells.Add(new TableCell(new Paragraph(new Run(label) { FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, FontSize = size }) { Margin = new Thickness(0) }));
                r.Cells.Add(new TableCell(new Paragraph(new Run(Money.FormatPlain(paise)) { FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, FontSize = size })
                { TextAlignment = TextAlignment.Right, Margin = new Thickness(0) }));
                g.Rows.Add(r);
            }
            if (!composition)
            {
                Add("Taxable value", sale.TaxablePaise);
                if (sale.IsInterState) Add("IGST", sale.IgstPaise);
                else { Add("CGST", sale.CgstPaise); Add("SGST", sale.SgstPaise); }
            }
            if (sale.DiscountPaise > 0) Add("Total discount", sale.DiscountPaise);
            if (sale.RoundOffPaise != 0) Add("Round off", sale.RoundOffPaise);
            Add("TOTAL", sale.TotalPaise, true, 14);
            foreach (var p in sale.Payments) Add(p.Mode switch { PaymentMode.Cash => "Cash", PaymentMode.Upi => "UPI", PaymentMode.Card => "Card", _ => "On credit" }, p.AmountPaise);
            return t;
        }
        doc.Blocks.Add(Totals());
        Rule();
        Center("Thank you. Visit again.");
        return doc;
    }

    /// <summary>A short test page: the paper width, text sizes and a barcode.</summary>
    public static FlowDocument BuildTest(string? shopName)
    {
        var doc = new FlowDocument { PageWidth = PaperWidth, ColumnWidth = PaperWidth, PagePadding = new Thickness(8), FontFamily = new FontFamily("Segoe UI"), FontSize = 11, Background = Brushes.White };
        Paragraph P(string t, double size = 11, bool bold = false, TextAlignment al = TextAlignment.Center) =>
            new(new Run(t) { FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal }) { TextAlignment = al, Margin = new Thickness(0, 0, 0, 2) };
        doc.Blocks.Add(P(shopName ?? "Shop", 15, true));
        doc.Blocks.Add(P("PRINTER TEST", 12, true));
        doc.Blocks.Add(P(new string('-', PrintConfig.RuleChars), 9));
        doc.Blocks.Add(P($"Paper: {PrintConfig.PaperMm} mm roll", 11, false, TextAlignment.Left));
        doc.Blocks.Add(P("The line of dashes above should reach both edges without being cut or wrapping.", 10, false, TextAlignment.Left));
        doc.Blocks.Add(P("Small text 9 pt   Normal 11 pt", 9, false, TextAlignment.Left));
        doc.Blocks.Add(P("Rupee sign: ₹ 1,234.50", 11, false, TextAlignment.Left));
        doc.Blocks.Add(new BlockUIContainer(LabelBuilder.BarcodeVisual("4006381333931", Math.Max(0.9, (PaperWidth - 24) / 113.0), 36)) { Margin = new Thickness(0, 6, 0, 0) });
        doc.Blocks.Add(P(LabelBuilder.SpacedDigits("4006381333931"), 8));
        doc.Blocks.Add(P("If the bars scan with your scanner, the printer is set up well.", 9));
        return doc;
    }

    /// <summary>
    /// Prints a document. Receipts (and other roll documents) go to the receipt printer chosen in Settings, or the Windows default, on one continuous
    /// page cut to the content. A4 documents and labels are tagged "a4" / "label": they go to their own printer, and ask which printer to use when
    /// none is set, so a statement is never squeezed onto a receipt roll by accident.
    /// </summary>
    public static void Print(FlowDocument doc, bool showDialog)
    {
        var kind = doc.Tag as string;
        var preferred = kind == "label" ? PrintConfig.LabelPrinter : kind == "a4" ? PrintConfig.DocumentPrinter : PrintConfig.ReceiptPrinter;
        var dialog = new PrintDialog();
        var chosen = false;
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            try { using var server = new LocalPrintServer(); dialog.PrintQueue = server.GetPrintQueue(preferred); chosen = true; }
            catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException) { /* the printer is gone: fall back to asking */ }
        }
        if (showDialog || ((kind == "a4" || kind == "label") && !chosen))
            if (dialog.ShowDialog() != true) return;

        if (kind is "a4" or "label")
        {
            var pages = (IDocumentPaginatorSource)doc;
            pages.DocumentPaginator.PageSize = new Size(doc.PageWidth, doc.PageHeight);
            dialog.PrintDocument(pages.DocumentPaginator, kind == "label" ? "Labels" : "Document");
            return;
        }

        doc.PageHeight = 20000;
        var paginator = ((IDocumentPaginatorSource)doc).DocumentPaginator;
        var width = double.IsNaN(doc.PageWidth) ? PaperWidth : doc.PageWidth;
        paginator.PageSize = new Size(width, 20000);
        paginator.ComputePageCount();
        var page = paginator.GetPage(0);
        var height = Math.Ceiling(page.ContentBox.Height + 24);
        doc.PageHeight = Math.Max(height, 120);
        paginator.PageSize = new Size(width, doc.PageHeight);
        dialog.PrintDocument(paginator, "Bill");
    }
}
