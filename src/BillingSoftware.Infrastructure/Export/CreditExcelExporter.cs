using BillingSoftware.Application;
using ClosedXML.Excel;

namespace BillingSoftware.Infrastructure.Export;

/// <summary>Who owes what (ageing) and a customer's statement as Excel files. Money is written as real numbers (rupees).</summary>
public static class CreditExcelExporter
{
    private const string MoneyFormat = "#,##0.00";

    private static void Header(IXLWorksheet ws, int row, params string[] titles)
    {
        for (var i = 0; i < titles.Length; i++)
        {
            var c = ws.Cell(row, i + 1); c.Value = titles[i]; c.Style.Font.Bold = true; c.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F7");
        }
    }

    public static void WriteAgeing(AgeingReport r, string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Who owes you");
        ws.Cell(1, 1).Value = r.CompanyName; ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = $"Customers who owe money, as at {r.AsAt:dd-MMM-yyyy}";
        ws.Cell(3, 1).Value = "Payments and credit notes are taken against the oldest amounts first.";
        ws.Cell(3, 1).Style.Font.Italic = true;
        Header(ws, 5, "Customer", "Phone", "Owes", "0-30 days", "31-60 days", "61-90 days", "Over 90 days", "Oldest unpaid", "Days", "Credit limit", "Over limit");
        var row = 6;
        foreach (var x in r.Rows)
        {
            ws.Cell(row, 1).Value = x.Name; ws.Cell(row, 2).Value = x.Phone ?? "";
            ws.Cell(row, 3).Value = x.OutstandingPaise / 100m; ws.Cell(row, 4).Value = x.CurrentPaise / 100m; ws.Cell(row, 5).Value = x.Days31To60Paise / 100m;
            ws.Cell(row, 6).Value = x.Days61To90Paise / 100m; ws.Cell(row, 7).Value = x.Over90Paise / 100m;
            if (x.OldestUnpaid.HasValue) ws.Cell(row, 8).Value = x.OldestUnpaid.Value.Date;
            ws.Cell(row, 8).Style.DateFormat.Format = "dd-MMM-yyyy";
            ws.Cell(row, 9).Value = x.OldestDays; ws.Cell(row, 10).Value = x.CreditLimitPaise / 100m; ws.Cell(row, 11).Value = x.OverLimit ? "Yes" : "";
            ws.Range(row, 3, row, 7).Style.NumberFormat.Format = MoneyFormat; ws.Cell(row, 10).Style.NumberFormat.Format = MoneyFormat;
            row++;
        }
        ws.Cell(row, 1).Value = "Total"; ws.Cell(row, 3).Value = r.TotalPaise / 100m; ws.Cell(row, 4).Value = r.CurrentPaise / 100m; ws.Cell(row, 5).Value = r.Days31To60Paise / 100m;
        ws.Cell(row, 6).Value = r.Days61To90Paise / 100m; ws.Cell(row, 7).Value = r.Over90Paise / 100m;
        ws.Range(row, 1, row, 11).Style.Font.Bold = true; ws.Range(row, 3, row, 7).Style.NumberFormat.Format = MoneyFormat;
        ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }

    public static void WriteStatement(CustomerStatement s, string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Statement");
        ws.Cell(1, 1).Value = s.CompanyName; ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = $"Statement of account: {s.Name}";
        ws.Cell(3, 1).Value = $"{s.From:dd-MMM-yyyy} to {s.To:dd-MMM-yyyy}";
        Header(ws, 5, "Date", "Type", "Reference", "Details", "Debit (owed)", "Credit (paid)", "Balance");
        ws.Cell(6, 4).Value = "Balance brought forward"; ws.Cell(6, 7).Value = s.BroughtForwardPaise / 100m;
        var row = 7;
        foreach (var e in s.Entries)
        {
            ws.Cell(row, 1).Value = e.Date.Date; ws.Cell(row, 1).Style.DateFormat.Format = "dd-MMM-yyyy";
            ws.Cell(row, 2).Value = e.Kind; ws.Cell(row, 3).Value = e.Reference; ws.Cell(row, 4).Value = e.Description;
            ws.Cell(row, 5).Value = e.DebitPaise / 100m; ws.Cell(row, 6).Value = e.CreditPaise / 100m; ws.Cell(row, 7).Value = e.BalancePaise / 100m;
            row++;
        }
        ws.Cell(row, 4).Value = "Closing balance"; ws.Cell(row, 5).Value = s.TotalDebitPaise / 100m; ws.Cell(row, 6).Value = s.TotalCreditPaise / 100m; ws.Cell(row, 7).Value = s.ClosingPaise / 100m;
        ws.Range(row, 1, row, 7).Style.Font.Bold = true;
        ws.Range(6, 5, row, 7).Style.NumberFormat.Format = MoneyFormat;
        ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }
}
