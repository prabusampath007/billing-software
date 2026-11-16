using System.Security.Cryptography;
using System.Text;
using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

internal abstract class ServiceBase(DbFactory factory, ICurrentUser current)
{
    protected DbFactory Factory { get; } = factory;
    protected ICurrentUser Current { get; } = current;

    protected SessionUser RequireUser() =>
        Current.User ?? throw new PermissionDeniedException("Please sign in first.");

    protected SessionUser RequireAdmin()
    {
        var user = RequireUser();
        if (user.Role != UserRole.Admin) throw new PermissionDeniedException("Only the Admin can do this.");
        return user;
    }
}

/// <summary>Tamper-evident audit log: each row stores the hash of the previous row (decision D2).</summary>
internal static class AuditWriter
{
    public static async Task AppendAsync(AppDbContext db, SessionUser? user, string action, string entityType, long? entityId, string details, string? usernameOverride = null)
    {
        var prev = await db.AuditEvents.OrderByDescending(a => a.Id).Select(a => a.Hash).FirstOrDefaultAsync() ?? "";
        var e = new AuditEvent
        {
            TimestampUtc = DateTime.UtcNow,
            UserId = user?.Id,
            Username = user?.Username ?? usernameOverride ?? "system",
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = details,
            PrevHash = prev,
        };
        e.Hash = ComputeHash(e);
        db.AuditEvents.Add(e);
        await db.SaveChangesAsync(); // flush now so the next event sees this hash
    }

    public static string ComputeHash(AuditEvent e)
    {
        var payload = $"{e.PrevHash}|{e.TimestampUtc.Ticks}|{e.UserId}|{e.Username}|{e.Action}|{e.EntityType}|{e.EntityId}|{e.Details}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}

internal static class Posting
{
    /// <summary>Saves a balanced journal built from drafts. Throws if the drafts are not balanced.</summary>
    public static async Task<Journal> PostAsync(AppDbContext db, DateTime date, string narration, string sourceType, long sourceId,
        long userId, IReadOnlyList<JournalLineDraft> drafts)
    {
        PostingBuilder.EnsureBalanced(drafts);
        var codes = drafts.Select(d => d.AccountCode).Distinct().ToList();
        var accounts = await db.Accounts.Where(a => codes.Contains(a.Code)).ToDictionaryAsync(a => a.Code, a => a.Id);
        var journal = new Journal { Date = date, Narration = narration, SourceType = sourceType, SourceId = sourceId, CreatedByUserId = userId };
        foreach (var d in drafts)
        {
            if (!accounts.TryGetValue(d.AccountCode, out var accountId))
                throw new InvalidOperationException($"Account {d.AccountCode} is missing from the chart of accounts.");
            journal.Lines.Add(new JournalLine { AccountId = accountId, DebitPaise = d.DebitPaise, CreditPaise = d.CreditPaise });
        }
        db.Journals.Add(journal);
        await db.SaveChangesAsync();
        return journal;
    }

    /// <summary>The balance of an account from all journals (credit side positive when <paramref name="creditNormal"/>).</summary>
    public static async Task<long> BalanceAsync(AppDbContext db, string code, bool creditNormal)
    {
        var id = await db.Accounts.Where(a => a.Code == code).Select(a => (long?)a.Id).FirstOrDefaultAsync();
        if (id == null) return 0;
        var d = await db.JournalLines.Where(l => l.AccountId == id).SumAsync(l => (long?)l.DebitPaise) ?? 0;
        var c = await db.JournalLines.Where(l => l.AccountId == id).SumAsync(l => (long?)l.CreditPaise) ?? 0;
        return creditNormal ? c - d : d - c;
    }

    public static async Task EnsurePeriodOpenAsync(AppDbContext db, DateTime date)
    {
        var locked = await db.Settings.Where(s => s.Key == SettingKeys.LockedThrough).Select(s => s.Value).FirstOrDefaultAsync();
        if (DateTime.TryParse(locked, out var lockedThrough) && date.Date <= lockedThrough.Date)
            throw new BusinessRuleException($"The period up to {lockedThrough:dd-MMM-yyyy} is locked. Posting is not allowed.");
    }

    public static async Task<FinancialYear> EnsureFinancialYearAsync(AppDbContext db, DateTime date)
    {
        var (start, end, label) = FinancialYears.For(date);
        var fy = await db.FinancialYears.FirstOrDefaultAsync(f => f.Label == label);
        if (fy == null)
        {
            fy = new FinancialYear { Label = label, StartDate = start, EndDate = end };
            db.FinancialYears.Add(fy);
            await db.SaveChangesAsync();
        }
        if (fy.IsLocked) throw new BusinessRuleException($"Financial year {label} is closed.");
        return fy;
    }
}

/// <summary>Short-lived record of Admin approvals, so a cashier's retry after approval is accepted.</summary>
internal sealed class ApprovalLedger
{
    private readonly Dictionary<long, DateTime> _grants = new();
    private readonly object _gate = new();
    private static readonly TimeSpan Validity = TimeSpan.FromMinutes(2);

    // Throttle for the on-the-spot Admin approval prompt (password only, so failures cannot be tied to one account).
    private int _failures;
    private DateTime _lockedUntil;

    public void Record(long adminId) { lock (_gate) _grants[adminId] = DateTime.UtcNow + Validity; }
    public bool IsValid(long adminId) { lock (_gate) return _grants.TryGetValue(adminId, out var until) && until > DateTime.UtcNow; }

    public bool IsThrottled() { lock (_gate) return _lockedUntil > DateTime.UtcNow; }
    public void RegisterFailure() { lock (_gate) { if (++_failures >= 5) { _lockedUntil = DateTime.UtcNow.AddMinutes(1); _failures = 0; } } }
    public void RegisterSuccess() { lock (_gate) _failures = 0; }
}
