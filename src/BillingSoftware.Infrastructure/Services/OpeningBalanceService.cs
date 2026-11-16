using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

/// <summary>
/// Lets the books start from the position the business really had: cash, bank, what customers owe, what is owed to suppliers,
/// loans, and furniture or equipment. Each entry posts one balanced journal against "Opening balance equity", and customer and
/// supplier dues feed the same balances that receipts and supplier payments are checked against.
/// </summary>
internal sealed class OpeningBalanceService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), IOpeningBalanceService
{
    private const long MaxAmountPaise = 100_000_000_00;     // 10 crore
    private const int MaxBackdateDays = 3650;

    public async Task<OpeningSummary> GetSummaryAsync()
    {
        RequireAdmin();
        await using var db = Factory.Create();
        var rows = await db.OpeningBalances.AsNoTracking().OrderByDescending(o => o.Date).ThenByDescending(o => o.Id).ToListAsync();
        var items = rows.Select(o => new OpeningItem(o.Id, o.Kind, DateOnly.FromDateTime(o.Date), o.Name, o.AmountPaise, o.CostPaise, o.Note, o.IsVoided, o.VoidReason)).ToList();
        long Sum(OpeningKind k) => rows.Where(o => o.Kind == k && !o.IsVoided).Sum(o => o.AmountPaise);

        var equity = await Posting.BalanceAsync(db, AccountCodes.OpeningEquity, creditNormal: true);
        var stock = await OpeningStockAsync(db);
        var assets = Sum(OpeningKind.Cash) + Sum(OpeningKind.Bank) + Sum(OpeningKind.CustomerDue) + Sum(OpeningKind.FixedAsset) + stock;
        var liabilities = Sum(OpeningKind.SupplierDue) + Sum(OpeningKind.Loan);
        var closed = await db.DayCloses.AnyAsync();
        return new OpeningSummary(items, Sum(OpeningKind.Cash), Sum(OpeningKind.Bank), Sum(OpeningKind.CustomerDue), Sum(OpeningKind.FixedAsset),
            Sum(OpeningKind.SupplierDue), Sum(OpeningKind.Loan), stock, assets, liabilities, equity, closed);
    }

    /// <summary>Stock entered as opening stock (on the Items screen) is shown here as a memo; it is not entered twice.</summary>
    private static async Task<long> OpeningStockAsync(AppDbContext db)
    {
        var id = await db.Accounts.Where(a => a.Code == AccountCodes.OpeningEquity).Select(a => (long?)a.Id).FirstOrDefaultAsync();
        if (id == null) return 0;
        return await (from l in db.JournalLines join j in db.Journals on l.JournalId equals j.Id
                      where l.AccountId == id && j.SourceType == "StockIn"
                      select (long?)(l.CreditPaise - l.DebitPaise)).SumAsync() ?? 0;
    }

    public async Task<long> AddAsync(AddOpeningCommand cmd)
    {
        var admin = RequireAdmin();
        if (!Enum.IsDefined(cmd.Kind)) throw new ValidationException("Choose what kind of balance this is.");
        if (cmd.AmountPaise <= 0) throw new ValidationException("Amount must be greater than zero.");
        if (cmd.AmountPaise > MaxAmountPaise) throw new ValidationException("That amount is too large. Please check it.");
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (cmd.Date > today) throw new ValidationException("The date cannot be in the future. Use the date the balance is as at.");
        if (today.DayNumber - cmd.Date.DayNumber > MaxBackdateDays) throw new ValidationException("That date is too far in the past.");
        var note = string.IsNullOrWhiteSpace(cmd.Note) ? null : cmd.Note.Trim();
        if ((note?.Length ?? 0) > 200) throw new ValidationException("The note is too long.");
        var name = (cmd.Name ?? "").Trim();

        await using var db = Factory.Create();
        var existing = await db.OpeningBalances.AsNoTracking().FirstOrDefaultAsync(o => o.CommandId == cmd.CommandId);
        if (existing != null) return existing.Id;

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var date = cmd.Date.ToDateTime(TimeOnly.MinValue);
            await Posting.EnsurePeriodOpenAsync(db, date);

            long? partyId = null, supplierId = null, cost = null;
            switch (cmd.Kind)
            {
                case OpeningKind.CustomerDue:
                    var party = await db.Parties.FirstOrDefaultAsync(p => p.Id == cmd.PartyId && p.IsActive)
                        ?? throw new ValidationException("Choose the customer.");
                    partyId = party.Id; name = party.Name;
                    break;
                case OpeningKind.SupplierDue:
                    var sup = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == cmd.SupplierId && s.IsActive)
                        ?? throw new ValidationException("Choose the supplier.");
                    supplierId = sup.Id; name = sup.Name;
                    break;
                case OpeningKind.Cash:
                    if (await db.DayCloses.AnyAsync())
                        throw new BusinessRuleException("Cash cannot be added after a day-end close, because the drawer has already been counted. Use \"Money put in\" on the Day end screen instead.");
                    name = "Cash in hand";
                    break;
                case OpeningKind.Bank:
                case OpeningKind.Loan:
                case OpeningKind.FixedAsset:
                    if (name.Length < 2 || name.Length > 80)
                        throw new ValidationException(cmd.Kind == OpeningKind.Bank ? "Write the bank or account name (2 to 80 characters)."
                            : cmd.Kind == OpeningKind.Loan ? "Write who the loan is from (2 to 80 characters)." : "Write what the asset is (2 to 80 characters).");
                    if (cmd.Kind == OpeningKind.FixedAsset)
                    {
                        cost = cmd.CostPaise ?? cmd.AmountPaise;
                        if (cost < cmd.AmountPaise) throw new ValidationException("The original cost cannot be less than its value today.");
                        if (cost > MaxAmountPaise) throw new ValidationException("That amount is too large. Please check it.");
                    }
                    break;
            }

            var o = new OpeningBalance
            {
                CommandId = cmd.CommandId, Kind = cmd.Kind, Date = date, PartyId = partyId, SupplierId = supplierId, Name = name,
                AmountPaise = cmd.AmountPaise, CostPaise = cost, Note = note, UserId = admin.Id, RecordedAt = DateTime.Now,
            };
            db.OpeningBalances.Add(o);
            await db.SaveChangesAsync();

            var journalDate = cmd.Date == today ? DateTime.Now : cmd.Date.ToDateTime(new TimeOnly(12, 0));
            await Posting.PostAsync(db, journalDate, $"Opening balance: {OpeningItem.KindLabel(cmd.Kind)} - {name}", "Opening", o.Id, admin.Id,
                PostingBuilder.ForOpening(cmd.Kind, cmd.AmountPaise));
            await AuditWriter.AppendAsync(db, admin, "opening.added", "OpeningBalance", o.Id, $"{cmd.Kind} {name} {cmd.AmountPaise} paise as at {cmd.Date:yyyy-MM-dd}");
            await tx.CommitAsync();
            return o.Id;
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync();
            await using var db2 = Factory.Create();
            var again = await db2.OpeningBalances.AsNoTracking().FirstOrDefaultAsync(x => x.CommandId == cmd.CommandId);
            if (again != null) return again.Id;
            throw;
        }
    }

    public async Task VoidAsync(long id, string reason)
    {
        var admin = RequireAdmin();
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3) throw new ValidationException("Write the reason it is being cancelled.");

        await using var db = Factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync();
        var o = await db.OpeningBalances.FirstOrDefaultAsync(x => x.Id == id) ?? throw new ValidationException("Entry not found.");
        if (o.IsVoided) throw new ValidationException("This entry is already cancelled.");

        var now = DateTime.Now;
        await Posting.EnsurePeriodOpenAsync(db, now);
        await Posting.EnsurePeriodOpenAsync(db, o.Date);

        switch (o.Kind)
        {
            case OpeningKind.Cash:
                if (await db.DayCloses.AnyAsync())
                    throw new BusinessRuleException("This cash was counted at a day-end close, so it cannot be cancelled now.");
                break;
            case OpeningKind.CustomerDue:
                var owed = await PartyService.OutstandingAsync(db, o.PartyId!.Value);
                if (owed - o.AmountPaise < 0)
                    throw new BusinessRuleException("This customer has already paid against this balance (or been given credit), so it cannot be cancelled.");
                break;
            case OpeningKind.SupplierDue:
                var payable = await SupplierService.PayableAsync(db, o.SupplierId!.Value);
                if (payable - o.AmountPaise < 0)
                    throw new BusinessRuleException("This supplier has already been paid against this balance (or credited), so it cannot be cancelled.");
                break;
            case OpeningKind.Loan:
                if (await Posting.BalanceAsync(db, AccountCodes.Loans, creditNormal: true) - o.AmountPaise < 0)
                    throw new BusinessRuleException("Part of this loan has already been repaid, so the entry cannot be cancelled.");
                break;
            case OpeningKind.FixedAsset:
                if (await Posting.BalanceAsync(db, AccountCodes.FixedAssets, creditNormal: false) - o.AmountPaise < 0)
                    throw new BusinessRuleException("Depreciation has already been recorded against these assets, so the entry cannot be cancelled.");
                break;
        }

        var original = await db.Journals.Include(j => j.Lines).FirstAsync(j => j.SourceType == "Opening" && j.SourceId == o.Id);
        var codes = await db.Accounts.ToDictionaryAsync(a => a.Id, a => a.Code);
        var reversal = PostingBuilder.Reverse(original.Lines.Select(l => new JournalLineDraft(codes[l.AccountId], l.DebitPaise, l.CreditPaise)));
        await Posting.PostAsync(db, now, $"Cancelled opening balance: {o.Name}", "OpeningVoid", o.Id, admin.Id, reversal);

        o.IsVoided = true; o.VoidedAt = now; o.VoidReason = reason.Trim();
        await db.SaveChangesAsync();
        await AuditWriter.AppendAsync(db, admin, "opening.voided", "OpeningBalance", o.Id, $"{o.Kind} {o.Name} cancelled: {reason.Trim()}");
        await tx.CommitAsync();
    }
}

/// <summary>Opening balances recorded for customers and suppliers, used by the subledger totals.</summary>
internal static class OpeningDues
{
    public static async Task<long> CustomerAsync(AppDbContext db, long partyId) =>
        await db.OpeningBalances.Where(o => o.Kind == OpeningKind.CustomerDue && o.PartyId == partyId && !o.IsVoided).SumAsync(o => (long?)o.AmountPaise) ?? 0;

    public static async Task<long> SupplierAsync(AppDbContext db, long supplierId) =>
        await db.OpeningBalances.Where(o => o.Kind == OpeningKind.SupplierDue && o.SupplierId == supplierId && !o.IsVoided).SumAsync(o => (long?)o.AmountPaise) ?? 0;
}

/// <summary>Only real money (cash, UPI, card) or credit is accepted wherever a payment is taken or made.</summary>
internal static class PaymentModeRules
{
    /// <summary>Cash, UPI, card, and (when allowed) on-account. "No money involved" is only for depreciation.</summary>
    public static void EnsureMoney(PaymentMode mode, bool allowCredit = false)
    {
        var ok = mode is PaymentMode.Cash or PaymentMode.Upi or PaymentMode.Card || (allowCredit && mode == PaymentMode.Credit);
        if (!ok) throw new ValidationException(allowCredit ? "Choose cash, UPI, card or on account." : "Choose cash, UPI or card.");
    }
}
