using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using BillingSoftware.Application;
using BillingSoftware.Domain;

namespace BillingSoftware.Desktop.Services;

/// <summary>Printable credit notes, debit notes and purchase vouchers.</summary>
public static class OtherDocuments
{
    /// <summary>A document laid out on A4 pages: tagged so printing asks for (or uses) the document printer instead of the receipt roll.</summary>
    private static Doc A4Doc()
    {
        var d = new Doc(LabelBuilder.A4Width);
        d.Flow.PageHeight = LabelBuilder.A4Height; d.Flow.PagePadding = new Thickness(40); d.Flow.Tag = "a4";
        return d;
    }

    private sealed class Doc
    {
        public FlowDocument Flow { get; }
        public Doc(double width)
        {
            Flow = new FlowDocument
            {
                PageWidth = width, ColumnWidth = width, PagePadding = new Thickness(10), FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11, Background = Brushes.White,
            };
        }
        public void Center(string text, double size = 11, bool bold = false) =>
            Flow.Blocks.Add(new Paragraph(new Run(text) { FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal })
            { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 1) });
        public void Line(string text, bool bold = false) =>
            Flow.Blocks.Add(new Paragraph(new Run(text) { FontWeight = bold ? FontWeights.Bold : FontWeights.Normal }) { Margin = new Thickness(0, 0, 0, 1) });
        public void Rule() => Flow.Blocks.Add(new Paragraph(new Run(new string('-', (int)Math.Clamp(Flow.PageWidth / 5.5, 24, 140)))) { Margin = new Thickness(0), FontSize = 9, Foreground = Brushes.Gray });

        /// <summary>A table; columns after the first are right-aligned numbers.</summary>
        public void Grid(double[] stars, IEnumerable<string[]> rows, bool headerBold = true, double size = 11)
        {
            var table = new Table { CellSpacing = 0 };
            foreach (var w in stars) table.Columns.Add(new TableColumn { Width = new GridLength(w, GridUnitType.Star) });
            var group = new TableRowGroup();
            table.RowGroups.Add(group);
            var first = true;
            foreach (var cells in rows)
            {
                var row = new TableRow();
                for (var i = 0; i < cells.Length; i++)
                    row.Cells.Add(new TableCell(new Paragraph(new Run(cells[i]) { FontSize = size, FontWeight = first && headerBold ? FontWeights.Bold : FontWeights.Normal })
                    { TextAlignment = i == 0 ? TextAlignment.Left : TextAlignment.Right, Margin = new Thickness(0, 0, 0, 1) }));
                group.Rows.Add(row);
                first = false;
            }
            Flow.Blocks.Add(table);
        }
    }

    private static string Mode(PaymentMode m) => m switch { PaymentMode.Cash => "Cash", PaymentMode.Upi => "UPI", PaymentMode.Card => "Card", _ => "On account" };

    public static FlowDocument BuildReturn(ReturnDetailDto r, CompanyDto? company)
    {
        var d = new Doc(ReceiptBuilder.PaperWidth);
        d.Center(company?.Name ?? "Shop", 15, true);
        if (!string.IsNullOrWhiteSpace(company?.Gstin)) d.Center("GSTIN: " + company!.Gstin);
        d.Center(r.Kind.ToUpperInvariant(), 13, true);
        d.Rule();
        d.Line($"No: {r.ReturnNumber}", true);
        d.Line($"Date: {r.Date:dd-MMM-yyyy HH:mm}");
        d.Line($"Against: {r.OriginalNumber}");
        d.Line($"{(r.Kind == "Credit note" ? "Customer" : "Supplier")}: {r.PartyName}");
        d.Rule();
        var rows = new List<string[]> { new[] { "Item", "Qty", "Amount" } };
        foreach (var l in r.Lines) rows.Add(new[] { l.Name, Qty.Format(l.QtyMilli), Money.FormatPlain(l.TotalPaise) });
        d.Grid(new[] { 3.4, 1.0, 1.8 }, rows);
        d.Rule();
        var t = new List<string[]> { new[] { "Taxable value", Money.FormatPlain(r.TaxablePaise) } };
        if (r.IgstPaise != 0) t.Add(new[] { "IGST", Money.FormatPlain(r.IgstPaise) });
        else { t.Add(new[] { "CGST", Money.FormatPlain(r.CgstPaise) }); t.Add(new[] { "SGST", Money.FormatPlain(r.SgstPaise) }); }
        if (r.RoundOffPaise != 0) t.Add(new[] { "Round off", Money.FormatPlain(r.RoundOffPaise) });
        t.Add(new[] { r.Kind == "Credit note" ? "REFUND" : "AMOUNT", Money.FormatPlain(r.TotalPaise) });
        d.Grid(new[] { 2.4, 1.8 }, t, headerBold: false);
        d.Rule();
        d.Line((r.Kind == "Credit note" ? "Refunded by: " : "Settled by: ") + Mode(r.RefundMode));
        d.Line("Reason: " + r.Reason);
        return d.Flow;
    }

    /// <summary>A customer's statement of account on A4 pages (a fixed page height makes it print page by page).</summary>
    public static FlowDocument BuildStatement(CustomerStatement s)
    {
        var d = A4Doc();
        d.Center(s.CompanyName, 15, true);
        d.Center("STATEMENT OF ACCOUNT", 13, true);
        d.Center($"{s.From:dd-MMM-yyyy} to {s.To:dd-MMM-yyyy}", 11);
        d.Rule();
        d.Line(s.Name, true);
        if (!string.IsNullOrWhiteSpace(s.Phone)) d.Line("Phone: " + s.Phone);
        if (!string.IsNullOrWhiteSpace(s.Gstin)) d.Line("GSTIN: " + s.Gstin);
        d.Line("");
        var rows = new List<string[]> { new[] { "Details", "Owed", "Paid", "Balance" } };
        rows.Add(new[] { "Balance brought forward", "", "", Money.FormatPlain(s.BroughtForwardPaise) });
        foreach (var e in s.Entries)
            rows.Add(new[] { $"{e.Date:dd-MMM-yy}   {e.Kind} {e.Reference}" + (string.IsNullOrWhiteSpace(e.Description) ? "" : $": {e.Description}"),
                e.DebitPaise == 0 ? "" : Money.FormatPlain(e.DebitPaise), e.CreditPaise == 0 ? "" : Money.FormatPlain(e.CreditPaise), Money.FormatPlain(e.BalancePaise) });
        rows.Add(new[] { "Closing balance", Money.FormatPlain(s.TotalDebitPaise), Money.FormatPlain(s.TotalCreditPaise), Money.FormatPlain(s.ClosingPaise) });
        d.Grid(new[] { 5.4, 1.2, 1.2, 1.4 }, rows);
        d.Rule();
        d.Line($"Amount due on {s.To:dd-MMM-yyyy}: {Money.Format(s.ClosingPaise)}", true);
        if (s.CreditLimitPaise > 0) d.Line($"Credit limit: {Money.Format(s.CreditLimitPaise)}");
        d.Line("");
        d.Line("Please tell us if anything here does not match your records.");
        return d.Flow;
    }

    /// <summary>A polite reminder listing the bills that are still unpaid.</summary>
    public static FlowDocument BuildReminder(CustomerReminder r)
    {
        var d = A4Doc();
        d.Center(r.CompanyName, 15, true);
        d.Center("PAYMENT REMINDER", 13, true);
        d.Rule();
        d.Line($"Date: {r.AsAt:dd-MMM-yyyy}");
        d.Line($"To: {r.CustomerName}", true);
        d.Line("");
        d.Line($"Dear {r.CustomerName},");
        d.Line($"Our records show that {Money.Format(r.OutstandingPaise)} is unpaid on your account as at {r.AsAt:dd-MMM-yyyy}. The details are below.");
        d.Line("");
        var rows = new List<string[]> { new[] { "Bill", "Date", "Days", "Unpaid" } };
        foreach (var i in r.Items) rows.Add(new[] { i.Reference, i.Date.ToString("dd-MMM-yy"), i.Days.ToString(), Money.FormatPlain(i.UnpaidPaise) });
        rows.Add(new[] { "Total unpaid", "", "", Money.FormatPlain(r.OutstandingPaise) });
        d.Grid(new[] { 3.4, 1.4, 0.9, 1.5 }, rows);
        d.Rule();
        d.Line("Please arrange the payment at your earliest convenience. If you have already paid, please ignore this reminder and let us know so we can correct our records.");
        d.Line("");
        if (!string.IsNullOrWhiteSpace(r.CompanyPhone)) d.Line("You can reach us on " + r.CompanyPhone + ".");
        d.Line("Thank you.");
        return d.Flow;
    }

    public static FlowDocument BuildBalanceSheet(BalanceSheetReport r)
    {
        var d = A4Doc();
        d.Center(r.CompanyName, 15, true);
        d.Center("BALANCE SHEET", 13, true);
        d.Center($"As at {r.Current.AsAt:dd-MMM-yyyy}", 11);
        d.Center("Read from the books. For the owner and the accountant to review.", 10);
        d.Rule();
        var cols = new[] { 3.6, 1.5, 1.5, 1.2 };
        d.Grid(cols, new[] { new[] { "", $"{r.Current.AsAt:dd-MMM-yy}", $"{r.Compare.AsAt:dd-MMM-yy}", "Change" } });
        foreach (var x in BalanceSheetStatement.Rows(r))
        {
            if (x.Kind == PnlRowKind.Heading) { d.Line(""); d.Line(x.Label.ToUpperInvariant(), true); continue; }
            d.Grid(cols, new[] { new[] { x.Label, Money.FormatPlain(x.Current), Money.FormatPlain(x.Previous), Money.FormatPlain(x.Current - x.Previous) } },
                headerBold: x.Kind is PnlRowKind.Total or PnlRowKind.Subtotal);
        }
        d.Rule();
        if (r.Current.DifferencePaise != 0) d.Line("DOES NOT BALANCE: difference " + Money.Format(r.Current.DifferencePaise), true);
        if (r.Warnings.Count + r.Checks.Count(c => !c.Ok) > 0)
        {
            d.Line("Things to check", true);
            foreach (var c in r.Checks.Where(c => !c.Ok)) d.Line($"- {c.Name}: {c.Message}");
            foreach (var w in r.Warnings) d.Line("- " + w);
        }
        d.Line("Notes", true);
        foreach (var n in r.Notes) d.Line("- " + n);
        return d.Flow;
    }

    public static FlowDocument BuildProfitLoss(ProfitLossReport r)
    {
        var d = A4Doc();
        d.Center(r.CompanyName, 15, true);
        d.Center("PROFIT AND LOSS", 13, true);
        d.Center("Read from the books. For the owner and the accountant to review.", 10);
        d.Rule();
        static string Pct(long bp) => (bp / 100m).ToString("0.0") + "%";
        var cols = new[] { 3.4, 1.5, 1.5, 1.3 };
        d.Grid(cols, new[] { new[] { "", $"{r.Current.From:dd-MMM-yy} to {r.Current.To:dd-MMM-yy}", $"{r.Previous.From:dd-MMM-yy} to {r.Previous.To:dd-MMM-yy}", "Change" } });
        foreach (var x in PnlStatement.Rows(r))
        {
            if (x.Kind == PnlRowKind.Heading) { d.Line(""); d.Line(x.Label.ToUpperInvariant(), true); continue; }
            var pct = x.Kind == PnlRowKind.Percent;
            var cells = new[]
            {
                x.Label,
                pct ? Pct(x.Current) : Money.FormatPlain(x.Current),
                pct ? Pct(x.Previous) : Money.FormatPlain(x.Previous),
                pct ? ((x.Current - x.Previous) / 100m).ToString("+0.0;-0.0;0.0") + " pts" : Money.FormatPlain(x.Current - x.Previous),
            };
            d.Grid(cols, new[] { cells }, headerBold: x.Kind is PnlRowKind.Total or PnlRowKind.Subtotal);
        }
        d.Rule();
        d.Line("Notes", true);
        foreach (var n in r.Notes) d.Line("- " + n);
        return d.Flow;
    }

    public static FlowDocument BuildGstSummary(GstReport r)
    {
        var d = A4Doc();
        d.Center(r.CompanyName, 15, true);
        d.Center($"GST summary for {r.PeriodLabel}", 13, true);
        d.Center($"GSTIN: {r.CompanyGstin ?? "not set"}   State code: {r.StateCode}", 10.5);
        d.Center("Worksheet for the accountant to review. Nothing here has been filed.", 10);
        d.Rule();
        if (r.IsComposition)
        {
            d.Line("Composition scheme: turnover for the month (after returns): " + Money.Format(r.CompositionTurnoverPaise), true);
        }
        else
        {
            var g = r.Gstr3b;
            d.Line("3.1 Outward supplies (after credit notes)", true);
            d.Grid(new[] { 3.4, 1.4, 1.1, 1.1, 1.1 }, new[]
            {
                new[] { "", "Taxable", "IGST", "CGST", "SGST" },
                new[] { "(a) Taxable supplies", Money.FormatPlain(g.OutTaxablePaise), Money.FormatPlain(g.OutIgstPaise), Money.FormatPlain(g.OutCgstPaise), Money.FormatPlain(g.OutSgstPaise) },
                new[] { "(c) Nil-rated / exempt", Money.FormatPlain(g.NilRatedTaxablePaise), "", "", "" },
            });
            d.Line("");
            d.Line("4 Input tax credit", true);
            d.Grid(new[] { 3.4, 1.4, 1.1, 1.1, 1.1 }, new[]
            {
                new[] { "", "", "IGST", "CGST", "SGST" },
                new[] { "(A)(5) Credit claimed", "", Money.FormatPlain(g.ItcIgstPaise), Money.FormatPlain(g.ItcCgstPaise), Money.FormatPlain(g.ItcSgstPaise) },
                new[] { "Tax where credit was not claimed", "", Money.FormatPlain(g.IneligibleIgstPaise), Money.FormatPlain(g.IneligibleCgstPaise), Money.FormatPlain(g.IneligibleSgstPaise) },
            });
            d.Line("");
            d.Line("Net tax by head (output tax less input credit; negative = credit left over)", true);
            d.Grid(new[] { 3.4, 1.4, 1.1, 1.1, 1.1 }, new[]
            {
                new[] { "", "", "IGST", "CGST", "SGST" },
                new[] { "Net", "", Money.FormatPlain(g.NetIgstPaise), Money.FormatPlain(g.NetCgstPaise), Money.FormatPlain(g.NetSgstPaise) },
            });
        }
        d.Rule();
        d.Line($"GSTR-1 tables: {r.B2B.Count} B2B rows, {r.B2Cl.Count} B2CL rows, {r.B2Cs.Count} B2CS rows, {r.Notes.Count} credit note rows, {r.Hsn.Count} HSN rows.");
        d.Line("Things to check", true);
        foreach (var w in r.Warnings) d.Line("- " + w);
        return d.Flow;
    }

    public static FlowDocument BuildPurchase(PurchaseDetailDto p, CompanyDto? company)
    {
        var d = A4Doc();
        d.Center(company?.Name ?? "Shop", 15, true);
        d.Center("PURCHASE", 13, true);
        d.Rule();
        d.Line($"No: {p.PurchaseNumber}      Date: {p.Date:dd-MMM-yyyy HH:mm}", true);
        d.Line($"Supplier: {p.SupplierName}");
        d.Line($"Supplier invoice: {p.SupplierInvoiceNo}" + (p.SupplierInvoiceDate.HasValue ? $"  dated {p.SupplierInvoiceDate:dd-MMM-yyyy}" : ""));
        d.Rule();
        var rows = new List<string[]> { new[] { "Item", "Qty", "Cost", "GST", "Taxable", "Tax", "Total", "Credit" } };
        foreach (var l in p.Lines)
            rows.Add(new[] { l.Name, Qty.Format(l.QtyMilli), Money.FormatPlain(l.UnitCostPaise), $"{l.GstRateBp / 100m:0.##}%", Money.FormatPlain(l.TaxablePaise),
                             Money.FormatPlain(l.TaxPaise), Money.FormatPlain(l.TotalPaise), l.ItcEligible ? "Yes" : "No" });
        d.Grid(new[] { 3.2, 0.9, 1.1, 0.8, 1.3, 1.1, 1.3, 0.9 }, rows, size: 10.5);
        d.Rule();
        var t = new List<string[]> { new[] { "Taxable value", Money.FormatPlain(p.TaxablePaise) } };
        if (p.IgstPaise != 0) t.Add(new[] { "IGST", Money.FormatPlain(p.IgstPaise) });
        else { t.Add(new[] { "CGST", Money.FormatPlain(p.CgstPaise) }); t.Add(new[] { "SGST", Money.FormatPlain(p.SgstPaise) }); }
        if (p.RoundOffPaise != 0) t.Add(new[] { "Round off", Money.FormatPlain(p.RoundOffPaise) });
        t.Add(new[] { "TOTAL", Money.FormatPlain(p.TotalPaise) });
        foreach (var pay in p.Payments) t.Add(new[] { Mode(pay.Mode), Money.FormatPlain(pay.AmountPaise) });
        d.Grid(new[] { 3.0, 1.6 }, t, headerBold: false);
        return d.Flow;
    }
}
