using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

internal sealed class BalanceSheetService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), IBalanceSheetService
{
    public const string PreviousProfitCode = "9998";
    public const string CurrentProfitCode = "9999";

    public async Task<BalanceSheetReport> BuildAsync(DateOnly asAt, DateOnly? compareAt = null)
    {
        RequireAdmin();
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (asAt.Year < 2017 || asAt > today.AddDays(366)) throw new ValidationException("Choose a date between 2017 and a year from today.");
        var compare = compareAt ?? asAt.AddMonths(-1);
        if (compare > asAt) throw new ValidationException("The comparison date cannot be after the balance sheet date.");

        await using var db = Factory.Create();
        var company = await db.Companies.AsNoTracking().Select(c => c.Name).FirstOrDefaultAsync() ?? "";
        var accounts = await db.Accounts.AsNoTracking().ToListAsync();
        var cur = await ColumnAsync(db, accounts, asAt);
        var cmp = await ColumnAsync(db, accounts, compare);

        var checks = new List<BsCheck>();
        var warnings = new List<string>();
        if (asAt >= today) checks.AddRange(await ChecksAsync(db, cur));
        else warnings.Add("The checks against customer, supplier, expense and stock records are only made for today's balance sheet, because those records show the position today.");

        if (cur.DifferencePaise != 0)
            warnings.Add($"The balance sheet does not balance (difference {Money.Format(cur.DifferencePaise)}). Do not rely on it; contact support.");
        void Negative(string code, string what)
        {
            var l = cur.Assets.FirstOrDefault(x => x.Code == code);
            if (l is { AmountPaise: < 0 }) warnings.Add($"{what} is below zero ({Money.Format(l.AmountPaise)}). Check pay-outs, purchases and expenses, and whether opening balances are missing.");
        }
        Negative(AccountCodes.Cash, "Cash in hand");
        Negative(AccountCodes.Bank, "Bank and UPI/card");
        Negative(AccountCodes.Inventory, "Stock");
        Negative(AccountCodes.Receivables, "What customers owe you");
        foreach (var l in cur.Liabilities.Where(l => l.AmountPaise < 0))
            warnings.Add($"{l.Name} is below zero ({Money.Format(l.AmountPaise)}), which means you have paid or credited more than you owed.");

        var notes = new List<string>
        {
            "Read from the posted entries in the books as at the end of the date chosen.",
            "Profit is part of owner's funds. It is split into earlier financial years and this financial year (April to March), so no year-end closing step is needed.",
            "GST is shown in full: GST collected as a liability and GST paid on purchases as credit to claim. They are not netted off.",
            "Balances from before you started (bank, cash, customer and supplier dues, loans, furniture and equipment) are in the books only if they were entered under Opening balances. Depreciation is in the books only if it was recorded as an expense.",
            "Stock is valued at weighted-average cost.",
        };
        return new BalanceSheetReport(company, cur, cmp, checks, warnings, notes);
    }

    private static async Task<BsColumn> ColumnAsync(AppDbContext db, List<Account> accounts, DateOnly asAt)
    {
        var end = asAt.ToDateTime(TimeOnly.MinValue).AddDays(1);
        var fyStart = FinancialYears.For(asAt.ToDateTime(TimeOnly.MinValue)).Start;

        async Task<Dictionary<long, long>> Nets(DateTime upTo) =>
            (await (from l in db.JournalLines.AsNoTracking()
                    join j in db.Journals on l.JournalId equals j.Id
                    where j.Date < upTo
                    group l by l.AccountId into g
                    select new { g.Key, Debit = g.Sum(x => x.DebitPaise), Credit = g.Sum(x => x.CreditPaise) }).ToListAsync())
            .ToDictionary(x => x.Key, x => x.Debit - x.Credit);     // positive = debit balance

        var all = await Nets(end);
        var before = await Nets(fyStart);

        long Net(Dictionary<long, long> d, Account a) => d.GetValueOrDefault(a.Id);
        var assets = new List<BsLine>(); var liabilities = new List<BsLine>(); var equity = new List<BsLine>();
        long profitAll = 0, profitBefore = 0;
        foreach (var a in accounts.OrderBy(a => a.Code, StringComparer.Ordinal))
        {
            var net = Net(all, a);
            switch (a.Type)
            {
                case AccountType.Asset: if (net != 0 || IsCore(a.Code)) assets.Add(new BsLine(a.Code, a.Name, net)); break;
                case AccountType.Liability: if (net != 0 || IsCore(a.Code)) liabilities.Add(new BsLine(a.Code, a.Name, -net)); break;
                case AccountType.Equity: if (net != 0) equity.Add(new BsLine(a.Code, a.Name, -net)); break;
                case AccountType.Income: profitAll += -net; profitBefore += -Net(before, a); break;
                case AccountType.Expense: profitAll -= net; profitBefore -= Net(before, a); break;
            }
        }
        // Profit is part of owner's funds: earlier years' profit, and profit so far this financial year.
        equity.Add(new BsLine(PreviousProfitCode, "Profit of earlier years", profitBefore));
        equity.Add(new BsLine(CurrentProfitCode, "Profit this financial year so far", profitAll - profitBefore));

        return new BsColumn(asAt, assets, liabilities, equity, assets.Sum(l => l.AmountPaise), liabilities.Sum(l => l.AmountPaise), equity.Sum(l => l.AmountPaise));
    }

    private static bool IsCore(string code) => code is AccountCodes.Cash or AccountCodes.Bank or AccountCodes.Receivables or AccountCodes.Inventory or AccountCodes.Payables;

    /// <summary>Compares what the books say with the detailed records (today's position only).</summary>
    private static async Task<List<BsCheck>> ChecksAsync(AppDbContext db, BsColumn col)
    {
        long Books(IReadOnlyList<BsLine> lines, string code) => lines.FirstOrDefault(l => l.Code == code)?.AmountPaise ?? 0;
        var checks = new List<BsCheck>();
        void Add(string name, long books, long records, long tolerance, string what)
        {
            var diff = books - records;
            var ok = Math.Abs(diff) <= tolerance;
            checks.Add(new BsCheck(name, books, records, ok, ok
                ? $"{what} agree."
                : $"The books say {Money.Format(books)} but the records add up to {Money.Format(records)} (difference {Money.Format(diff)}). Look into this before relying on the balance sheet."));
        }

        // customers: credit given on bills, less what they paid, less credit notes put against their account
        var customerCredit = await (from s in db.Sales.AsNoTracking() where s.Status == SaleStatus.Posted && s.PartyId != null
                                    from p in s.Payments where p.Mode == PaymentMode.Credit select (long?)p.AmountPaise).SumAsync() ?? 0;
        var customerPaid = await db.Receipts.AsNoTracking().SumAsync(r => (long?)r.AmountPaise) ?? 0;
        var customerCredited = await db.SaleReturns.AsNoTracking().Where(r => r.PartyId != null && r.RefundMode == PaymentMode.Credit).SumAsync(r => (long?)r.TotalPaise) ?? 0;
        var customerOpening = await db.OpeningBalances.AsNoTracking().Where(o => o.Kind == OpeningKind.CustomerDue && !o.IsVoided).SumAsync(o => (long?)o.AmountPaise) ?? 0;
        Add("What customers owe you", Books(col.Assets, AccountCodes.Receivables), customerCredit - customerPaid - customerCredited + customerOpening, 0, "The books and the customer accounts");

        var supplierCredit = await (from p in db.Purchases.AsNoTracking() from pay in p.Payments where pay.Mode == PaymentMode.Credit select (long?)pay.AmountPaise).SumAsync() ?? 0;
        var supplierPaid = await db.SupplierPayments.AsNoTracking().SumAsync(p => (long?)p.AmountPaise) ?? 0;
        var supplierCredited = await db.PurchaseReturns.AsNoTracking().Where(r => r.RefundMode == PaymentMode.Credit).SumAsync(r => (long?)r.TotalPaise) ?? 0;
        var supplierOpening = await db.OpeningBalances.AsNoTracking().Where(o => o.Kind == OpeningKind.SupplierDue && !o.IsVoided).SumAsync(o => (long?)o.AmountPaise) ?? 0;
        Add("What you owe suppliers", Books(col.Liabilities, AccountCodes.Payables), supplierCredit - supplierPaid - supplierCredited + supplierOpening, 0, "The books and the supplier accounts");

        var unpaid = await db.Expenses.AsNoTracking().Where(e => e.Status == ExpenseStatus.Posted && e.PaymentMode == PaymentMode.Credit).SumAsync(e => (long?)e.AmountPaise) ?? 0;
        var unpaidPaid = await (from p in db.ExpensePayments.AsNoTracking() join e in db.Expenses on p.ExpenseId equals e.Id
                                where e.Status == ExpenseStatus.Posted select (long?)p.AmountPaise).SumAsync() ?? 0;
        Add("Expenses not paid yet", Books(col.Liabilities, AccountCodes.ExpensesPayable), unpaid - unpaidPaid, 0, "The books and the unpaid expenses");

        // stock: the books against quantity x average cost; rounding of average costs makes small differences normal
        var stock = await db.Products.AsNoTracking().Where(p => p.QtyOnHandMilli != 0).Select(p => new { p.QtyOnHandMilli, p.AvgCostPaise }).ToListAsync();
        var stockValue = stock.Sum(p => Costing.Value(p.QtyOnHandMilli, p.AvgCostPaise));
        Add("Stock", Books(col.Assets, AccountCodes.Inventory), stockValue, 100 + 5L * stock.Count, "The books and the stock list (small rounding differences are normal)");
        return checks;
    }
}
