using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Xunit;

namespace BillingSoftware.Tests;

public class PeriodLockTests
{
    private static DateOnly Today => Helpers.Today;

    private static async Task<long> RentAsync(TestApp app) =>
        (await app.Get<IExpenseService>().ListCategoriesAsync()).Single(c => c.Name == "Rent").Id;

    private static RecordExpenseCommand Expense(long category, DateOnly date) =>
        new(Guid.NewGuid(), date, category, "Landlord", "Rent", null, 10_000, PaymentMode.Cash);

    [Fact]
    public async Task A_locked_period_refuses_entries_dated_into_it_but_today_carries_on()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var rent = await RentAsync(app);
        var old = await app.Get<IExpenseService>().RecordAsync(Expense(rent, Today.AddDays(-20)));

        var lockSvc = app.Get<IPeriodLockService>();
        Assert.Null((await lockSvc.GetAsync()).LockedThrough);
        await lockSvc.SetAsync(Today.AddDays(-10), TestApp.AdminPassword, null);
        var status = await lockSvc.GetAsync();
        Assert.Equal(Today.AddDays(-10), status.LockedThrough);
        Assert.Contains("locked", status.Summary);

        await Assert.ThrowsAsync<BusinessRuleException>(() => app.Get<IExpenseService>().RecordAsync(Expense(rent, Today.AddDays(-15))));    // into the locked period
        await Assert.ThrowsAsync<BusinessRuleException>(() => app.Get<IExpenseService>().RecordAsync(Expense(rent, Today.AddDays(-10))));    // the lock date itself
        await Assert.ThrowsAsync<BusinessRuleException>(() => app.Get<IExpenseService>().VoidAsync(old.Id, "Wrong"));                         // cancelling from it
        await app.Get<IExpenseService>().RecordAsync(Expense(rent, Today.AddDays(-9)));                                                       // the day after is open
        await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(soap, 1000, 11_800));                                                   // today's billing goes on
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task The_lock_can_move_forward_freely_but_moving_back_or_removing_needs_a_reason()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var svc = app.Get<IPeriodLockService>();
        await svc.SetAsync(Today.AddDays(-30), TestApp.AdminPassword, null);
        await svc.SetAsync(Today.AddDays(-5), TestApp.AdminPassword, null);                       // forward: no reason needed
        await Assert.ThrowsAsync<ValidationException>(() => svc.SetAsync(Today.AddDays(-20), TestApp.AdminPassword, null));    // back
        await Assert.ThrowsAsync<ValidationException>(() => svc.SetAsync(Today.AddDays(-20), TestApp.AdminPassword, "  "));
        await Assert.ThrowsAsync<ValidationException>(() => svc.SetAsync(null, TestApp.AdminPassword, null));                 // remove
        await svc.SetAsync(Today.AddDays(-20), TestApp.AdminPassword, "Accountant found a missed bill");
        Assert.Equal(Today.AddDays(-20), (await svc.GetAsync()).LockedThrough);
        await svc.SetAsync(null, TestApp.AdminPassword, "Year-end corrections");
        Assert.Null((await svc.GetAsync()).LockedThrough);
        await Assert.ThrowsAsync<ValidationException>(() => svc.SetAsync(null, TestApp.AdminPassword, "Nothing to remove"));
    }

    [Fact]
    public async Task Bad_dates_and_wrong_passwords_are_refused_and_change_nothing()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var svc = app.Get<IPeriodLockService>();
        await Assert.ThrowsAsync<ValidationException>(() => svc.SetAsync(Today, TestApp.AdminPassword, null));              // today would stop billing
        await Assert.ThrowsAsync<ValidationException>(() => svc.SetAsync(Today.AddDays(3), TestApp.AdminPassword, null));
        await Assert.ThrowsAsync<ValidationException>(() => svc.SetAsync(new DateOnly(2010, 1, 1), TestApp.AdminPassword, null));
        await Assert.ThrowsAsync<ValidationException>(() => svc.SetAsync(Today.AddDays(-5), "wrong-password", null));
        Assert.Null((await svc.GetAsync()).LockedThrough);
        await svc.SetAsync(Today.AddDays(-5), TestApp.AdminPassword, null);
        await Assert.ThrowsAsync<ValidationException>(() => svc.SetAsync(Today.AddDays(-5), TestApp.AdminPassword, null));  // already that date
    }

    [Fact]
    public async Task Only_the_Admin_can_change_the_lock_and_every_change_is_logged()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var svc = app.Get<IPeriodLockService>();
        await svc.SetAsync(Today.AddDays(-10), TestApp.AdminPassword, null);
        await svc.SetAsync(Today.AddDays(-20), TestApp.AdminPassword, "Reopen for a correction");
        await using (var db = app.Get<DbFactory>().Create())
        {
            var actions = db.AuditEvents.Select(e => e.Action).ToList();
            Assert.Contains("period.locked", actions);
            Assert.Contains("period.unlocked", actions);
            Assert.Contains(db.AuditEvents.Select(e => e.Details).ToList(), d => d.Contains("Reopen for a correction"));
        }
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => svc.SetAsync(Today.AddDays(-1), TestApp.AdminPassword, null));
        Assert.Equal(Today.AddDays(-20), (await svc.GetAsync()).LockedThrough);                    // anyone signed in can see it
    }

    [Fact]
    public async Task An_opening_balance_cannot_be_dated_into_a_locked_period()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<IPeriodLockService>().SetAsync(Today.AddDays(-10), TestApp.AdminPassword, null);
        await Assert.ThrowsAsync<BusinessRuleException>(() => app.Get<IOpeningBalanceService>().AddAsync(
            new AddOpeningCommand(Guid.NewGuid(), OpeningKind.Bank, Today.AddDays(-30), null, null, "Bank", 1_000, null, null)));
    }
}
