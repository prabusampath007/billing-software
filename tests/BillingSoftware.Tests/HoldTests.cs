using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Xunit;

namespace BillingSoftware.Tests;

public class HoldTests
{
    private static HoldBillCommand Bill(long product, long qty = 2_000, string? label = null, long? party = null, long discount = 0) =>
        new(label, party, new[] { new HeldLine(product, qty, discount) });

    [Fact]
    public async Task A_bill_can_be_held_listed_recalled_once_and_is_then_gone()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var ravi = await app.Get<IPartyService>().SaveAsync(new PartyDto(0, "Ravi Traders", null, null, "33", 0));
        var hold = app.Get<IHoldService>();
        var id = await hold.HoldAsync(Bill(soap, 2_500, "Ravi waiting", ravi.Id, 500));

        var list = await hold.ListAsync();
        var item = Assert.Single(list);
        Assert.Equal("Ravi waiting", item.Label);
        Assert.Equal("Ravi Traders", item.CustomerName);
        Assert.Equal(1, item.ItemCount);
        Assert.Equal("Owner", item.UserName);

        var back = await hold.RecallAsync(id);
        Assert.NotNull(back);
        Assert.Equal(ravi.Id, back!.PartyId);
        Assert.Equal("Ravi Traders", back.CustomerName);
        var line = Assert.Single(back.Lines);
        Assert.Equal((soap, 2_500L, 500L), (line.ProductId, line.QtyMilli, line.DiscountPaise));
        Assert.Empty(await hold.ListAsync());
        Assert.Null(await hold.RecallAsync(id));                                // nobody can recall it twice
    }

    [Fact]
    public async Task Holding_a_bill_moves_no_stock_and_posts_nothing()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var before = (await app.Get<IReportService>().TrialBalanceAsync(Helpers.Today)).Rows.Sum(r => r.DebitPaise);
        await app.Get<IHoldService>().HoldAsync(Bill(soap, 5_000));
        Assert.Equal(10_000, (await app.Get<ICatalogService>().GetAsync(soap))!.QtyOnHandMilli);
        Assert.Equal(before, (await app.Get<IReportService>().TrialBalanceAsync(Helpers.Today)).Rows.Sum(r => r.DebitPaise));
        Assert.Empty(await app.Get<ISalesService>().ListAsync(Helpers.Today));
    }

    [Fact]
    public async Task A_default_label_is_made_from_the_time_and_a_bill_can_be_discarded()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var hold = app.Get<IHoldService>();
        var id = await hold.HoldAsync(Bill(soap));
        Assert.StartsWith("Held at ", (await hold.ListAsync()).Single().Label);
        await hold.DiscardAsync(id);
        await hold.DiscardAsync(id);                                           // discarding what is gone is harmless
        Assert.Empty(await hold.ListAsync());
    }

    [Fact]
    public async Task Bad_bills_are_refused()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var hold = app.Get<IHoldService>();
        await Assert.ThrowsAsync<ValidationException>(() => hold.HoldAsync(new HoldBillCommand(null, null, Array.Empty<HeldLine>())));
        await Assert.ThrowsAsync<ValidationException>(() => hold.HoldAsync(Bill(soap, 0)));
        await Assert.ThrowsAsync<ValidationException>(() => hold.HoldAsync(Bill(soap, -1)));
        await Assert.ThrowsAsync<ValidationException>(() => hold.HoldAsync(Bill(soap, 1_000, discount: -5)));
        await Assert.ThrowsAsync<ValidationException>(() => hold.HoldAsync(Bill(99999)));
        await Assert.ThrowsAsync<ValidationException>(() => hold.HoldAsync(Bill(soap, party: 99999)));
        await Assert.ThrowsAsync<ValidationException>(() => hold.HoldAsync(new HoldBillCommand(null, null, Enumerable.Repeat(new HeldLine(soap, 1_000, 0), 301).ToList())));
        Assert.Empty(await hold.ListAsync());
        var long60 = await hold.HoldAsync(Bill(soap, label: new string('x', 100)));
        Assert.Equal(60, (await hold.ListAsync()).Single(x => x.Id == long60).Label.Length);
    }

    [Fact]
    public async Task At_most_thirty_bills_can_be_held()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var hold = app.Get<IHoldService>();
        for (var i = 0; i < 30; i++) await hold.HoldAsync(Bill(soap, label: "B" + i));
        await Assert.ThrowsAsync<BusinessRuleException>(() => hold.HoldAsync(Bill(soap)));
        var first = (await hold.ListAsync()).First();
        await hold.DiscardAsync(first.Id);
        await hold.HoldAsync(Bill(soap));
    }

    [Fact]
    public async Task Bills_held_for_more_than_three_days_are_removed()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var hold = app.Get<IHoldService>();
        var stale = await hold.HoldAsync(Bill(soap, label: "old"));
        await hold.HoldAsync(Bill(soap, label: "new"));
        await using (var db = app.Get<DbFactory>().Create())
        {
            var h = db.HeldBills.Single(x => x.Id == stale);
            h.CreatedAt = DateTime.Now.AddDays(-4);
            await db.SaveChangesAsync();
        }
        var list = await hold.ListAsync();
        Assert.Equal(new[] { "new" }, list.Select(x => x.Label).ToArray());
    }

    [Fact]
    public async Task A_cashier_can_hold_and_recall_and_held_bills_survive_a_restart()
    {
        var app = TestApp.Create();
        var soap = await app.SeedAsync();
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        var id = await app.Get<IHoldService>().HoldAsync(Bill(soap, label: "From the cashier"));
        var dir = app.Dir;
        app.Services.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var again = TestApp.Create(dir);
        await again.SignInAsAdminAsync();
        var list = await again.Get<IHoldService>().ListAsync();
        Assert.Equal("From the cashier", list.Single().Label);
        Assert.Equal("Cashier One", list.Single().UserName);
        Assert.NotNull(await again.Get<IHoldService>().RecallAsync(id));
    }

    [Fact]
    public async Task A_held_bill_is_then_sold_in_the_normal_way()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var hold = app.Get<IHoldService>();
        var id = await hold.HoldAsync(Bill(soap, 1_000));
        var back = (await hold.RecallAsync(id))!;
        var r = await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(back.Lines[0].ProductId, back.Lines[0].QtyMilli, 11_800));
        Assert.Equal(11_800, r.TotalPaise);
        Assert.Equal(9_000, (await app.Get<ICatalogService>().GetAsync(soap))!.QtyOnHandMilli);
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task A_database_from_the_previous_release_gets_the_held_bills_table()
    {
        var app1 = TestApp.Create();
        await app1.SeedAsync();
        var dir = app1.Dir;
        var factory = app1.Get<DbFactory>();
        var dbPath = app1.Paths.DbPath;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using (var raw = factory.OpenRaw(dbPath))
        using (var cmd = raw.CreateCommand())
        {
            cmd.CommandText = "DROP TABLE HeldBills; DELETE FROM SchemaVersions WHERE Version = 10;";
            cmd.ExecuteNonQuery();
        }
        app1.Services.Dispose();

        using var app2 = TestApp.Create(dir);
        await app2.SignInAsAdminAsync();
        var soap = (await app2.Get<ICatalogService>().SearchAsync("SKU1")).Single().Id;
        await app2.Get<IHoldService>().HoldAsync(Bill(soap));
        Assert.Single(await app2.Get<IHoldService>().ListAsync());
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
