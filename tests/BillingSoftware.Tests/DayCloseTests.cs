using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BillingSoftware.Tests;

public class DayCloseTests
{
    private static Task<long> CashBooksAsync(TestApp app) => Helpers.NetAsync(app, AccountCodes.Cash);

    private static async Task<DayCloseResult> CloseExactAsync(TestApp app, long floatPaise = 0)
    {
        var p = await app.Get<IDayCloseService>().PreviewAsync(floatPaise);
        return await app.Get<IDayCloseService>().CloseAsync(new DayCloseCommand(Guid.NewGuid(), p.Report.ExpectedCashPaise, floatPaise));
    }

    [Fact]
    public async Task First_close_records_the_opening_cash_and_the_books_then_match_the_drawer()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 2000, 23600));                          // cash 236.00
        await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800, PaymentMode.Upi));          // UPI is not in the drawer
        var dc = app.Get<IDayCloseService>();

        var preview = await dc.PreviewAsync(100_000);                                                              // started with 1,000.00
        Assert.True(preview.IsFirstClose);
        Assert.Equal(100_000, preview.Report.OpeningCashPaise);
        Assert.Equal(100_000 + 23_600, preview.Report.ExpectedCashPaise);

        var r = await dc.CloseAsync(new DayCloseCommand(Guid.NewGuid(), 123_600, 100_000));
        Assert.Equal(0, r.VariancePaise);
        Assert.Equal(123_600, await CashBooksAsync(app));                                                         // books agree with the count
        await Helpers.AssertBooksBalancedAsync(app);

        var second = await dc.PreviewAsync();
        Assert.False(second.IsFirstClose);
        Assert.Equal(123_600, second.Report.OpeningCashPaise);                                                     // yesterday's count is today's start
        Assert.Equal(123_600, second.Report.ExpectedCashPaise);
        Assert.Equal(0, second.Report.GrossSalesPaise);                                                            // nothing carried over into the new period
    }

    [Fact]
    public async Task Cash_short_is_posted_as_a_loss_and_needs_a_note()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 2000, 23600));
        var dc = app.Get<IDayCloseService>();

        var cmd = new DayCloseCommand(Guid.NewGuid(), 23_600 - 5000, 0);
        var ex = await Assert.ThrowsAsync<ValidationException>(() => dc.CloseAsync(cmd));
        Assert.Contains("short", ex.Message);
        Assert.Empty(await dc.ListAsync());                                                                        // nothing was saved
        Assert.Equal(23_600, await CashBooksAsync(app));

        var r = await dc.CloseAsync(cmd with { Note = "Gave wrong change in the evening" });
        Assert.Equal(-5000, r.VariancePaise);
        Assert.Equal(5000, await Helpers.NetAsync(app, AccountCodes.CashOverShort));                               // debit = loss
        Assert.Equal(18_600, await CashBooksAsync(app));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Cash_over_is_posted_as_a_gain()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800));
        var r = await app.Get<IDayCloseService>().CloseAsync(new DayCloseCommand(Guid.NewGuid(), 11_800 + 3000, 0, "Customer left a tip"));
        Assert.Equal(3000, r.VariancePaise);
        Assert.Equal(-3000, await Helpers.NetAsync(app, AccountCodes.CashOverShort));                              // credit = gain
        Assert.Equal(14_800, await CashBooksAsync(app));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Expected_cash_always_equals_the_cash_account_after_every_kind_of_cash_movement()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var sales = app.Get<ISalesService>();
        var dc = app.Get<IDayCloseService>();

        var earlier = await sales.CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800));                               // made in the first period
        await CloseExactAsync(app);                                                                                // period 1 closed, drawer = 118.00

        // ---- period 2: one of everything ----
        var cashSale = await sales.CompleteSaleAsync(TestApp.Sale(pid, 2000, 23600));
        await sales.CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800, PaymentMode.Upi));
        var ravi = await app.Get<IPartyService>().SaveAsync(new PartyDto(0, "Ravi Traders", null, null, "33", 0));
        var (creditSale, creditLine) = await Helpers.SellAsync(app, pid, 3000, 35400, PaymentMode.Credit, ravi.Id);
        await app.Get<IPartyService>().ReceivePaymentAsync(ravi.Id, 5000, PaymentMode.Cash);                       // customer pays 50.00 in cash
        await app.Get<IPartyService>().ReceivePaymentAsync(ravi.Id, 2000, PaymentMode.Upi);                        // and 20.00 by UPI (not cash)
        var (retSale, retLine) = await Helpers.SellAsync(app, pid, 1000, 11800);                                   // a cash sale ...
        await app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(retSale, retLine, 1000));                     // ... returned for a cash refund
        await sales.VoidSaleAsync(cashSale.SaleId, "wrong item", null);                                            // voided in the same period
        await sales.VoidSaleAsync(earlier.SaleId, "customer came back", null);                                     // voided after its period was closed

        var sup = await Helpers.AddSupplierAsync(app);
        var ps = app.Get<IPurchaseService>();
        var cashBuy = await ps.CompleteAsync(Helpers.Buy(sup, pid, 10_000, 5000, 59_000, "C-1"));                  // paid in cash
        await ps.CompleteAsync(Helpers.Buy(sup, pid, 10_000, 5000, 59_000, "C-2", PaymentMode.Credit));            // on account
        await app.Get<ISupplierService>().PayAsync(sup, 10_000, PaymentMode.Cash);
        var line = (await ps.GetReturnableAsync(cashBuy.PurchaseId))[0].LineId;
        await ps.ReturnAsync(new PurchaseReturnCommand(Guid.NewGuid(), cashBuy.PurchaseId, new[] { new ReturnLineInput(line, 2000, false) }, PaymentMode.Cash, "Damaged"));

        await dc.RecordMovementAsync(CashMovementType.OwnerPayIn, 500_000, "Owner added float");
        await dc.RecordMovementAsync(CashMovementType.Expense, 12_000, "Tea and courier");
        await dc.RecordMovementAsync(CashMovementType.BankDeposit, 100_000, "Deposit to bank");
        await dc.RecordMovementAsync(CashMovementType.BankWithdrawal, 30_000, "Cash from bank");
        await dc.RecordMovementAsync(CashMovementType.OwnerWithdrawal, 20_000, "Owner took cash");

        var preview = await dc.PreviewAsync();
        Assert.Equal(await CashBooksAsync(app), preview.Report.ExpectedCashPaise);                                 // the main invariant
        Assert.Equal(preview.Report.ExpectedCashPaise, preview.Report.CashLines.Sum(l => l.AmountPaise));
        Assert.Contains(preview.Report.CashLines, l => l.Label.Contains("voided") && l.AmountPaise == -(23_600 + 11_800));

        var closed = await dc.CloseAsync(new DayCloseCommand(Guid.NewGuid(), preview.Report.ExpectedCashPaise, 0));
        Assert.Equal(0, closed.VariancePaise);
        Assert.Equal(preview.Report.ExpectedCashPaise, await CashBooksAsync(app));
        await Helpers.AssertBooksBalancedAsync(app);
        Assert.True((await app.Get<IAuditService>().VerifyChainAsync()).Valid);
    }

    [Fact]
    public async Task The_z_report_shows_sales_by_cashier_and_payment_mode_and_never_changes_once_closed()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800));                          // admin, cash
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 2000, 23600, PaymentMode.Upi));         // cashier, UPI
        await app.SignInAsAdminAsync();

        var dc = app.Get<IDayCloseService>();
        var preview = await dc.PreviewAsync();
        var rep = preview.Report;
        Assert.Equal(2, rep.BillsMade);
        Assert.Equal(35_400, rep.GrossSalesPaise);
        Assert.Equal(35_400, rep.NetSalesPaise);
        Assert.Equal(2, rep.ByCashier.Count);
        Assert.Contains(rep.ByCashier, c => c.Cashier.StartsWith("Cashier") && c.TotalPaise == 23_600);
        Assert.Equal(11_800, rep.SalesByMode.Single(m => m.Label == "Cash").AmountPaise);
        Assert.Equal(23_600, rep.SalesByMode.Single(m => m.Label == "UPI").AmountPaise);
        Assert.Equal(5_400, rep.CgstPaise + rep.SgstPaise);                                                        // 54.00 of GST in 354.00

        var closed = await dc.CloseAsync(new DayCloseCommand(Guid.NewGuid(), 11_800, 0));
        await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 5000, 59_000));                         // a later sale must not leak into the old report
        var saved = (await dc.GetAsync(closed.Id))!;
        Assert.Equal(35_400, saved.GrossSalesPaise);
        Assert.Equal(11_800, saved.CountedCashPaise);
        Assert.Equal(2, saved.BillsMade);
    }

    [Fact]
    public async Task Returns_and_voids_reduce_net_sales_and_are_listed()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var (a, al) = await Helpers.SellAsync(app, pid, 3000, 35_400);
        var (b, _) = await Helpers.SellAsync(app, pid, 1000, 11_800);
        await app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(a, al, 1000));
        await app.Get<ISalesService>().VoidSaleAsync(b, "mistake", null);
        var rep = (await app.Get<IDayCloseService>().PreviewAsync()).Report;
        Assert.Equal(47_200, rep.GrossSalesPaise);
        Assert.Equal(11_800, rep.VoidedPaise);
        Assert.Equal(11_800, rep.ReturnsPaise);
        Assert.Equal(47_200 - 11_800 - 11_800, rep.NetSalesPaise);
        Assert.Equal(1, rep.BillsVoided);
        Assert.Equal(1, rep.ReturnCount);
        Assert.Equal(11_800, rep.RefundsByMode.Single(m => m.Label == "Cash").AmountPaise);
    }

    [Fact]
    public async Task Closing_twice_with_the_same_command_makes_one_close_and_empty_periods_are_fine()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var dc = app.Get<IDayCloseService>();
        var cmd = new DayCloseCommand(Guid.NewGuid(), 5000, 5000);
        var a = await dc.CloseAsync(cmd);
        var b = await dc.CloseAsync(cmd);
        Assert.False(a.AlreadyExisted);
        Assert.True(b.AlreadyExisted);
        Assert.Equal(a.Id, b.Id);
        Assert.Single(await dc.ListAsync());
        Assert.Equal(5000, await CashBooksAsync(app));                                                             // the opening cash was posted only once

        var empty = await CloseExactAsync(app);                                                                    // nothing happened since
        Assert.Equal(0, empty.VariancePaise);
        Assert.Equal(2, (await dc.ListAsync()).Count);
    }

    [Fact]
    public async Task Bad_input_is_refused_and_only_the_admin_can_use_the_cash_drawer_screens()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var dc = app.Get<IDayCloseService>();
        await Assert.ThrowsAsync<ValidationException>(() => dc.CloseAsync(new DayCloseCommand(Guid.NewGuid(), -1, 0)));
        await Assert.ThrowsAsync<ValidationException>(() => dc.CloseAsync(new DayCloseCommand(Guid.NewGuid(), 0, -5)));
        await Assert.ThrowsAsync<ValidationException>(() => dc.RecordMovementAsync(CashMovementType.Expense, 0, "tea"));
        await Assert.ThrowsAsync<ValidationException>(() => dc.RecordMovementAsync(CashMovementType.Expense, 100, " "));
        await Assert.ThrowsAsync<ValidationException>(() => dc.RecordMovementAsync((CashMovementType)99, 100, "tea"));
        Assert.Empty(await dc.ListMovementsAsync());

        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => dc.PreviewAsync());
        await Assert.ThrowsAsync<PermissionDeniedException>(() => dc.CloseAsync(new DayCloseCommand(Guid.NewGuid(), 0, 0)));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => dc.RecordMovementAsync(CashMovementType.Expense, 100, "tea"));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => dc.ListAsync());
    }

    [Fact]
    public async Task Petty_cash_movements_post_to_the_right_accounts_and_only_show_for_the_open_period()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var dc = app.Get<IDayCloseService>();
        await dc.RecordMovementAsync(CashMovementType.OwnerPayIn, 100_000, "Float");
        await dc.RecordMovementAsync(CashMovementType.Expense, 4_500, "Tea");
        await dc.RecordMovementAsync(CashMovementType.OwnerWithdrawal, 10_000, "Owner");
        Assert.Equal(100_000 - 4_500 - 10_000, await CashBooksAsync(app));
        Assert.Equal(4_500, await Helpers.NetAsync(app, AccountCodes.ShopExpenses));
        Assert.Equal(10_000, await Helpers.NetAsync(app, AccountCodes.OwnerDrawings));
        Assert.Equal(-100_000, await Helpers.NetAsync(app, AccountCodes.OwnerFunds));
        Assert.Equal(3, (await dc.ListMovementsAsync()).Count);
        await CloseExactAsync(app);
        Assert.Empty(await dc.ListMovementsAsync());                                                               // a new period starts clean
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task A_locked_period_blocks_closing_and_petty_cash()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<ISettingsService>().SetAsync(SettingKeys.LockedThrough, DateTime.Today.ToString("yyyy-MM-dd"));
        var dc = app.Get<IDayCloseService>();
        await Assert.ThrowsAsync<BusinessRuleException>(() => dc.CloseAsync(new DayCloseCommand(Guid.NewGuid(), 0, 0)));
        await Assert.ThrowsAsync<BusinessRuleException>(() => dc.RecordMovementAsync(CashMovementType.Expense, 100, "tea"));
    }

    [Fact]
    public async Task A_database_from_the_previous_release_is_upgraded_for_the_cash_drawer()
    {
        var app1 = TestApp.Create();
        var pid = await app1.SeedAsync();
        await app1.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800));
        var dir = app1.Dir;
        var factory = app1.Get<DbFactory>();
        var dbPath = app1.Paths.DbPath;

        SqliteConnection.ClearAllPools();
        using (var raw = factory.OpenRaw(dbPath))
        using (var cmd = raw.CreateCommand())
        {
            cmd.CommandText = """
                DROP TABLE DayCloses; DROP TABLE CashMovements;
                DELETE FROM Accounts WHERE Code IN ('3010','3020','5940');
                DELETE FROM SchemaVersions WHERE Version = 4;
                """;
            cmd.ExecuteNonQuery();
        }
        app1.Services.Dispose();

        using var app2 = TestApp.Create(dir);
        await app2.SignInAsAdminAsync();
        var dc = app2.Get<IDayCloseService>();
        await dc.RecordMovementAsync(CashMovementType.Expense, 500, "tea");                                       // needs the new account and table
        var closed = await CloseExactAsync(app2);
        Assert.Equal(0, closed.VariancePaise);
        Assert.Equal(11_800 - 500, await CashBooksAsync(app2));
        await Helpers.AssertBooksBalancedAsync(app2);
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
