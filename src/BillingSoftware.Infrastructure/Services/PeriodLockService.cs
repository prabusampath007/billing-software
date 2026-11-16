using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

internal sealed class PeriodLockService(DbFactory factory, ICurrentUser current, IAuthService auth) : ServiceBase(factory, current), IPeriodLockService
{
    private static DateOnly? Parse(string? text) => DateTime.TryParse(text, out var d) ? DateOnly.FromDateTime(d) : null;

    private static string Summarise(DateOnly? d) => d == null
        ? "No period is locked. Entries can be dated any day."
        : $"Everything up to and including {d:dd-MMM-yyyy} is locked. Nothing can be dated into that period, and bills, expenses and balances from it cannot be cancelled.";

    public async Task<PeriodLockStatus> GetAsync()
    {
        RequireUser();
        await using var db = Factory.Create();
        var d = Parse(await db.Settings.AsNoTracking().Where(s => s.Key == SettingKeys.LockedThrough).Select(s => s.Value).FirstOrDefaultAsync());
        return new PeriodLockStatus(d, Summarise(d));
    }

    public async Task SetAsync(DateOnly? lockThrough, string adminPassword, string? reason)
    {
        var admin = RequireAdmin();
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (lockThrough is { } d)
        {
            if (d >= today) throw new ValidationException("Choose a date before today. Locking today would stop billing.");
            if (d.Year < 2017) throw new ValidationException("That date is too far in the past.");
        }

        await using var db = Factory.Create();
        var old = Parse(await db.Settings.AsNoTracking().Where(s => s.Key == SettingKeys.LockedThrough).Select(s => s.Value).FirstOrDefaultAsync());
        if (old == lockThrough) throw new ValidationException(lockThrough == null ? "No period is locked." : "That date is already the lock date.");

        var loosening = old != null && (lockThrough == null || lockThrough < old);
        if (loosening && (reason ?? "").Trim().Length < 3)
            throw new ValidationException("Write the reason the lock is being moved back or removed.");

        await auth.VerifyAdminApprovalAsync(adminPassword);            // a wrong password throws

        await using var tx = await db.Database.BeginTransactionAsync();
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingKeys.LockedThrough);
        if (lockThrough == null) { if (row != null) db.Settings.Remove(row); }
        else if (row == null) db.Settings.Add(new Setting { Key = SettingKeys.LockedThrough, Value = lockThrough.Value.ToString("yyyy-MM-dd") });
        else row.Value = lockThrough.Value.ToString("yyyy-MM-dd");
        await db.SaveChangesAsync();
        await AuditWriter.AppendAsync(db, admin, loosening ? "period.unlocked" : "period.locked", "Setting", null,
            $"locked through {(old.HasValue ? old.Value.ToString("yyyy-MM-dd") : "none")} -> {(lockThrough.HasValue ? lockThrough.Value.ToString("yyyy-MM-dd") : "none")}"
            + (string.IsNullOrWhiteSpace(reason) ? "" : $"; reason: {reason!.Trim()}"));
        await tx.CommitAsync();
    }
}
