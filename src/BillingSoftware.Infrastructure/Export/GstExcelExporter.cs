using BillingSoftware.Application;
using BillingSoftware.Domain;
using ClosedXML.Excel;

namespace BillingSoftware.Infrastructure.Export;

/// <summary>
/// Writes the GST worksheets to one Excel file for the accountant: summary, GSTR-1 tables, registers and the list of checks.
/// Money is written as real numbers (rupees), so the accountant can add, filter and check it.
/// </summary>
public static class GstExcelExporter
{
    private const string MoneyFormat = "#,##0.00";
    private const string DateFormat = "dd-mmm-yyyy";

    private static decimal R(long paise) => paise / 100m;
    private static double Rate(int bp) => bp / 100.0;
    private static double Q(long milli) => milli / 1000.0;

    public static void Write(GstReport r, string path)
    {
        using var wb = new XLWorkbook();
        Summary(wb, r);
        Sheet(wb, "GSTR1 B2B", r.B2B, new[] { "GSTIN of customer", "Customer", "Invoice no.", "Invoice date", "Invoice value", "Place of supply", "Rate %", "Taxable value", "IGST", "CGST", "SGST" },
            x => x.Gstin, x => x.PartyName, x => x.DocNo, x => x.Date, x => R(x.DocValuePaise), x => x.PlaceOfSupply, x => Rate(x.RateBp), x => R(x.TaxablePaise), x => R(x.IgstPaise), x => R(x.CgstPaise), x => R(x.SgstPaise));
        Sheet(wb, "GSTR1 B2CL", r.B2Cl, new[] { "Invoice no.", "Invoice date", "Invoice value", "Place of supply", "Rate %", "Taxable value", "IGST" },
            x => x.DocNo, x => x.Date, x => R(x.DocValuePaise), x => x.PlaceOfSupply, x => Rate(x.RateBp), x => R(x.TaxablePaise), x => R(x.IgstPaise));
        Sheet(wb, "GSTR1 B2CS", r.B2Cs, new[] { "Place of supply", "Rate %", "Taxable value", "IGST", "CGST", "SGST" },
            x => x.PlaceOfSupply, x => Rate(x.RateBp), x => R(x.TaxablePaise), x => R(x.IgstPaise), x => R(x.CgstPaise), x => R(x.SgstPaise));
        Sheet(wb, "GSTR1 Credit notes", r.Notes, new[] { "Type", "GSTIN of customer", "Customer", "Credit note no.", "Credit note date", "Original invoice", "Original date", "Note value", "Place of supply", "Rate %", "Taxable value", "IGST", "CGST", "SGST" },
            x => x.Kind, x => x.Gstin, x => x.PartyName, x => x.NoteNo, x => x.NoteDate, x => x.OriginalNo, x => x.OriginalDate, x => R(x.NoteValuePaise), x => x.PlaceOfSupply, x => Rate(x.RateBp), x => R(x.TaxablePaise), x => R(x.IgstPaise), x => R(x.CgstPaise), x => R(x.SgstPaise));
        Sheet(wb, "GSTR1 HSN", r.Hsn, new[] { "HSN", "Unit", "UQC", "Rate %", "Qty sold", "Taxable sold", "Tax sold", "Value sold", "Qty returned", "Taxable returned", "Tax returned", "Value returned" },
            x => x.Hsn, x => x.Unit, x => x.Uqc, x => Rate(x.RateBp), x => Q(x.SoldQtyMilli), x => R(x.SoldTaxablePaise), x => R(x.SoldTaxPaise), x => R(x.SoldValuePaise),
            x => Q(x.ReturnQtyMilli), x => R(x.ReturnTaxablePaise), x => R(x.ReturnTaxPaise), x => R(x.ReturnValuePaise));
        Sheet(wb, "GSTR1 Documents", r.Documents, new[] { "Document type", "From", "To", "Total issued", "Cancelled" },
            x => x.Description, x => x.From, x => x.To, x => (double)x.Total, x => (double)x.Cancelled);
        Sheet(wb, "Nil rated", r.NilRated, new[] { "Description", "Taxable value" }, x => x.Description, x => R(x.TaxablePaise));
        Sheet(wb, "Sales register", r.SalesRegister, new[] { "Document no.", "Date", "Customer", "GSTIN", "Status", "Place of supply", "Taxable value", "CGST", "SGST", "IGST", "Round off", "Total" },
            x => x.DocNo, x => x.Date, x => x.Customer, x => x.Gstin ?? "", x => x.Status, x => x.PlaceOfSupply, x => R(x.TaxablePaise), x => R(x.CgstPaise), x => R(x.SgstPaise), x => R(x.IgstPaise), x => R(x.RoundOffPaise), x => R(x.TotalPaise));
        Sheet(wb, "Purchase register", r.PurchaseRegister, new[] { "Purchase no.", "Date", "Supplier", "Supplier GSTIN", "Supplier invoice", "Supplier invoice date", "Taxable value", "CGST", "SGST", "IGST", "Credit claimed", "Credit not claimed", "Total" },
            x => x.PurchaseNo, x => x.Date, x => x.Supplier, x => x.SupplierGstin ?? "", x => x.SupplierInvoiceNo, x => x.SupplierInvoiceDate, x => R(x.TaxablePaise), x => R(x.CgstPaise), x => R(x.SgstPaise), x => R(x.IgstPaise), x => R(x.ItcClaimedPaise), x => R(x.ItcNotClaimedPaise), x => R(x.TotalPaise));
        Sheet(wb, "Credit and debit notes", r.NoteRegister, new[] { "Type", "Note no.", "Date", "Against", "Party", "GSTIN", "Taxable value", "CGST", "SGST", "IGST", "Total" },
            x => x.Kind, x => x.NoteNo, x => x.Date, x => x.Against, x => x.Party, x => x.Gstin ?? "", x => R(x.TaxablePaise), x => R(x.CgstPaise), x => R(x.SgstPaise), x => R(x.IgstPaise), x => R(x.TotalPaise));
        Checks(wb, r);
        wb.SaveAs(path);
    }

    private static void Summary(XLWorkbook wb, GstReport r)
    {
        var ws = wb.Worksheets.Add("Summary 3B");
        var row = 1;
        ws.Cell(row, 1).Value = r.CompanyName; ws.Cell(row, 1).Style.Font.Bold = true; ws.Cell(row, 1).Style.Font.FontSize = 14; row++;
        ws.Cell(row++, 1).Value = $"GSTIN: {r.CompanyGstin ?? "not set"}    State code: {r.StateCode}";
        ws.Cell(row++, 1).Value = $"Return period: {r.PeriodLabel}";
        ws.Cell(row, 1).Value = "Worksheet for the accountant to review. Nothing here has been filed.";
        ws.Cell(row, 1).Style.Font.Italic = true; row += 2;

        if (r.IsComposition)
        {
            ws.Cell(row, 1).Value = "Composition scheme: turnover for the period (after returns)"; ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = R(r.CompositionTurnoverPaise); ws.Cell(row, 2).Style.NumberFormat.Format = MoneyFormat;
            ws.Columns().AdjustToContents();
            return;
        }

        void Header(string a, string b, string c, string d, string e)
        {
            var values = new[] { a, b, c, d, e };
            for (var i = 0; i < 5; i++) { var cell = ws.Cell(row, i + 1); cell.Value = values[i]; cell.Style.Font.Bold = true; cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F7"); }
            row++;
        }
        void Line(string label, long taxable, long igst, long cgst, long sgst, bool bold = false)
        {
            ws.Cell(row, 1).Value = label;
            var vals = new[] { taxable, igst, cgst, sgst };
            for (var i = 0; i < 4; i++) { var cell = ws.Cell(row, i + 2); cell.Value = R(vals[i]); cell.Style.NumberFormat.Format = MoneyFormat; }
            if (bold) ws.Range(row, 1, row, 5).Style.Font.Bold = true;
            row++;
        }
        void Section(string title) { ws.Cell(row, 1).Value = title; ws.Cell(row, 1).Style.Font.Bold = true; ws.Cell(row, 1).Style.Font.FontSize = 12; row++; }

        var g = r.Gstr3b;
        Section("3.1 Outward supplies (after credit notes)");
        Header("", "Taxable value", "IGST", "CGST", "SGST");
        Line("(a) Taxable supplies other than zero-rated, nil-rated and exempt", g.OutTaxablePaise, g.OutIgstPaise, g.OutCgstPaise, g.OutSgstPaise);
        Line("(c) Nil-rated and exempt supplies (0% items; accountant to classify)", g.NilRatedTaxablePaise, 0, 0, 0);
        row++;
        Section("3.2 Inter-state supplies to unregistered customers, by place of supply");
        Header("Place of supply", "Taxable value", "IGST", "", "");
        foreach (var p in g.InterStateToUnregistered) Line(p.PlaceOfSupply, p.TaxablePaise, p.IgstPaise, 0, 0);
        row++;
        Section("4 Input tax credit");
        Header("", "", "IGST", "CGST", "SGST");
        Line("(A)(5) Credit claimed on purchases (after debit notes)", 0, g.ItcIgstPaise, g.ItcCgstPaise, g.ItcSgstPaise);
        Line("Tax on purchases where credit was not claimed (for the accountant to classify)", 0, g.IneligibleIgstPaise, g.IneligibleCgstPaise, g.IneligibleSgstPaise);
        row++;
        Section("5 Inward supplies at 0%");
        Line("From inside the state", g.InwardNilIntraPaise, 0, 0, 0);
        Line("From other states", g.InwardNilInterPaise, 0, 0, 0);
        row++;
        Section("Net tax by head (output tax less input credit; negative = credit left over)");
        Header("", "", "IGST", "CGST", "SGST");
        Line("Net", 0, g.NetIgstPaise, g.NetCgstPaise, g.NetSgstPaise, bold: true);
        ws.Cell(row, 1).Value = "Set-off of one tax head against another is not applied. The accountant decides the final payment."; ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Column(1).Width = 78;
        for (var c = 2; c <= 5; c++) ws.Column(c).Width = 16;
    }

    private static void Checks(XLWorkbook wb, GstReport r)
    {
        var ws = wb.Worksheets.Add("Checks and notes");
        ws.Cell(1, 1).Value = "Things to check before using these figures"; ws.Cell(1, 1).Style.Font.Bold = true;
        var row = 2;
        foreach (var w in r.Warnings) { ws.Cell(row, 1).Value = w; ws.Cell(row, 1).Style.Alignment.WrapText = true; row++; }
        ws.Column(1).Width = 120;
    }

    private static void Sheet<T>(XLWorkbook wb, string name, IEnumerable<T> rows, string[] headers, params Func<T, object?>[] columns)
    {
        var ws = wb.Worksheets.Add(name);
        for (var c = 0; c < headers.Length; c++)
        {
            var h = ws.Cell(1, c + 1);
            h.Value = headers[c]; h.Style.Font.Bold = true; h.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F7");
        }
        var r = 2;
        foreach (var item in rows)
        {
            for (var c = 0; c < columns.Length; c++)
            {
                var cell = ws.Cell(r, c + 1);
                switch (columns[c](item))
                {
                    case null: break;
                    case decimal d: cell.Value = d; cell.Style.NumberFormat.Format = MoneyFormat; break;
                    case double d: cell.Value = d; cell.Style.NumberFormat.Format = "0.###"; break;
                    case DateTime dt: cell.Value = dt; cell.Style.NumberFormat.Format = DateFormat; break;
                    case string s: cell.Value = s; break;
                    default: cell.Value = columns[c](item)!.ToString(); break;
                }
            }
            r++;
        }
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();
    }
}
