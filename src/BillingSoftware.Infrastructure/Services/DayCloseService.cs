using System.Text.Json;
using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

internal sealed class DayCloseService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), IDayCloseService
{
    private sealed record Computed(DayCloseReport Report, bool First, DateTime? LastClosedAt, long? LastCounted, bool OpeningRecorded);

    public static string MovementLabel(CashMovementType t) => t switch
    {
        CashMovementType.OwnerPayIn => "Money put in by the owner",
        CashMovementType.BankWithdrawal => "Cash taken from the bank",
        CashMovementType.Expense => "Shop expense paid from the drawer",
        CashMovementType.OwnerWithdrawal => "Money taken out by the owner",
        CashMovementType.BankDeposit => "Cash deposited in the bank",
        CashMovementType.LoanReceived => "Loan received in cash",
        CashMovementType.LoanRepaid => "Loan repaid in cash",
        _ => t.ToString(),
    };

    public async Task<DayClosePreview> PreviewAsync(long openingFloatPaise = 0)
    {
        RequireAdmin();
        await using var db = Factory.Create();
        var c = await ComputeAsync(db, DateTime.Now, Math.Max(0, openingFloatPaise), null, null, null);
        return new DayClosePreview(c.Report, c.First, c.LastClosedAt, c.LastCounted, c.OpeningRecorded);
    }

    /// <summary>
    /// Works out everything between the previous close and <paramref name="end"/>. Expected cash is the opening cash plus every cash
    /// movement in the period, and must always equal the cash account in the books (tests check this).
    /// </summary>
    private static async Task<Computed> ComputeAsync(AppDbContext db, DateTime end, long openingFloat, long? counted, string? note, string? closedBy)
    {
        var last = await db.DayCloses.AsNoTracking().OrderByDescending(d => d.Id).FirstOrDefaultAsync();
        var start = last?.PeriodEnd ?? DateTime.MinValue;
        var first = last == null;
        // If the owner recorded the cash in hand under Opening balances, that is the starting cash and the typed float is ignored.
        var recordedOpening = first ? await db.OpeningBalances.Where(o => o.Kind == OpeningKind.Cash && !o.IsVoided).SumAsync(o => (long?)o.AmountPaise) ?? 0 : 0;
        var opening = first ? (recordedOpening > 0 ? recordedOpening : openingFloat) : last!.CountedCashPaise;

        var made = await db.Sales.AsNoTracking().Include(s => s.Payments).Where(s => s.SaleDate >= start && s.SaleDate < end).ToListAsync();
        var voided = await db.Sales.AsNoTracking().Include(s => s.Payments).Where(s => s.VoidedAt != null && s.VoidedAt >= start && s.VoidedAt < end).ToListAsync();
        var returns = await db.SaleReturns.AsNoTracking().Where(r => r.Date >= start && r.Date < end).ToListAsync();
        var receipts = await db.Receipts.AsNoTracking().Where(r => r.Date >= start && r.Date < end).ToListAsync();
        var purchases = await db.Purchases.AsNoTracking().Include(p => p.Payments).Where(p => p.PurchaseDate >= start && p.PurchaseDate < end).ToListAsync();
        var supplierPays = await db.SupplierPayments.AsNoTracking().Where(p => p.Date >= start && p.Date < end).ToListAsync();
        var purchaseReturns = await db.PurchaseReturns.AsNoTracking().Where(r => r.Date >= start && r.Date < end).ToListAsync();
        var expenses = await db.Expenses.AsNoTracking().Where(e => e.RecordedAt >= start && e.RecordedAt < end).ToListAsync();
        var expenseVoids = await db.Expenses.AsNoTracking().Where(e => e.VoidedAt != null && e.VoidedAt >= start && e.VoidedAt < end).ToListAsync();
        var expensePays = await db.ExpensePayments.AsNoTracking().Where(p => p.Date >= start && p.Date < end).ToListAsync();
        var movements = await db.CashMovements.AsNoTracking().Where(m => m.Date >= start && m.Date < end).OrderBy(m => m.Id).ToListAsync();

        long Cash(IEnumerable<Sale> sales) => sales.SelectMany(s => s.Payments).Where(p => p.Mode == PaymentMode.Cash).Sum(p => p.AmountPaise);

        // ---- sales summary ----
        var gross = made.Sum(s => s.TotalPaise);
        var voidedTotal = voided.Sum(s => s.TotalPaise);
        var returnsTotal = returns.Sum(r => r.TotalPaise);
        var modeTotals = new Dictionary<PaymentMode, long>();
        foreach (var p in made.SelectMany(s => s.Payments)) modeTotals[p.Mode] = modeTotals.GetValueOrDefault(p.Mode) + p.AmountPaise;
        foreach (var p in voided.SelectMany(s => s.Payments)) modeTotals[p.Mode] = modeTotals.GetValueOrDefault(p.Mode) - p.AmountPaise;
        var refundTotals = returns.GroupBy(r => r.RefundMode).ToDictionary(g => g.Key, g => g.Sum(r => r.TotalPaise));

        var cashierIds = made.Where(s => s.Status == SaleStatus.Posted).Select(s => s.CashierId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => cashierIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var byCashier = made.Where(s => s.Status == SaleStatus.Posted).GroupBy(s => s.CashierId)
            .Select(g => new CashierLine(names.GetValueOrDefault(g.Key, "?"), g.Count(), g.Sum(s => s.TotalPaise))).OrderByDescending(c => c.TotalPaise).ToList();

        static string Mode(PaymentMode m) => m switch { PaymentMode.Cash => "Cash", PaymentMode.Upi => "UPI", PaymentMode.Card => "Card", _ => "On credit" };

        // ---- cash in the drawer: signed lines that add up to the expected cash ----
        var lines = new List<NamedAmount> { new("Cash in the drawer at the start", opening) };
        void Add(string label, long amount, bool always = false) { if (amount != 0 || always) lines.Add(new(label, amount)); }
        Add("Cash from sales", Cash(made), always: true);
        Add("Cash given back for bills voided", -Cash(voided));
        Add("Cash received from customers (settling credit)", receipts.Where(r => r.Mode == PaymentMode.Cash).Sum(r => r.AmountPaise));
        Add("Cash refunded to customers for returns", -returns.Where(r => r.RefundMode == PaymentMode.Cash).Sum(r => r.TotalPaise));
        Add("Cash paid for purchases", -purchases.SelectMany(p => p.Payments).Where(p => p.Mode == PaymentMode.Cash).Sum(p => p.AmountPaise));
        Add("Cash paid to suppliers", -supplierPays.Where(p => p.Mode == PaymentMode.Cash).Sum(p => p.AmountPaise));
        Add("Cash paid for business expenses", -expenses.Where(e => e.PaymentMode == PaymentMode.Cash).Sum(e => e.AmountPaise));
        Add("Cash paid later for expenses", -expensePays.Where(p => p.Mode == PaymentMode.Cash).Sum(p => p.AmountPaise));
        Add("Cash given back for expenses cancelled", expenseVoids.Where(e => e.PaymentMode == PaymentMode.Cash).Sum(e => e.AmountPaise));
        Add("Cash refunded by suppliers for goods sent back", purchaseReturns.Where(r => r.RefundMode == PaymentMode.Cash).Sum(r => r.TotalPaise));
        foreach (var g in movements.GroupBy(m => m.Type).OrderBy(g => g.Key))
        {
            var sign = g.Key is CashMovementType.OwnerPayIn or CashMovementType.BankWithdrawal or CashMovementType.LoanReceived ? 1 : -1;
            Add(MovementLabel(g.Key), sign * g.Sum(m => m.AmountPaise));
        }
        var expected = lines.Sum(l => l.AmountPaise);
        var counts = counted ?? 0;

        var report = new DayCloseReport(start == DateTime.MinValue ? (made.Count > 0 ? made.Min(s => s.SaleDate) : end) : start, end, closedBy ?? "",
            made.Count, voided.Count, returns.Count, gross, voidedTotal, returnsTotal, gross - voidedTotal - returnsTotal,
            made.Sum(s => s.DiscountPaise) - voided.Sum(s => s.DiscountPaise),
            made.Sum(s => s.CgstPaise) - voided.Sum(s => s.CgstPaise) - returns.Sum(r => r.CgstPaise),
            made.Sum(s => s.SgstPaise) - voided.Sum(s => s.SgstPaise) - returns.Sum(r => r.SgstPaise),
            made.Sum(s => s.IgstPaise) - voided.Sum(s => s.IgstPaise) - returns.Sum(r => r.IgstPaise),
            modeTotals.Where(kv => kv.Value != 0).OrderBy(kv => kv.Key).Select(kv => new NamedAmount(Mode(kv.Key), kv.Value)).ToList(),
            refundTotals.OrderBy(kv => kv.Key).Select(kv => new NamedAmount(Mode(kv.Key), kv.Value)).ToList(),
            byCashier, lines, opening, expected, counted.HasValue ? counts : expected, counted.HasValue ? counts - expected : 0, note);
        return new Computed(report, first, last?.PeriodEnd, last?.CountedCashPaise, recordedOpening > 0);
    }

    public async Task<DayCloseResult> CloseAsync(DayCloseCommand cmd)
    {
        var admin = RequireAdmin();
        if (cmd.CountedCashPaise < 0) throw new ValidationException("The counted cash cannot be negative.");
        if (cmd.OpeningFloatPaise < 0) throw new ValidationException("The opening cash cannot be negative.");

        await using var db = Factory.Create();
        var existing = await db.DayCloses.AsNoTracking().FirstOrDefaultAsync(d => d.CommandId == cmd.CommandId);
        if (existing != null) return new DayCloseResult(existing.Id, existing.ExpectedCashPaise, existing.CountedCashPaise, existing.VariancePaise, true);

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var end = DateTime.Now;
            await Posting.EnsurePeriodOpenAsync(db, end);
            var c = await ComputeAsync(db, end, cmd.OpeningFloatPaise, cmd.CountedCashPaise, cmd.Note?.Trim(), admin.DisplayName);
            var variance = c.Report.VariancePaise;
            if (variance != 0 && (cmd.Note ?? "").Trim().Length < 3)
                throw new ValidationException($"The cash is {(variance > 0 ? "over" : "short")} by {Money.Format(Math.Abs(variance))}. Write a short note explaining it.");

            var close = new DayClose
            {
                CommandId = cmd.CommandId, PeriodStart = c.Report.PeriodStart, PeriodEnd = end, ClosedByUserId = admin.Id,
                OpeningCashPaise = c.Report.OpeningCashPaise, ExpectedCashPaise = c.Report.ExpectedCashPaise, CountedCashPaise = cmd.CountedCashPaise,
                VariancePaise = variance, Note = cmd.Note?.Trim(), ReportJson = JsonSerializer.Serialize(c.Report),
            };
            db.DayCloses.Add(close);
            await db.SaveChangesAsync();

            // The books must say exactly what is in the drawer: record any starting cash, then the difference found when counting.
            if (c.First && !c.OpeningRecorded && cmd.OpeningFloatPaise > 0)
                await Posting.PostAsync(db, end, "Opening cash in the drawer", "DayClose", close.Id, admin.Id, PostingBuilder.ForOpeningCash(cmd.OpeningFloatPaise));
            if (variance != 0)
                await Posting.PostAsync(db, end, $"Cash {(variance > 0 ? "over" : "short")} at day end", "DayClose", close.Id, admin.Id, PostingBuilder.ForCashVariance(variance));
            await AuditWriter.AppendAsync(db, admin, "day.closed", "DayClose", close.Id,
                $"expected {close.ExpectedCashPaise}, counted {close.CountedCashPaise}, variance {variance}" + (string.IsNullOrWhiteSpace(close.Note) ? "" : $"; note: {close.Note}"));
            await tx.CommitAsync();
            return new DayCloseResult(close.Id, close.ExpectedCashPaise, close.CountedCashPaise, variance, false);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync();
            await using var db2 = Factory.Create();
            var again = await db2.DayCloses.AsNoTracking().FirstOrDefaultAsync(d => d.CommandId == cmd.CommandId);
            if (again != null) return new DayCloseResult(again.Id, again.ExpectedCashPaise, again.CountedCashPaise, again.VariancePaise, true);
            throw;
        }
    }

    public async Task<DayCloseReport?> GetAsync(long id)
    {
        RequireAdmin();
        await using var db = Factory.Create();
        var json = await db.DayCloses.AsNoTracking().Where(d => d.Id == id).Select(d => d.ReportJson).FirstOrDefaultAsync();
        return json == null ? null : JsonSerializer.Deserialize<DayCloseReport>(json);
    }

    public async Task<IReadOnlyList<DayCloseListItem>> ListAsync(int take = 30)
    {
        RequireAdmin();
        await using var db = Factory.Create();
        return await (from d in db.DayCloses.AsNoTracking() join u in db.Users on d.ClosedByUserId equals u.Id orderby d.Id descending
                      select new DayCloseListItem(d.Id, d.PeriodEnd, u.DisplayName, d.CountedCashPaise, d.VariancePaise)).Take(Math.Clamp(take, 1, 200)).ToListAsync();
    }

    public async Task<CashMovementDto> RecordMovementAsync(CashMovementType type, long amountPaise, string reason)
    {
        var admin = RequireAdmin();
        if (!Enum.IsDefined(type)) throw new ValidationException("Choose what the money was for.");
        if (amountPaise <= 0) throw new ValidationException("Amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3) throw new ValidationException("Write a short reason.");

        await using var db = Factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync();
        var now = DateTime.Now;
        await Posting.EnsurePeriodOpenAsync(db, now);
        if (type == CashMovementType.LoanRepaid)
        {
            var loan = await Posting.BalanceAsync(db, AccountCodes.Loans, creditNormal: true);
            if (amountPaise > loan) throw new ValidationException(loan <= 0 ? "No loan is recorded, so there is nothing to repay. Record the loan first (Opening balances or \"Loan received\")." : $"That is more than the loan still owed ({Money.Format(loan)}).");
        }
        var m = new CashMovement { Date = now, Type = type, AmountPaise = amountPaise, Reason = reason.Trim(), UserId = admin.Id };
        db.CashMovements.Add(m);
        await db.SaveChangesAsync();
        await Posting.PostAsync(db, now, $"{MovementLabel(type)}: {m.Reason}", "CashMovement", m.Id, admin.Id, PostingBuilder.ForCashMovement(type, amountPaise));
        await AuditWriter.AppendAsync(db, admin, "cash.moved", "CashMovement", m.Id, $"{type} {amountPaise} paise: {m.Reason}");
        await tx.CommitAsync();
        return new CashMovementDto(m.Id, m.Date, m.Type, m.AmountPaise, m.Reason, admin.DisplayName);
    }

    public async Task<IReadOnlyList<CashMovementDto>> ListMovementsAsync()
    {
        RequireAdmin();
        await using var db = Factory.Create();
        var start = await db.DayCloses.AsNoTracking().OrderByDescending(d => d.Id).Select(d => (DateTime?)d.PeriodEnd).FirstOrDefaultAsync() ?? DateTime.MinValue;
        return await (from m in db.CashMovements.AsNoTracking() where m.Date >= start join u in db.Users on m.UserId equals u.Id orderby m.Id descending
                      select new CashMovementDto(m.Id, m.Date, m.Type, m.AmountPaise, m.Reason, u.DisplayName)).Take(200).ToListAsync();
    }
}
