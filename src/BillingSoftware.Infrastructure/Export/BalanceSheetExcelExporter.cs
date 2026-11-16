using BillingSoftware.Application;
using ClosedXML.Excel;

namespace BillingSoftware.Infrastructure.Export;

/// <summary>Balance sheet as an Excel file for the accountant: the statement, the checks against the records, and the notes. Money is written as real numbers (rupees).</summary>
public static class BalanceSheetExcelExporter
{
    private const string MoneyFormat = "#,##0.00";

    public static void Write(BalanceSheetReport r, string path)
    {
        using var wb = new XLWorkbook();
        Statement(wb, r);
        Checks(wb, r);
        Notes(wb, r);
        wb.SaveAs(path);
    }

    private static void Statement(XLWorkbook wb, BalanceSheetReport r)
    {
        var ws = wb.Worksheets.Add("Balance sheet");
        ws.Cell(1, 1).Value = r.CompanyName; ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = "Balance sheet";
        ws.Cell(3, 1).Value = "Read from the books. For the accountant to review.";
        ws.Cell(3, 1).Style.Font.Italic = true;
        var h = new[] { "", $"As at {r.Current.AsAt:dd-MMM-yyyy}", $"As at {r.Compare.AsAt:dd-MMM-yyyy}", "Change" };
        for (var i = 0; i < 4; i++) { var c = ws.Cell(5, i + 1); c.Value = h[i]; c.Style.Font.Bold = true; c.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F7"); }

        var row = 6;
        foreach (var x in BalanceSheetStatement.Rows(r))
        {
            ws.Cell(row, 1).Value = x.Label;
            if (x.Kind != PnlRowKind.Heading)
            {
                ws.Cell(row, 2).Value = x.Current / 100m; ws.Cell(row, 3).Value = x.Previous / 100m; ws.Cell(row, 4).Value = (x.Current - x.Previous) / 100m;
                ws.Range(row, 2, row, 4).Style.NumberFormat.Format = MoneyFormat;
            }
            if (x.Kind is PnlRowKind.Heading or PnlRowKind.Subtotal or PnlRowKind.Total) ws.Range(row, 1, row, 4).Style.Font.Bold = true;
            if (x.Kind == PnlRowKind.Heading) ws.Range(row, 1, row, 4).Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF2FA");
            if (x.Kind == PnlRowKind.Total) ws.Range(row, 1, row, 4).Style.Border.TopBorder = XLBorderStyleValues.Thin;
            row++;
        }
        ws.Cell(row + 1, 1).Value = "Difference (assets less liabilities less owner's funds; must be zero)";
        ws.Cell(row + 1, 2).Value = r.Current.DifferencePaise / 100m; ws.Cell(row + 1, 3).Value = r.Compare.DifferencePaise / 100m;
        ws.Range(row + 1, 2, row + 1, 3).Style.NumberFormat.Format = MoneyFormat;
        ws.Column(1).Width = 60;
        for (var c = 2; c <= 4; c++) ws.Column(c).Width = 24;
    }

    private static void Checks(XLWorkbook wb, BalanceSheetReport r)
    {
        var ws = wb.Worksheets.Add("Checks");
        var h = new[] { "Check", "Books", "Records", "Result", "Note" };
        for (var i = 0; i < h.Length; i++) { var c = ws.Cell(1, i + 1); c.Value = h[i]; c.Style.Font.Bold = true; c.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F7"); }
        var row = 2;
        foreach (var c in r.Checks)
        {
            ws.Cell(row, 1).Value = c.Name;
            ws.Cell(row, 2).Value = c.BooksPaise / 100m; ws.Cell(row, 3).Value = c.RecordsPaise / 100m;
            ws.Range(row, 2, row, 3).Style.NumberFormat.Format = MoneyFormat;
            ws.Cell(row, 4).Value = c.Ok ? "OK" : "CHECK";
            ws.Cell(row, 5).Value = c.Message;
            row++;
        }
        row++;
        foreach (var w in r.Warnings) { ws.Cell(row, 1).Value = w; ws.Cell(row, 1).Style.Font.FontColor = XLColor.FromHtml("#B3261E"); row++; }
        ws.Columns().AdjustToContents();
    }

    private static void Notes(XLWorkbook wb, BalanceSheetReport r)
    {
        var ws = wb.Worksheets.Add("Notes");
        ws.Cell(1, 1).Value = "How to read this"; ws.Cell(1, 1).Style.Font.Bold = true;
        var row = 2;
        foreach (var n in r.Notes) { ws.Cell(row, 1).Value = n; ws.Cell(row, 1).Style.Alignment.WrapText = true; row++; }
        ws.Column(1).Width = 120;
    }
}
