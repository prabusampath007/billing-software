namespace BillingSoftware.Application;

public sealed record BsLine(string Code, string Name, long AmountPaise);

/// <summary>The balance sheet as at one date. Liability and owner's-funds amounts are positive when they are owed or owned.</summary>
public sealed record BsColumn(
    DateOnly AsAt, IReadOnlyList<BsLine> Assets, IReadOnlyList<BsLine> Liabilities, IReadOnlyList<BsLine> OwnersFunds,
    long TotalAssetsPaise, long TotalLiabilitiesPaise, long TotalOwnersFundsPaise)
{
    /// <summary>Assets less liabilities less owner's funds. Always zero when the books are in order.</summary>
    public long DifferencePaise => TotalAssetsPaise - TotalLiabilitiesPaise - TotalOwnersFundsPaise;
}

/// <summary>A comparison of the books with the detailed records behind them (customers, suppliers, unpaid expenses, stock).</summary>
public sealed record BsCheck(string Name, long BooksPaise, long RecordsPaise, bool Ok, string Message);

public sealed record BalanceSheetReport(
    string CompanyName, BsColumn Current, BsColumn Compare,
    IReadOnlyList<BsCheck> Checks, IReadOnlyList<string> Warnings, IReadOnlyList<string> Notes);

public interface IBalanceSheetService
{
    /// <summary>
    /// The balance sheet as at a date, next to the same statement for an earlier date (one month before unless given).
    /// Read from the posted entries; profit is shown inside owner's funds, split into earlier years and this financial year.
    /// </summary>
    Task<BalanceSheetReport> BuildAsync(DateOnly asAt, DateOnly? compareAt = null);
}

/// <summary>The one layout of the balance sheet, shared by the screen, the Excel file and the printout.</summary>
public static class BalanceSheetStatement
{
    public static IReadOnlyList<PnlRow> Rows(BalanceSheetReport r)
    {
        var c = r.Current; var p = r.Compare;
        var rows = new List<PnlRow>();
        void Section(string heading, IReadOnlyList<BsLine> cur, IReadOnlyList<BsLine> prev, string totalLabel, long curTotal, long prevTotal)
        {
            rows.Add(new(heading, 0, 0, PnlRowKind.Heading));
            foreach (var code in cur.Select(l => l.Code).Union(prev.Select(l => l.Code)).OrderBy(x => x, StringComparer.Ordinal))
            {
                var a = cur.FirstOrDefault(l => l.Code == code); var b = prev.FirstOrDefault(l => l.Code == code);
                rows.Add(new((a ?? b)!.Name, a?.AmountPaise ?? 0, b?.AmountPaise ?? 0, PnlRowKind.Line));
            }
            rows.Add(new(totalLabel, curTotal, prevTotal, PnlRowKind.Subtotal));
        }
        Section("Assets (what the business has)", c.Assets, p.Assets, "Total assets", c.TotalAssetsPaise, p.TotalAssetsPaise);
        Section("Liabilities (what the business owes)", c.Liabilities, p.Liabilities, "Total liabilities", c.TotalLiabilitiesPaise, p.TotalLiabilitiesPaise);
        Section("Owner's funds (what belongs to the owner)", c.OwnersFunds, p.OwnersFunds, "Total owner's funds", c.TotalOwnersFundsPaise, p.TotalOwnersFundsPaise);
        rows.Add(new("Liabilities + owner's funds", c.TotalLiabilitiesPaise + c.TotalOwnersFundsPaise, p.TotalLiabilitiesPaise + p.TotalOwnersFundsPaise, PnlRowKind.Total));
        return rows;
    }
}
