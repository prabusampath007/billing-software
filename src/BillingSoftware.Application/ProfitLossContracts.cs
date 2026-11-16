namespace BillingSoftware.Application;

public sealed record PnlLine(string Code, string Name, long AmountPaise);

/// <summary>One period of the profit and loss statement. Income and expense lines are positive when they add to income or to expense.</summary>
public sealed record PnlColumn(
    DateOnly From, DateOnly To,
    long GrossSalesPaise, long VoidedPaise, long ReturnsPaise, long NetSalesPaise,
    IReadOnlyList<PnlLine> OtherIncome, long CogsPaise, long GrossProfitPaise,
    IReadOnlyList<PnlLine> Expenses, long TotalExpensesPaise, long NetProfitPaise)
{
    /// <summary>Gross margin in basis points of net sales (0 when there were no sales).</summary>
    public long GrossMarginBp => NetSalesPaise == 0 ? 0 : GrossProfitPaise * 10000 / NetSalesPaise;
    public long NetMarginBp => NetSalesPaise == 0 ? 0 : NetProfitPaise * 10000 / NetSalesPaise;
}

public sealed record ItemProfitRow(long ProductId, string Name, long QtyMilli, long SalesPaise, long CostPaise, long ProfitPaise)
{
    public long MarginBp => SalesPaise <= 0 ? 0 : ProfitPaise * 10000 / SalesPaise;
}

public sealed record ProfitLossReport(
    string CompanyName, PnlColumn Current, PnlColumn Previous, IReadOnlyList<ItemProfitRow> Items, IReadOnlyList<string> Notes);

public interface IProfitLossService
{
    /// <summary>
    /// Profit and loss for the dates given (inclusive), compared with the period of the same length just before it.
    /// Read from the posted entries in the books, so it always agrees with the trial balance.
    /// </summary>
    Task<ProfitLossReport> BuildAsync(DateOnly from, DateOnly to);
}
