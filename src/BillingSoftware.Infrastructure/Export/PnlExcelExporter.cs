using BillingSoftware.Application;
using ClosedXML.Excel;

namespace BillingSoftware.Infrastructure.Export;

/// <summary>Profit and loss as an Excel file for the accountant: the statement, profit by item, and the notes. Money is written as real numbers (rupees).</summary>
public static class PnlExcelExporter
{
    private const string MoneyFormat = "#,##0.00";

    public static void Write(ProfitLossReport r, string path)
    {
        using var wb = new XLWorkbook();
        Statement(wb, r);
        Items(wb, r);
        Notes(wb, r);
        wb.SaveAs(path);
    }

    private static string Range(PnlColumn c) => $"{c.From:dd-MMM-yyyy} to {c.To:dd-MMM-yyyy}";

    private static void Statement(XLWorkbook wb, ProfitLossReport r)
    {
        var ws = wb.Worksheets.Add("Profit and loss");
        ws.Cell(1, 1).Value = r.CompanyName; ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = "Profit and loss statement";
        ws.Cell(3, 1).Value = "Read from the books. For the accountant to review.";
        ws.Cell(3, 1).Style.Font.Italic = true;

        var h = new[] { "", Range(r.Current), Range(r.Previous), "Change" };
        for (var i = 0; i < 4; i++)
        {
            var cell = ws.Cell(5, i + 1);
            cell.Value = h[i]; cell.Style.Font.Bold = true; cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F7");
        }

        var row = 6;
        foreach (var x in PnlStatement.Rows(r))
        {
            ws.Cell(row, 1).Value = x.Label;
            if (x.Kind != PnlRowKind.Heading)
            {
                if (x.Kind == PnlRowKind.Percent)
                {
                    ws.Cell(row, 2).Value = x.Current / 10000.0; ws.Cell(row, 3).Value = x.Previous / 10000.0;
                    ws.Range(row, 2, row, 3).Style.NumberFormat.Format = "0.0%";
                }
                else
                {
                    ws.Cell(row, 2).Value = x.Current / 100m; ws.Cell(row, 3).Value = x.Previous / 100m; ws.Cell(row, 4).Value = (x.Current - x.Previous) / 100m;
                    ws.Range(row, 2, row, 4).Style.NumberFormat.Format = MoneyFormat;
                }
            }
            if (x.Kind is PnlRowKind.Heading or PnlRowKind.Total or PnlRowKind.Subtotal) ws.Range(row, 1, row, 4).Style.Font.Bold = true;
            if (x.Kind == PnlRowKind.Heading) ws.Range(row, 1, row, 4).Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF2FA");
            if (x.Kind == PnlRowKind.Total) ws.Range(row, 1, row, 4).Style.Border.TopBorder = XLBorderStyleValues.Thin;
            row++;
        }
        ws.Column(1).Width = 46;
        for (var c = 2; c <= 4; c++) ws.Column(c).Width = 28;
    }

    private static void Items(XLWorkbook wb, ProfitLossReport r)
    {
        var ws = wb.Worksheets.Add("Profit by item");
        var h = new[] { "Item", "Quantity", "Sales (before GST)", "Cost", "Profit", "Margin %" };
        for (var i = 0; i < h.Length; i++) { var c = ws.Cell(1, i + 1); c.Value = h[i]; c.Style.Font.Bold = true; c.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F7"); }
        var row = 2;
        foreach (var i in r.Items)
        {
            ws.Cell(row, 1).Value = i.Name;
            ws.Cell(row, 2).Value = i.QtyMilli / 1000.0; ws.Cell(row, 2).Style.NumberFormat.Format = "0.###";
            ws.Cell(row, 3).Value = i.SalesPaise / 100m; ws.Cell(row, 4).Value = i.CostPaise / 100m; ws.Cell(row, 5).Value = i.ProfitPaise / 100m;
            ws.Range(row, 3, row, 5).Style.NumberFormat.Format = MoneyFormat;
            ws.Cell(row, 6).Value = i.MarginBp / 10000.0; ws.Cell(row, 6).Style.NumberFormat.Format = "0.0%";
            row++;
        }
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();
    }

    private static void Notes(XLWorkbook wb, ProfitLossReport r)
    {
        var ws = wb.Worksheets.Add("Notes");
        ws.Cell(1, 1).Value = "How to read this"; ws.Cell(1, 1).Style.Font.Bold = true;
        var row = 2;
        foreach (var n in r.Notes) { ws.Cell(row, 1).Value = n; ws.Cell(row, 1).Style.Alignment.WrapText = true; row++; }
        ws.Column(1).Width = 120;
    }
}
