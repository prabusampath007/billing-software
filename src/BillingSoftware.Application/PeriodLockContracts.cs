namespace BillingSoftware.Application;

/// <summary>Everything on or before this date is closed: no bill, expense, payment or other entry can be dated into it.</summary>
public sealed record PeriodLockStatus(DateOnly? LockedThrough, string Summary);

public interface IPeriodLockService
{
    Task<PeriodLockStatus> GetAsync();
    /// <summary>
    /// Admin only, needs the Admin password. Locks every date up to and including <paramref name="lockThrough"/> (it must be before today so billing goes on),
    /// or removes the lock when null. Moving the lock back or removing it needs a reason and is recorded in the activity log.
    /// </summary>
    Task SetAsync(DateOnly? lockThrough, string adminPassword, string? reason);
}
