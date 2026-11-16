using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using BillingSoftware.Application;
using BillingSoftware.Domain;

namespace BillingSoftware.Desktop.Services;

/// <summary>The full tax invoice on A4 pages, for customers who need a proper invoice (the 80 mm receipt stays the counter bill).</summary>
public static class InvoiceBuilder
{
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0xB8, 0xC0, 0xCC));

    public static FlowDocument BuildA4(SaleDetailDto sale, CompanyDto? company, bool duplicate)
    {
        var doc = new FlowDocument
        {
            PageWidth = LabelBuilder.A4Width, PageHeight = LabelBuilder.A4Height, ColumnWidth = LabelBuilder.A4Width,
            PagePadding = new Thickness(40, 36, 40, 36), FontFamily = new FontFamily("Segoe UI"), FontSize = 11, Background = Brushes.White, Tag = "a4",
        };
        var composition = sale.CgstPaise + sale.SgstPaise + sale.IgstPaise == 0 && sale.InvoiceNumber.StartsWith("BOS", StringComparison.Ordinal);

        Paragraph P(string text, double size = 11, bool bold = false, TextAlignment al = TextAlignment.Left, Brush? color = null, Thickness? margin = null) =>
            new(new Run(text) { FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, Foreground = color ?? Brushes.Black })
            { TextAlignment = al, Margin = margin ?? new Thickness(0, 0, 0, 1) };

        // ---- heading: business on the left, document on the right ----
        var head = new Table { CellSpacing = 0 };
        head.Columns.Add(new TableColumn { Width = new GridLength(3, GridUnitType.Star) });
        head.Columns.Add(new TableColumn { Width = new GridLength(2, GridUnitType.Star) });
        var hg = new TableRowGroup(); head.RowGroups.Add(hg);
        var hr = new TableRow(); hg.Rows.Add(hr);
        var left = new TableCell { Padding = new Thickness(0, 0, 8, 6) };
        left.Blocks.Add(P(company?.Name ?? "Shop", 18, true));
        if (!string.IsNullOrWhiteSpace(company?.Address)) left.Blocks.Add(P(company!.Address!));
        if (!string.IsNullOrWhiteSpace(company?.Phone)) left.Blocks.Add(P("Phone: " + company!.Phone));
        if (!string.IsNullOrWhiteSpace(company?.Gstin)) left.Blocks.Add(P("GSTIN: " + company!.Gstin, 11, true));
        var right = new TableCell { Padding = new Thickness(8, 0, 0, 6) };
        right.Blocks.Add(P(composition ? "BILL OF SUPPLY" : "TAX INVOICE", 16, true, TextAlignment.Right));
        right.Blocks.Add(P($"No: {sale.InvoiceNumber}", 11, true, TextAlignment.Right));
        right.Blocks.Add(P($"Date: {sale.SaleDate:dd-MMM-yyyy}", 11, false, TextAlignment.Right));
        if (duplicate) right.Blocks.Add(P("DUPLICATE COPY", 12, true, TextAlignment.Right, Brushes.DarkRed));
        if (sale.Status == SaleStatus.Voided) right.Blocks.Add(P("CANCELLED", 14, true, TextAlignment.Right, Brushes.DarkRed));
        hr.Cells.Add(left); hr.Cells.Add(right);
        doc.Blocks.Add(head);

        // ---- buyer ----
        var buyer = new Table { CellSpacing = 0 };
        buyer.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var bg = new TableRowGroup(); buyer.RowGroups.Add(bg);
        var br = new TableRow(); bg.Rows.Add(br);
        var bc = new TableCell { BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 1), Padding = new Thickness(0, 6, 0, 6) };
        bc.Blocks.Add(P("Bill to", 9, false, TextAlignment.Left, Brushes.Gray));
        bc.Blocks.Add(P(string.IsNullOrWhiteSpace(sale.CustomerName) ? "Walk-in customer" : sale.CustomerName!, 12, true));
        if (!string.IsNullOrWhiteSpace(sale.CustomerGstin)) bc.Blocks.Add(P("GSTIN: " + sale.CustomerGstin));
        if (!composition) bc.Blocks.Add(P(sale.IsInterState ? "Supply: inter-state (IGST)" : "Supply: within the state (CGST + SGST)", 10, false, TextAlignment.Left, Brushes.Gray));
        br.Cells.Add(bc);
        doc.Blocks.Add(buyer);
        doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, 4) });

        // ---- items ----
        var cols = composition ? new[] { 0.4, 3.6, 1.0, 1.0, 1.2, 1.0, 1.4 } : new[] { 0.4, 2.8, 0.9, 0.9, 1.1, 0.9, 1.2, 0.7, 1.0, 1.2 };
        var items = new Table { CellSpacing = 0 };
        foreach (var w in cols) items.Columns.Add(new TableColumn { Width = new GridLength(w, GridUnitType.Star) });
        var ig = new TableRowGroup(); items.RowGroups.Add(ig);

        TableRow Row(string[] cells, bool header = false)
        {
            var row = new TableRow();
            if (header) row.Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xFA));
            for (var i = 0; i < cells.Length; i++)
            {
                var al = i == 1 ? TextAlignment.Left : (i == 0 || i == 2 ? TextAlignment.Left : TextAlignment.Right);
                row.Cells.Add(new TableCell(P(cells[i], header ? 10 : 10.5, header, al, null, new Thickness(0)))
                { BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 0.5), Padding = new Thickness(3, 3, 3, 3) });
            }
            return row;
        }
        ig.Rows.Add(Row(composition
            ? new[] { "#", "Description", "HSN", "Qty", "Price", "Disc.", "Amount" }
            : new[] { "#", "Description", "HSN", "Qty", "Price", "Disc.", "Taxable", "GST %", "GST", "Amount" }, header: true));
        var n = 1;
        foreach (var l in sale.Lines)
        {
            var qty = Qty.Format(l.QtyMilli) + " " + l.Unit;
            var cells = new List<string> { n++.ToString(), l.ProductName, l.HsnCode ?? "", qty, Money.FormatPlain(l.UnitPricePaise), l.DiscountPaise > 0 ? Money.FormatPlain(l.DiscountPaise) : "" };
            if (!composition) { cells.Add(Money.FormatPlain(l.TaxablePaise)); cells.Add(l.GstRateBp > 0 ? $"{l.GstRateBp / 100m:0.##}%" : "0%"); cells.Add(Money.FormatPlain(l.TaxPaise)); }
            cells.Add(Money.FormatPlain(l.LineTotalPaise));
            ig.Rows.Add(Row(cells.ToArray()));
        }
        doc.Blocks.Add(items);
        doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, 6) });

        // ---- tax by rate and totals, side by side ----
        var foot = new Table { CellSpacing = 0 };
        foot.Columns.Add(new TableColumn { Width = new GridLength(1.25, GridUnitType.Star) });
        foot.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var fg = new TableRowGroup(); foot.RowGroups.Add(fg);
        var fr = new TableRow(); fg.Rows.Add(fr);

        var summary = new TableCell { Padding = new Thickness(0, 0, 14, 0) };
        if (!composition)
        {
            summary.Blocks.Add(P("Tax summary", 10, true));
            var t = new Table { CellSpacing = 0 };
            foreach (var w in new[] { 1.2, 1.4, 1.4 }) t.Columns.Add(new TableColumn { Width = new GridLength(w, GridUnitType.Star) });
            var tg = new TableRowGroup(); t.RowGroups.Add(tg);
            TableRow R(string a, string b, string c, bool bold = false)
            {
                var row = new TableRow();
                row.Cells.Add(new TableCell(P(a, 10, bold, TextAlignment.Left, null, new Thickness(0))) { Padding = new Thickness(0, 1, 3, 1) });
                row.Cells.Add(new TableCell(P(b, 10, bold, TextAlignment.Right, null, new Thickness(0))) { Padding = new Thickness(3, 1, 3, 1) });
                row.Cells.Add(new TableCell(P(c, 10, bold, TextAlignment.Right, null, new Thickness(0))) { Padding = new Thickness(3, 1, 0, 1) });
                return row;
            }
            tg.Rows.Add(R("GST rate", "Taxable value", "Tax", true));
            foreach (var g in sale.Lines.GroupBy(l => l.GstRateBp).OrderBy(g => g.Key))
                tg.Rows.Add(R(g.Key == 0 ? "0% (nil)" : sale.IsInterState ? $"IGST {g.Key / 100m:0.##}%" : $"CGST+SGST {g.Key / 100m:0.##}%",
                    Money.FormatPlain(g.Sum(l => l.TaxablePaise)), Money.FormatPlain(g.Sum(l => l.TaxPaise))));
            summary.Blocks.Add(t);
        }
        summary.Blocks.Add(P(""));
        summary.Blocks.Add(P("Amount in words", 9, false, TextAlignment.Left, Brushes.Gray));
        summary.Blocks.Add(P(AmountInWords.Rupees(sale.TotalPaise), 11, true));
        if (sale.Payments.Count > 0)
        {
            summary.Blocks.Add(P(""));
            summary.Blocks.Add(P("Paid by: " + string.Join(", ", sale.Payments.Select(p =>
                $"{(p.Mode switch { PaymentMode.Cash => "Cash", PaymentMode.Upi => "UPI", PaymentMode.Card => "Card", _ => "On credit" })} {Money.FormatPlain(p.AmountPaise)}")), 10));
        }

        var totals = new TableCell();
        var tt = new Table { CellSpacing = 0 };
        tt.Columns.Add(new TableColumn { Width = new GridLength(1.4, GridUnitType.Star) });
        tt.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var ttg = new TableRowGroup(); tt.RowGroups.Add(ttg);
        void Total(string label, long paise, bool bold = false, double size = 11)
        {
            var row = new TableRow();
            row.Cells.Add(new TableCell(P(label, size, bold, TextAlignment.Left, null, new Thickness(0))) { Padding = new Thickness(0, 2, 6, 2) });
            row.Cells.Add(new TableCell(P(Money.FormatPlain(paise), size, bold, TextAlignment.Right, null, new Thickness(0))) { Padding = new Thickness(0, 2, 0, 2) });
            ttg.Rows.Add(row);
        }
        if (!composition)
        {
            Total("Taxable value", sale.TaxablePaise);
            if (sale.IsInterState) Total("IGST", sale.IgstPaise);
            else { Total("CGST", sale.CgstPaise); Total("SGST", sale.SgstPaise); }
        }
        if (sale.DiscountPaise > 0) Total("Total discount given", sale.DiscountPaise);
        if (sale.RoundOffPaise != 0) Total("Round off", sale.RoundOffPaise);
        Total("TOTAL", sale.TotalPaise, true, 14);
        totals.Blocks.Add(tt);
        fr.Cells.Add(summary); fr.Cells.Add(totals);
        doc.Blocks.Add(foot);

        // ---- declaration and signature ----
        doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 14, 0, 0) });
        doc.Blocks.Add(P(composition ? "Composition taxable person, not eligible to collect tax on supplies." : "Goods once sold are not returnable except as agreed at the time of sale.", 9, false, TextAlignment.Left, Brushes.Gray));
        doc.Blocks.Add(P($"For {company?.Name ?? "the business"}", 11, true, TextAlignment.Right, null, new Thickness(0, 28, 0, 0)));
        doc.Blocks.Add(P("Authorised signatory", 9, false, TextAlignment.Right, Brushes.Gray));
        doc.Blocks.Add(P("This is a computer generated invoice.", 9, false, TextAlignment.Center, Brushes.Gray, new Thickness(0, 14, 0, 0)));
        return doc;
    }
}
