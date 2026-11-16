using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

internal sealed class ProfitLossService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), IProfitLossService
{
    private const int MaxDays = 800;

    public async Task<ProfitLossReport> BuildAsync(DateOnly from, DateOnly to)
    {
        RequireAdmin();
        if (to < from) throw new ValidationException("The end date cannot be before the start date.");
        var days = to.DayNumber - from.DayNumber + 1;
        if (days > MaxDays) throw new ValidationException("Choose a period of at most two years.");

        var prevTo = from.AddDays(-1);
        var prevFrom = prevTo.AddDays(-(days - 1));

        await using var db = Factory.Create();
        var company = await db.Companies.AsNoTracking().Select(c => c.Name).FirstOrDefaultAsync() ?? "";
        var accounts = await db.Accounts.AsNoTracking().ToListAsync();
        var current_ = await ColumnAsync(db, accounts, from, to);
        var previous = await ColumnAsync(db, accounts, prevFrom, prevTo);
        var items = await ItemsAsync(db, from, to);

        var notes = new List<string>
        {
            "Read from the posted entries in the books (accrual basis): a sale counts when it is billed, not when the money arrives.",
            "GST collected and paid is not income or expense, so it is left out.",
            "Stock is valued at weighted-average cost. Cost of goods sold is booked when the goods are sold.",
            "Purchases are not an expense: they become stock until sold. Stock losses, cost differences, round-off and cash over/short are shown under expenses.",
            "Expenses entered on the Expenses screen (rent, salaries, electricity and so on) appear here under their category on the day they belong to.",
            "Depreciation, interest accruals, income tax and other year-end adjustments by the accountant are not in the software unless entered as expenses.",
        };
        return new ProfitLossReport(company, current_, previous, items, notes);
    }

    private static async Task<PnlColumn> ColumnAsync(AppDbContext db, List<Account> accounts, DateOnly from, DateOnly to)
    {
        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = to.ToDateTime(TimeOnly.MinValue).AddDays(1);

        // Totals per account, and for the Sales account also per kind of entry (bills, voids, returns).
        var sums = await (from l in db.JournalLines.AsNoTracking()
                          join j in db.Journals on l.JournalId equals j.Id
                          where j.Date >= start && j.Date < end
                          group l by new { l.AccountId, j.SourceType } into g
                          select new { g.Key.AccountId, g.Key.SourceType, Debit = g.Sum(x => x.DebitPaise), Credit = g.Sum(x => x.CreditPaise) }).ToListAsync();

        var byId = accounts.ToDictionary(a => a.Id);
        long Net(Account a, bool credit) => sums.Where(s => s.AccountId == a.Id).Sum(s => credit ? s.Credit - s.Debit : s.Debit - s.Credit);

        var sales = accounts.First(a => a.Code == AccountCodes.Sales);
        long Part(string source, bool credit) => sums.Where(s => s.AccountId == sales.Id && s.SourceType == source).Sum(s => credit ? s.Credit : s.Debit);
        var gross = Part("Sale", credit: true);
        var voided = Part("SaleVoid", credit: false);
        var returns = Part("SaleReturn", credit: false);
        var netSales = Net(sales, credit: true);

        var otherIncome = accounts.Where(a => a.Type == AccountType.Income && a.Code != AccountCodes.Sales)
            .Select(a => new PnlLine(a.Code, a.Name, Net(a, credit: true))).Where(l => l.AmountPaise != 0).OrderBy(l => l.Code).ToList();
        var cogs = Net(accounts.First(a => a.Code == AccountCodes.Cogs), credit: false);
        var expenses = accounts.Where(a => a.Type == AccountType.Expense && a.Code != AccountCodes.Cogs)
            .Select(a => new PnlLine(a.Code, a.Name, Net(a, credit: false))).Where(l => l.AmountPaise != 0).OrderBy(l => l.Code).ToList();

        var gross_profit = netSales + otherIncome.Sum(l => l.AmountPaise) - cogs;
        var totalExpenses = expenses.Sum(l => l.AmountPaise);
        return new PnlColumn(from, to, gross, voided, returns, netSales, otherIncome, cogs, gross_profit, expenses, totalExpenses, gross_profit - totalExpenses);
    }

    /// <summary>
    /// Profit per item, built from the cost stored on each bill line. It follows the same events as the books
    /// (bills by their date, voids and returns by theirs), so its total always equals sales less cost of goods sold.
    /// </summary>
    private static async Task<IReadOnlyList<ItemProfitRow>> ItemsAsync(AppDbContext db, DateOnly from, DateOnly to)
    {
        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = to.ToDateTime(TimeOnly.MinValue).AddDays(1);

        var made = await db.Sales.AsNoTracking().Include(s => s.Lines).Where(s => s.SaleDate >= start && s.SaleDate < end).ToListAsync();
        var voided = await db.Sales.AsNoTracking().Include(s => s.Lines).Where(s => s.VoidedAt != null && s.VoidedAt >= start && s.VoidedAt < end).ToListAsync();
        var returns = await db.SaleReturns.AsNoTracking().Include(r => r.Lines).Where(r => r.Date >= start && r.Date < end).ToListAsync();
        var lineNames = voided.Concat(made).SelectMany(s => s.Lines).GroupBy(l => l.ProductId).ToDictionary(g => g.Key, g => g.First().ProductName);
        var returnLineIds = returns.SelectMany(r => r.Lines).Select(l => l.SaleLineId).Distinct().ToList();
        var returnedLines = await db.SaleLines.AsNoTracking().Where(l => returnLineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);

        var rows = new Dictionary<long, long[]>();     // qty, sales, cost
        long[] Row(long productId, string name)
        {
            if (!rows.TryGetValue(productId, out var a)) { rows[productId] = a = new long[3]; lineNames.TryAdd(productId, name); }
            return a;
        }
        void Add(SaleLine l, int sign)
        {
            var a = Row(l.ProductId, l.ProductName);
            a[0] += sign * l.QtyMilli; a[1] += sign * l.TaxablePaise; a[2] += sign * Costing.Value(l.QtyMilli, l.UnitCostPaise);
        }
        foreach (var s in made) foreach (var l in s.Lines) Add(l, +1);
        foreach (var s in voided) foreach (var l in s.Lines) Add(l, -1);
        foreach (var r in returns)
            foreach (var rl in r.Lines)
            {
                var sl = returnedLines[rl.SaleLineId];
                var a = Row(rl.ProductId, sl.ProductName);
                a[0] -= rl.QtyMilli; a[1] -= rl.TaxablePaise;
                if (rl.Restock) a[2] -= Costing.Value(rl.QtyMilli, rl.UnitCostPaise);     // damaged goods stay as cost, exactly as in the books
            }

        return rows.Where(kv => kv.Value.Any(v => v != 0))
            .Select(kv => new ItemProfitRow(kv.Key, lineNames.GetValueOrDefault(kv.Key, "?"), kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[1] - kv.Value[2]))
            .OrderByDescending(i => i.ProfitPaise).ThenBy(i => i.Name).ToList();
    }
}
