namespace BillingSoftware.Application;

public enum PnlRowKind { Heading, Line, Subtotal, Total, Percent }

/// <summary>One row of the printed statement. Values are paise, or basis points for a Percent row.</summary>
public sealed record PnlRow(string Label, long Current, long Previous, PnlRowKind Kind);

/// <summary>The one layout of the profit and loss statement, shared by the screen, the Excel file and the printout.</summary>
public static class PnlStatement
{
    public static IReadOnlyList<PnlRow> Rows(ProfitLossReport r)
    {
        var c = r.Current; var p = r.Previous;
        var rows = new List<PnlRow>
        {
            new("Income", 0, 0, PnlRowKind.Heading),
            new("Sales billed", c.GrossSalesPaise, p.GrossSalesPaise, PnlRowKind.Line),
            new("Less: bills voided", -c.VoidedPaise, -p.VoidedPaise, PnlRowKind.Line),
            new("Less: returns (credit notes)", -c.ReturnsPaise, -p.ReturnsPaise, PnlRowKind.Line),
            new("Net sales", c.NetSalesPaise, p.NetSalesPaise, PnlRowKind.Subtotal),
        };
        foreach (var code in c.OtherIncome.Select(l => l.Code).Union(p.OtherIncome.Select(l => l.Code)).OrderBy(x => x))
            rows.Add(new(Name(c.OtherIncome, p.OtherIncome, code), Amount(c.OtherIncome, code), Amount(p.OtherIncome, code), PnlRowKind.Line));
        rows.Add(new("Less: cost of goods sold", -c.CogsPaise, -p.CogsPaise, PnlRowKind.Line));
        rows.Add(new("Gross profit", c.GrossProfitPaise, p.GrossProfitPaise, PnlRowKind.Total));
        rows.Add(new("Gross margin", c.GrossMarginBp, p.GrossMarginBp, PnlRowKind.Percent));
        rows.Add(new("Expenses", 0, 0, PnlRowKind.Heading));
        foreach (var code in c.Expenses.Select(l => l.Code).Union(p.Expenses.Select(l => l.Code)).OrderBy(x => x))
            rows.Add(new(Name(c.Expenses, p.Expenses, code), Amount(c.Expenses, code), Amount(p.Expenses, code), PnlRowKind.Line));
        rows.Add(new("Total expenses", c.TotalExpensesPaise, p.TotalExpensesPaise, PnlRowKind.Subtotal));
        rows.Add(new("NET PROFIT", c.NetProfitPaise, p.NetProfitPaise, PnlRowKind.Total));
        rows.Add(new("Net margin", c.NetMarginBp, p.NetMarginBp, PnlRowKind.Percent));
        return rows;
    }

    private static long Amount(IReadOnlyList<PnlLine> lines, string code) => lines.FirstOrDefault(l => l.Code == code)?.AmountPaise ?? 0;

    private static string Name(IReadOnlyList<PnlLine> a, IReadOnlyList<PnlLine> b, string code) =>
        (a.FirstOrDefault(l => l.Code == code) ?? b.First(l => l.Code == code)).Name;
}
