using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

internal sealed class ExpenseService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), IExpenseService
{
    private const long MaxAmountPaise = 100_000_000_00;     // 10 crore
    private const int MaxBackdateDays = 800;
    private const string DepreciationCode = "6180";
    private const string BlockedFoodCode = "6160";        // Tea and refreshments: credit on food and drink is generally blocked

    // ---------- categories ----------
    public async Task<IReadOnlyList<ExpenseCategoryDto>> ListCategoriesAsync(bool includeInactive = false)
    {
        RequireAdmin();
        await using var db = Factory.Create();
        var q = db.ExpenseCategories.AsNoTracking().AsQueryable();
        if (!includeInactive) q = q.Where(c => c.IsActive);
        return (await q.OrderBy(c => c.AccountCode).ToListAsync()).Select(c => new ExpenseCategoryDto(c.Id, c.Name, c.AccountCode, c.IsActive, c.IsDefault)).ToList();
    }

    public async Task<ExpenseCategoryDto> AddCategoryAsync(string name)
    {
        var admin = RequireAdmin();
        name = (name ?? "").Trim();
        if (name.Length < 2 || name.Length > 40) throw new ValidationException("Give the category a name of 2 to 40 characters.");

        await using var db = Factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync();
        if (await db.ExpenseCategories.AnyAsync(c => c.Name == name)) throw new ValidationException("There is already a category with that name.");

        var codes = (await db.ExpenseCategories.Select(c => c.AccountCode).ToListAsync())
            .Select(c => int.TryParse(c, out var n) ? n : 0).Where(n => n >= ExpenseDefaults.FirstCustomCode && n <= ExpenseDefaults.LastCustomCode).ToList();
        var next = codes.Count == 0 ? ExpenseDefaults.FirstCustomCode : codes.Max() + 10;
        if (next > ExpenseDefaults.LastCustomCode) throw new BusinessRuleException("There are too many custom categories. Use an existing one or deactivate one you no longer need.");

        var code = next.ToString();
        db.Accounts.Add(new Account { Code = code, Name = name, Type = AccountType.Expense });
        var cat = new ExpenseCategory { Name = name, AccountCode = code, IsDefault = false };
        db.ExpenseCategories.Add(cat);
        await db.SaveChangesAsync();
        await AuditWriter.AppendAsync(db, admin, "expense.category_added", "ExpenseCategory", cat.Id, $"{name} ({code})");
        await tx.CommitAsync();
        return new ExpenseCategoryDto(cat.Id, cat.Name, cat.AccountCode, true, false);
    }

    public async Task SetCategoryActiveAsync(long id, bool active)
    {
        var admin = RequireAdmin();
        await using var db = Factory.Create();
        var cat = await db.ExpenseCategories.FindAsync(id) ?? throw new ValidationException("Category not found.");
        if (!active && cat.AccountCode == "6900") throw new BusinessRuleException("\"Other expenses\" must stay available.");
        cat.IsActive = active;
        await db.SaveChangesAsync();
        await AuditWriter.AppendAsync(db, admin, active ? "expense.category_enabled" : "expense.category_disabled", "ExpenseCategory", cat.Id, cat.Name);
    }

    // ---------- recording ----------
    public async Task<ExpenseResult> RecordAsync(RecordExpenseCommand cmd)
    {
        var admin = RequireAdmin();
        var payTo = (cmd.PayTo ?? "").Trim();
        var description = (cmd.Description ?? "").Trim();
        if (cmd.AmountPaise <= 0) throw new ValidationException("Amount must be greater than zero.");
        if (cmd.AmountPaise > MaxAmountPaise) throw new ValidationException("That amount is too large. Please check it.");
        if (description.Length < 3 && payTo.Length < 2) throw new ValidationException("Write what the expense was for, or who it was paid to.");
        if (payTo.Length > 80 || description.Length > 200 || (cmd.Reference?.Length ?? 0) > 40) throw new ValidationException("The text is too long.");
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (cmd.Date > today) throw new ValidationException("The date cannot be in the future.");
        if (today.DayNumber - cmd.Date.DayNumber > MaxBackdateDays) throw new ValidationException("That date is too far in the past.");
        if (!Enum.IsDefined(cmd.PaymentMode)) throw new ValidationException("Choose how it was paid.");
        var gst = cmd.GstPaise;
        var gstin = string.IsNullOrWhiteSpace(cmd.SupplierGstin) ? null : cmd.SupplierGstin.Trim().ToUpperInvariant();
        if (gst < 0) throw new ValidationException("The GST cannot be negative.");
        if (gst >= cmd.AmountPaise) throw new ValidationException("The GST cannot be as much as the whole amount.");
        if (gst > 0 && gst * 100 > (cmd.AmountPaise - gst) * 28 + 100) throw new ValidationException("The GST is more than 28% of the amount before GST. Please check the figure.");
        if (gstin != null && !GstValidation.IsValidGstin(gstin)) throw new ValidationException("The supplier's GSTIN format is not valid.");
        if (cmd.ClaimCredit && gst <= 0) throw new ValidationException("Enter the GST amount shown on the bill to claim input credit.");
        if (cmd.ClaimCredit && gstin == null) throw new ValidationException("Input credit needs the supplier's GSTIN from their tax invoice.");
        if (cmd.ClaimCredit && string.IsNullOrWhiteSpace(cmd.Reference)) throw new ValidationException("Enter the supplier's bill number to claim input credit.");

        await using var db = Factory.Create();
        var existing = await db.Expenses.AsNoTracking().FirstOrDefaultAsync(e => e.CommandId == cmd.CommandId);
        if (existing != null) return new ExpenseResult(existing.Id, existing.ExpenseNumber, true);

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var cat = await db.ExpenseCategories.FirstOrDefaultAsync(c => c.Id == cmd.CategoryId && c.IsActive) ?? throw new ValidationException("Choose a category.");
            var isDepreciation = cat.AccountCode == DepreciationCode;
            if (isDepreciation && gst > 0) throw new ValidationException("Depreciation has no GST.");
            long cgst = 0, sgst = 0, igst = 0;
            if (cmd.ClaimCredit)
            {
                var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync() ?? throw new BusinessRuleException("Company setup is not complete.");
                if (!company.GstEnabled) throw new BusinessRuleException("GST is turned off for this business, so no input credit is claimed. Leave the claim off; the GST stays part of the cost.");
                if (company.IsComposition) throw new BusinessRuleException("A business on the composition scheme cannot claim input credit. Leave the claim off; the GST stays part of the cost.");
                if (string.IsNullOrWhiteSpace(company.Gstin)) throw new BusinessRuleException("Add the business GSTIN in Settings before claiming input credit.");
                if (cat.AccountCode == BlockedFoodCode)
                    throw new BusinessRuleException("GST on food, drink and refreshments is generally not available as input credit. Leave the claim off; the GST stays part of the cost. Ask your accountant if you think it applies.");
                if (!string.Equals(gstin![..2], company.StateCode, StringComparison.Ordinal)) igst = gst;
                else { cgst = gst / 2; sgst = gst - cgst; }
            }
            if (isDepreciation && cmd.PaymentMode != PaymentMode.NonCash) throw new ValidationException("Depreciation involves no money. Choose \"No money involved\".");
            if (!isDepreciation && cmd.PaymentMode == PaymentMode.NonCash) throw new ValidationException("\"No money involved\" is only for depreciation. Choose how it was paid.");
            if (isDepreciation)
            {
                var netValue = await Posting.BalanceAsync(db, AccountCodes.FixedAssets, creditNormal: false);
                if (cmd.AmountPaise > netValue)
                    throw new ValidationException(netValue <= 0 ? "No furniture or equipment is recorded, so there is nothing to depreciate. Add it under Opening balances first."
                        : $"Depreciation cannot be more than the value of the assets recorded ({Money.Format(netValue)}).");
            }
            var businessDate = cmd.Date.ToDateTime(TimeOnly.MinValue);
            await Posting.EnsurePeriodOpenAsync(db, businessDate);
            var fy = await Posting.EnsureFinancialYearAsync(db, businessDate);
            var now = DateTime.Now;

            var e = new Expense
            {
                CommandId = cmd.CommandId, FinancialYearId = fy.Id, Date = businessDate, RecordedAt = now, CategoryId = cat.Id, PayTo = payTo,
                Description = description, Reference = string.IsNullOrWhiteSpace(cmd.Reference) ? null : cmd.Reference.Trim(), AmountPaise = cmd.AmountPaise,
                PaymentMode = cmd.PaymentMode, UserId = admin.Id, SupplierGstin = gstin, GstPaise = gst, CgstPaise = cgst, SgstPaise = sgst, IgstPaise = igst,
            };
            e.ExpenseNumber = await Numbering.NextAsync(db, "EXP", fy);
            db.Expenses.Add(e);
            await db.SaveChangesAsync();

            // The entry is dated the day the expense belongs to, so the profit and loss of that day or month includes it.
            var journalDate = cmd.Date == today ? now : cmd.Date.ToDateTime(new TimeOnly(12, 0));
            await Posting.PostAsync(db, journalDate, $"{cat.Name}: {(description.Length > 0 ? description : payTo)}", "Expense", e.Id, admin.Id,
                PostingBuilder.ForExpense(cat.AccountCode, cmd.AmountPaise, cmd.PaymentMode, cgst, sgst, igst));
            await AuditWriter.AppendAsync(db, admin, "expense.recorded", "Expense", e.Id,
                $"{e.ExpenseNumber} {cat.Name} {cmd.AmountPaise} paise via {cmd.PaymentMode}" + (gst > 0 ? $"; GST {gst}, input credit claimed {cgst + sgst + igst}" : ""));
            await tx.CommitAsync();
            return new ExpenseResult(e.Id, e.ExpenseNumber, false);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync();
            await using var db2 = Factory.Create();
            var again = await db2.Expenses.AsNoTracking().FirstOrDefaultAsync(x => x.CommandId == cmd.CommandId);
            if (again != null) return new ExpenseResult(again.Id, again.ExpenseNumber, true);
            throw;
        }
    }

    public async Task PayAsync(long expenseId, long amountPaise, PaymentMode mode)
    {
        var admin = RequireAdmin();
        if (amountPaise <= 0) throw new ValidationException("Amount must be greater than zero.");
        PaymentModeRules.EnsureMoney(mode);

        await using var db = Factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync();
        var now = DateTime.Now;
        await Posting.EnsurePeriodOpenAsync(db, now);
        var e = await db.Expenses.FirstOrDefaultAsync(x => x.Id == expenseId) ?? throw new ValidationException("Expense not found.");
        if (e.Status != ExpenseStatus.Posted) throw new ValidationException("This expense was cancelled.");
        if (e.PaymentMode != PaymentMode.Credit) throw new ValidationException("This expense was already paid when it was entered.");
        var outstanding = await OutstandingAsync(db, e);
        if (amountPaise > outstanding) throw new ValidationException($"Amount is more than what is still unpaid ({Money.Format(outstanding)}).");

        var pay = new ExpensePayment { ExpenseId = e.Id, Date = now, Mode = mode, AmountPaise = amountPaise, UserId = admin.Id };
        db.ExpensePayments.Add(pay);
        await db.SaveChangesAsync();
        await Posting.PostAsync(db, now, $"Payment of expense {e.ExpenseNumber}", "ExpensePayment", pay.Id, admin.Id, PostingBuilder.ForExpenseSettlement(mode, amountPaise));
        await AuditWriter.AppendAsync(db, admin, "expense.paid", "ExpensePayment", pay.Id, $"{e.ExpenseNumber} {amountPaise} paise via {mode}");
        await tx.CommitAsync();
    }

    public async Task VoidAsync(long expenseId, string reason)
    {
        var admin = RequireAdmin();
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3) throw new ValidationException("Write the reason it is being cancelled.");

        await using var db = Factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync();
        var e = await db.Expenses.FirstOrDefaultAsync(x => x.Id == expenseId) ?? throw new ValidationException("Expense not found.");
        if (e.Status == ExpenseStatus.Voided) throw new ValidationException("This expense is already cancelled.");
        if (await db.ExpensePayments.AnyAsync(p => p.ExpenseId == e.Id)) throw new ValidationException("Payments have been made against this expense, so it cannot be cancelled.");

        var now = DateTime.Now;
        await Posting.EnsurePeriodOpenAsync(db, now);
        await Posting.EnsurePeriodOpenAsync(db, e.Date);

        // Reverse the original entry; the original is never edited.
        var original = await db.Journals.Include(j => j.Lines).FirstAsync(j => j.SourceType == "Expense" && j.SourceId == e.Id);
        var codes = await db.Accounts.ToDictionaryAsync(a => a.Id, a => a.Code);
        var reversal = PostingBuilder.Reverse(original.Lines.Select(l => new JournalLineDraft(codes[l.AccountId], l.DebitPaise, l.CreditPaise)));
        await Posting.PostAsync(db, now, $"Cancelled expense {e.ExpenseNumber}", "ExpenseVoid", e.Id, admin.Id, reversal);

        e.Status = ExpenseStatus.Voided; e.VoidedAt = now; e.VoidReason = reason.Trim(); e.VoidedByUserId = admin.Id;
        await db.SaveChangesAsync();
        await AuditWriter.AppendAsync(db, admin, "expense.voided", "Expense", e.Id, $"{e.ExpenseNumber} cancelled: {reason.Trim()}");
        await tx.CommitAsync();
    }

    // ---------- reading ----------
    private static async Task<long> OutstandingAsync(AppDbContext db, Expense e)
    {
        if (e.PaymentMode != PaymentMode.Credit || e.Status != ExpenseStatus.Posted) return 0;
        var paid = await db.ExpensePayments.Where(p => p.ExpenseId == e.Id).SumAsync(p => (long?)p.AmountPaise) ?? 0;
        return e.AmountPaise - paid;
    }

    public async Task<IReadOnlyList<ExpenseListItem>> ListAsync(DateOnly from, DateOnly to, long? categoryId = null, bool unpaidOnly = false)
    {
        RequireAdmin();
        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = to.ToDateTime(TimeOnly.MinValue).AddDays(1);
        await using var db = Factory.Create();
        var q = db.Expenses.AsNoTracking().Where(e => e.Date >= start && e.Date < end);
        if (categoryId.HasValue) q = q.Where(e => e.CategoryId == categoryId.Value);
        var rows = await (from e in q join c in db.ExpenseCategories on e.CategoryId equals c.Id orderby e.Date descending, e.Id descending
                          select new { e, Category = c.Name }).Take(2000).ToListAsync();
        var ids = rows.Select(r => r.e.Id).ToList();
        var paid = await db.ExpensePayments.AsNoTracking().Where(p => ids.Contains(p.ExpenseId))
            .GroupBy(p => p.ExpenseId).Select(g => new { Id = g.Key, Sum = g.Sum(x => x.AmountPaise) }).ToDictionaryAsync(x => x.Id, x => x.Sum);

        var list = rows.Select(r => new ExpenseListItem(r.e.Id, r.e.ExpenseNumber, DateOnly.FromDateTime(r.e.Date), r.Category, r.e.PayTo, r.e.Description, r.e.AmountPaise,
            r.e.PaymentMode, r.e.Status,
            r.e.PaymentMode == PaymentMode.Credit && r.e.Status == ExpenseStatus.Posted ? r.e.AmountPaise - paid.GetValueOrDefault(r.e.Id) : 0,
            r.e.GstPaise, r.e.CgstPaise + r.e.SgstPaise + r.e.IgstPaise)).ToList();
        return unpaidOnly ? list.Where(x => x.OutstandingPaise > 0).ToList() : list;
    }

    public async Task<ExpenseSummary> SummaryAsync(DateOnly from, DateOnly to)
    {
        RequireAdmin();
        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = to.ToDateTime(TimeOnly.MinValue).AddDays(1);
        await using var db = Factory.Create();
        var rows = await (from e in db.Expenses.AsNoTracking()
                          where e.Date >= start && e.Date < end && e.Status == ExpenseStatus.Posted
                          join c in db.ExpenseCategories on e.CategoryId equals c.Id
                          group e by c.Name into g
                          select new ExpenseCategoryTotal(g.Key, g.Count(), g.Sum(x => x.AmountPaise - x.CgstPaise - x.SgstPaise - x.IgstPaise))).ToListAsync();
        var ordered = rows.OrderByDescending(r => r.TotalPaise).ThenBy(r => r.Category).ToList();
        return new ExpenseSummary(ordered, ordered.Sum(r => r.TotalPaise), ordered.Sum(r => r.Count));
    }
}
