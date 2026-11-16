using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

/// <summary>
/// Customer accounts built from the same records the outstanding balance is worked out from (credit sales, receipts, credit notes and
/// opening dues), so a statement always ends on the balance the customer is shown as owing.
/// </summary>
internal sealed class CreditService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), ICreditService
{
    private const string Opening = "Opening balance", CreditSale = "Credit sale", Cancelled = "Bill cancelled", Receipt = "Receipt", CreditNote = "Credit note";

    private sealed record Ev(DateTime Date, int Order, string Kind, string Ref, string Desc, long Debit, long Credit);

    private static string ModeText(PaymentMode m) => m switch { PaymentMode.Cash => "cash", PaymentMode.Upi => "UPI", PaymentMode.Card => "card", _ => m.ToString() };

    /// <summary>Every movement on the customers' accounts, oldest first. <paramref name="partyId"/> null means all customers.</summary>
    private static async Task<Dictionary<long, List<Ev>>> EventsAsync(AppDbContext db, long? partyId, DateTime? upTo)
    {
        var result = new Dictionary<long, List<Ev>>();
        void Add(long id, Ev e)
        {
            if (upTo.HasValue && e.Date >= upTo.Value) return;
            if (!result.TryGetValue(id, out var list)) result[id] = list = new List<Ev>();
            list.Add(e);
        }

        var openings = await db.OpeningBalances.AsNoTracking().Where(o => o.Kind == OpeningKind.CustomerDue && !o.IsVoided && o.PartyId != null && (partyId == null || o.PartyId == partyId)).ToListAsync();
        foreach (var o in openings) Add(o.PartyId!.Value, new Ev(o.Date, 0, Opening, "Opening", string.IsNullOrWhiteSpace(o.Note) ? "Balance from before the software was used" : o.Note!, o.AmountPaise, 0));

        var sales = await db.Sales.AsNoTracking().Include(s => s.Payments).Where(s => s.PartyId != null && (partyId == null || s.PartyId == partyId)).ToListAsync();
        foreach (var s in sales)
        {
            var credit = s.Payments.Where(p => p.Mode == PaymentMode.Credit).Sum(p => p.AmountPaise);
            if (credit <= 0) continue;
            Add(s.PartyId!.Value, new Ev(s.SaleDate, 1, CreditSale, s.InvoiceNumber, "Goods on credit", credit, 0));
            if (s.Status == SaleStatus.Voided && s.VoidedAt.HasValue)
                Add(s.PartyId.Value, new Ev(s.VoidedAt.Value, 2, Cancelled, s.InvoiceNumber, string.IsNullOrWhiteSpace(s.VoidReason) ? "Bill cancelled" : "Bill cancelled: " + s.VoidReason, 0, credit));
        }

        var receipts = await db.Receipts.AsNoTracking().Where(r => partyId == null || r.PartyId == partyId).ToListAsync();
        foreach (var r in receipts) Add(r.PartyId, new Ev(r.Date, 3, Receipt, $"R-{r.Id}", "Received in " + ModeText(r.Mode), 0, r.AmountPaise));

        var notes = await db.SaleReturns.AsNoTracking().Where(r => r.PartyId != null && r.RefundMode == PaymentMode.Credit && (partyId == null || r.PartyId == partyId)).ToListAsync();
        foreach (var n in notes) Add(n.PartyId!.Value, new Ev(n.Date, 3, CreditNote, n.ReturnNumber, "Goods returned, put against the account", 0, n.TotalPaise));

        foreach (var list in result.Values) list.Sort((a, b) => a.Date != b.Date ? a.Date.CompareTo(b.Date) : a.Order.CompareTo(b.Order));
        return result;
    }

    // ---------------- statement ----------------
    public async Task<CustomerStatement> GetStatementAsync(long partyId, DateOnly from, DateOnly to)
    {
        RequireAdmin();
        if (to < from) throw new ValidationException("The end date cannot be before the start date.");
        if ((to.DayNumber - from.DayNumber) > 3700) throw new ValidationException("Choose a period of ten years or less.");

        await using var db = Factory.Create();
        var party = await db.Parties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == partyId) ?? throw new ValidationException("Customer not found.");
        var company = await db.Companies.AsNoTracking().Select(c => c.Name).FirstOrDefaultAsync() ?? "";
        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = to.ToDateTime(TimeOnly.MinValue).AddDays(1);
        var all = (await EventsAsync(db, partyId, end)).GetValueOrDefault(partyId) ?? new List<Ev>();

        var brought = all.Where(e => e.Date < start).Sum(e => e.Debit - e.Credit);
        var balance = brought;
        var entries = new List<LedgerEntry>();
        foreach (var e in all.Where(e => e.Date >= start))
        {
            balance += e.Debit - e.Credit;
            entries.Add(new LedgerEntry(e.Date, e.Kind, e.Ref, e.Desc, e.Debit, e.Credit, balance));
        }
        return new CustomerStatement(company, party.Id, party.Name, party.Phone, party.Gstin, from, to, brought, entries,
            entries.Sum(x => x.DebitPaise), entries.Sum(x => x.CreditPaise), balance, party.CreditLimitPaise);
    }

    // ---------------- ageing ----------------
    private sealed record Open(Ev Source, long UnpaidPaise);

    /// <summary>The amounts still unpaid, with payments and credit notes taken against the oldest amounts first. Cancelled bills drop out.</summary>
    private static List<Open> UnpaidItems(List<Ev> events)
    {
        var cancelled = events.Where(e => e.Kind == Cancelled).Select(e => e.Ref).ToHashSet();
        var live = events.Where(e => e.Kind != Cancelled && !(e.Kind == CreditSale && cancelled.Contains(e.Ref))).ToList();
        var pool = live.Sum(e => e.Credit);
        var items = new List<Open>();
        foreach (var d in live.Where(e => e.Debit > 0).OrderBy(e => e.Date).ThenBy(e => e.Order))
        {
            var take = Math.Min(d.Debit, pool);
            pool -= take;
            if (d.Debit - take > 0) items.Add(new Open(d, d.Debit - take));
        }
        return items;
    }

    private static int AgeDays(DateOnly asAt, DateTime date) => Math.Max(0, asAt.DayNumber - DateOnly.FromDateTime(date).DayNumber);

    public async Task<AgeingReport> GetAgeingAsync(DateOnly asAt)
    {
        RequireAdmin();
        if (asAt > DateOnly.FromDateTime(DateTime.Today).AddDays(1)) throw new ValidationException("The date cannot be in the future.");
        await using var db = Factory.Create();
        var company = await db.Companies.AsNoTracking().Select(c => c.Name).FirstOrDefaultAsync() ?? "";
        var end = asAt.ToDateTime(TimeOnly.MinValue).AddDays(1);
        var events = await EventsAsync(db, null, end);
        var partyIds = events.Keys.ToList();
        var parties = await db.Parties.AsNoTracking().Where(p => partyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        var rows = new List<AgeingRow>();
        foreach (var (id, evs) in events)
        {
            if (!parties.TryGetValue(id, out var party)) continue;
            var cancelled = evs.Where(e => e.Kind == Cancelled).Select(e => e.Ref).ToHashSet();
            var outstanding = evs.Where(e => e.Kind != Cancelled && !(e.Kind == CreditSale && cancelled.Contains(e.Ref))).Sum(e => e.Debit - e.Credit);
            if (outstanding <= 0) continue;
            long cur = 0, b2 = 0, b3 = 0, b4 = 0;
            DateTime? oldest = null;
            foreach (var o in UnpaidItems(evs))
            {
                var days = AgeDays(asAt, o.Source.Date);
                if (days <= 30) cur += o.UnpaidPaise; else if (days <= 60) b2 += o.UnpaidPaise; else if (days <= 90) b3 += o.UnpaidPaise; else b4 += o.UnpaidPaise;
                if (oldest == null || o.Source.Date < oldest) oldest = o.Source.Date;
            }
            rows.Add(new AgeingRow(id, party.Name, party.Phone, outstanding, cur, b2, b3, b4, oldest, oldest.HasValue ? AgeDays(asAt, oldest.Value) : 0, party.CreditLimitPaise));
        }
        var ordered = rows.OrderByDescending(r => r.Over90Paise + r.Days61To90Paise).ThenByDescending(r => r.OutstandingPaise).ThenBy(r => r.Name).ToList();
        return new AgeingReport(company, asAt, ordered, ordered.Sum(r => r.OutstandingPaise), ordered.Sum(r => r.CurrentPaise),
            ordered.Sum(r => r.Days31To60Paise), ordered.Sum(r => r.Days61To90Paise), ordered.Sum(r => r.Over90Paise));
    }

    // ---------------- reminder ----------------
    public async Task<CustomerReminder> GetReminderAsync(long partyId)
    {
        RequireAdmin();
        await using var db = Factory.Create();
        var party = await db.Parties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == partyId) ?? throw new ValidationException("Customer not found.");
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var evs = (await EventsAsync(db, partyId, null)).GetValueOrDefault(partyId) ?? new List<Ev>();
        var items = UnpaidItems(evs).Select(o => new UnpaidItem(o.Source.Ref == "Opening" ? "Earlier balance" : o.Source.Ref, o.Source.Date, o.UnpaidPaise, AgeDays(today, o.Source.Date))).ToList();
        if (items.Count == 0) throw new BusinessRuleException($"{party.Name} owes nothing, so there is nothing to remind them about.");
        return new CustomerReminder(company?.Name ?? "", company?.Phone, party.Name, today, items.Sum(i => i.UnpaidPaise), items);
    }
}
