using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BillingSoftware.Tests;

public class ExpenseTests
{
    private static DateOnly Today => Helpers.Today;

    private static async Task<long> CategoryAsync(TestApp app, string name) =>
        (await app.Get<IExpenseService>().ListCategoriesAsync()).Single(c => c.Name == name).Id;

    private static RecordExpenseCommand Cmd(long categoryId, long amount, PaymentMode mode = PaymentMode.Cash, DateOnly? date = null,
        string payTo = "Landlord", string description = "Shop rent", Guid? id = null) =>
        new(id ?? Guid.NewGuid(), date ?? Today, categoryId, payTo, description, null, amount, mode);

    private static async Task<long> CashBooksAsync(TestApp app) => await Helpers.NetAsync(app, AccountCodes.Cash);

    [Fact]
    public async Task A_cash_expense_posts_to_its_own_account_and_reaches_the_profit_and_loss()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ex = app.Get<IExpenseService>();
        var rent = await CategoryAsync(app, "Rent");
        var r = await ex.RecordAsync(Cmd(rent, 1_500_000));

        Assert.Equal($"EXP/{FinancialYears.For(DateTime.Now).Label}/000001", r.Number);
        Assert.Equal(1_500_000, await Helpers.NetAsync(app, "6010"));
        Assert.Equal(-1_500_000, await CashBooksAsync(app));
        await Helpers.AssertBooksBalancedAsync(app);

        var pnl = (await app.Get<IProfitLossService>().BuildAsync(Today, Today)).Current;
        Assert.Equal(1_500_000, pnl.Expenses.Single(e => e.Name == "Rent").AmountPaise);
        Assert.Equal(-1_500_000, pnl.NetProfitPaise);
    }

    [Theory]
    [InlineData(PaymentMode.Upi, AccountCodes.Bank, -1)]
    [InlineData(PaymentMode.Card, AccountCodes.Bank, -1)]
    [InlineData(PaymentMode.Credit, AccountCodes.ExpensesPayable, -1)]
    public async Task The_way_it_was_paid_decides_the_other_side_of_the_entry(PaymentMode mode, string account, int sign)
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var cat = await CategoryAsync(app, "Electricity");
        await app.Get<IExpenseService>().RecordAsync(Cmd(cat, 250_000, mode, description: "October bill", payTo: "Electricity board"));
        Assert.Equal(sign * 250_000, await Helpers.NetAsync(app, account));
        Assert.Equal(250_000, await Helpers.NetAsync(app, "6030"));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task An_unpaid_expense_can_be_paid_in_parts_and_the_expense_is_counted_when_entered_not_when_paid()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ex = app.Get<IExpenseService>();
        var salary = await CategoryAsync(app, "Salaries and wages");
        var r = await ex.RecordAsync(Cmd(salary, 1_000_000, PaymentMode.Credit, payTo: "Priya", description: "October salary"));
        var profitBefore = (await app.Get<IProfitLossService>().BuildAsync(Today, Today)).Current.NetProfitPaise;

        Assert.Equal(1_000_000, (await ex.ListAsync(Today, Today, unpaidOnly: true)).Single().OutstandingPaise);
        await ex.PayAsync(r.Id, 400_000, PaymentMode.Cash);
        await ex.PayAsync(r.Id, 100_000, PaymentMode.Upi);
        Assert.Equal(500_000, (await ex.ListAsync(Today, Today)).Single().OutstandingPaise);
        Assert.Equal(-500_000, await Helpers.NetAsync(app, AccountCodes.ExpensesPayable));
        Assert.Equal(-400_000, await CashBooksAsync(app));

        await Assert.ThrowsAsync<ValidationException>(() => ex.PayAsync(r.Id, 500_001, PaymentMode.Cash));
        await Assert.ThrowsAsync<ValidationException>(() => ex.PayAsync(r.Id, 100, PaymentMode.Credit));
        await Assert.ThrowsAsync<ValidationException>(() => ex.PayAsync(r.Id, 0, PaymentMode.Cash));
        await ex.PayAsync(r.Id, 500_000, PaymentMode.Cash);
        Assert.Empty(await ex.ListAsync(Today, Today, unpaidOnly: true));
        Assert.Equal(0, await Helpers.NetAsync(app, AccountCodes.ExpensesPayable));
        Assert.Equal(profitBefore, (await app.Get<IProfitLossService>().BuildAsync(Today, Today)).Current.NetProfitPaise);   // paying does not change profit
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Paying_an_expense_that_was_already_paid_or_cancelled_is_refused()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ex = app.Get<IExpenseService>();
        var cat = await CategoryAsync(app, "Cleaning");
        var paid = await ex.RecordAsync(Cmd(cat, 10_000));
        await Assert.ThrowsAsync<ValidationException>(() => ex.PayAsync(paid.Id, 1_000, PaymentMode.Cash));
        var unpaid = await ex.RecordAsync(Cmd(cat, 10_000, PaymentMode.Credit));
        await ex.VoidAsync(unpaid.Id, "entered twice");
        await Assert.ThrowsAsync<ValidationException>(() => ex.PayAsync(unpaid.Id, 1_000, PaymentMode.Cash));
    }

    [Fact]
    public async Task A_mistaken_expense_can_be_cancelled_once_and_never_after_payments()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ex = app.Get<IExpenseService>();
        var cat = await CategoryAsync(app, "Repairs and maintenance");
        var r = await ex.RecordAsync(Cmd(cat, 80_000, description: "Fan repair"));
        await Assert.ThrowsAsync<ValidationException>(() => ex.VoidAsync(r.Id, " "));
        await ex.VoidAsync(r.Id, "Wrong amount entered");
        Assert.Equal(0, await Helpers.NetAsync(app, "6070"));
        Assert.Equal(0, await CashBooksAsync(app));
        Assert.Equal(ExpenseStatus.Voided, (await ex.ListAsync(Today, Today)).Single().Status);
        Assert.Equal(0, (await ex.SummaryAsync(Today, Today)).TotalPaise);                                      // cancelled expenses are not counted
        await Assert.ThrowsAsync<ValidationException>(() => ex.VoidAsync(r.Id, "again"));

        var credit = await ex.RecordAsync(Cmd(cat, 50_000, PaymentMode.Credit));
        await ex.PayAsync(credit.Id, 10_000, PaymentMode.Cash);
        await Assert.ThrowsAsync<ValidationException>(() => ex.VoidAsync(credit.Id, "mistake"));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task An_expense_is_counted_in_the_day_it_belongs_to_and_dates_are_checked()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ex = app.Get<IExpenseService>();
        var cat = await CategoryAsync(app, "Rent");
        var earlier = Today.AddDays(-3);
        await ex.RecordAsync(Cmd(cat, 700_000, date: earlier));
        var pnl = app.Get<IProfitLossService>();
        Assert.Equal(700_000, (await pnl.BuildAsync(earlier, earlier)).Current.TotalExpensesPaise);
        Assert.Equal(0, (await pnl.BuildAsync(Today, Today)).Current.TotalExpensesPaise);

        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Cmd(cat, 100, date: Today.AddDays(1))));      // future
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Cmd(cat, 100, date: Today.AddYears(-3))));    // too old

        await app.Get<ISettingsService>().SetAsync(SettingKeys.LockedThrough, Today.AddDays(-1).ToString("yyyy-MM-dd"));
        await Assert.ThrowsAsync<BusinessRuleException>(() => ex.RecordAsync(Cmd(cat, 100, date: earlier)));              // that period is locked
        await ex.RecordAsync(Cmd(cat, 100));                                                                               // today is open
    }

    [Fact]
    public async Task Categories_have_their_own_accounts_and_custom_ones_can_be_added_and_switched_off()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ex = app.Get<IExpenseService>();
        var cats = await ex.ListCategoriesAsync();
        Assert.Equal(ExpenseDefaults.Categories.Count, cats.Count);
        Assert.All(ExpenseDefaults.Categories, d => Assert.Contains(cats, c => c.AccountCode == d.Code && c.Name == d.Name && c.IsDefault));

        var a = await ex.AddCategoryAsync("Festival decoration");
        var b = await ex.AddCategoryAsync("Staff uniforms");
        Assert.Equal("6200", a.AccountCode);
        Assert.Equal("6210", b.AccountCode);
        await Assert.ThrowsAsync<ValidationException>(() => ex.AddCategoryAsync("festival DECORATION"));
        await Assert.ThrowsAsync<ValidationException>(() => ex.AddCategoryAsync("x"));
        await Assert.ThrowsAsync<ValidationException>(() => ex.AddCategoryAsync(new string('y', 41)));

        await ex.RecordAsync(Cmd(a.Id, 30_000, description: "Diwali lights"));
        Assert.Equal(30_000, await Helpers.NetAsync(app, "6200"));
        Assert.Contains((await app.Get<IProfitLossService>().BuildAsync(Today, Today)).Current.Expenses, e => e.Name == "Festival decoration");

        await ex.SetCategoryActiveAsync(a.Id, false);
        Assert.DoesNotContain(await ex.ListCategoriesAsync(), c => c.Id == a.Id);
        Assert.Contains(await ex.ListCategoriesAsync(includeInactive: true), c => c.Id == a.Id);
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Cmd(a.Id, 100)));                              // switched off
        Assert.Single(await ex.ListAsync(Today, Today));                                                                   // old expenses stay
        var other = cats.Single(c => c.AccountCode == "6900");
        await Assert.ThrowsAsync<BusinessRuleException>(() => ex.SetCategoryActiveAsync(other.Id, false));
    }

    [Fact]
    public async Task Expected_cash_at_day_end_includes_every_kind_of_expense_and_still_equals_the_books()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var ex = app.Get<IExpenseService>();
        var dc = app.Get<IDayCloseService>();
        var rent = await CategoryAsync(app, "Rent");
        var tea = await CategoryAsync(app, "Tea and refreshments");

        await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 5000, 59_000));                                   // some cash comes in
        var oldOne = await ex.RecordAsync(Cmd(tea, 5_000));                                                                   // cash expense, first period
        var first = await dc.PreviewAsync(500_000);
        await dc.CloseAsync(new DayCloseCommand(Guid.NewGuid(), first.Report.ExpectedCashPaise, 500_000));

        await ex.RecordAsync(Cmd(rent, 120_000));                                                                             // cash
        await ex.RecordAsync(Cmd(rent, 80_000, PaymentMode.Upi));                                                             // not cash
        var unpaid = await ex.RecordAsync(Cmd(tea, 30_000, PaymentMode.Credit));
        await ex.PayAsync(unpaid.Id, 10_000, PaymentMode.Cash);                                                               // later, in cash
        await ex.PayAsync(unpaid.Id, 5_000, PaymentMode.Upi);                                                                 // later, not cash
        var wrong = await ex.RecordAsync(Cmd(tea, 7_000));
        await ex.VoidAsync(wrong.Id, "typed twice");                                                                          // cash given back
        await ex.VoidAsync(oldOne.Id, "was not ours");                                                                        // voided after its period closed

        var preview = await dc.PreviewAsync();
        Assert.Equal(await CashBooksAsync(app), preview.Report.ExpectedCashPaise);                                           // the invariant
        Assert.Contains(preview.Report.CashLines, l => l.Label == "Cash paid for business expenses" && l.AmountPaise == -(120_000 + 7_000));
        Assert.Contains(preview.Report.CashLines, l => l.Label == "Cash paid later for expenses" && l.AmountPaise == -10_000);
        Assert.Contains(preview.Report.CashLines, l => l.Label == "Cash given back for expenses cancelled" && l.AmountPaise == 7_000 + 5_000);

        var closed = await dc.CloseAsync(new DayCloseCommand(Guid.NewGuid(), preview.Report.ExpectedCashPaise, 0));
        Assert.Equal(0, closed.VariancePaise);
        Assert.Equal(preview.Report.ExpectedCashPaise, await CashBooksAsync(app));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Retrying_a_request_or_bad_input_never_creates_a_wrong_expense()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ex = app.Get<IExpenseService>();
        var cat = await CategoryAsync(app, "Stationery");
        var cmd = Cmd(cat, 25_000);
        var a = await ex.RecordAsync(cmd);
        var b = await ex.RecordAsync(cmd);
        Assert.False(a.AlreadyExisted);
        Assert.True(b.AlreadyExisted);
        Assert.Equal(a.Number, b.Number);
        Assert.Single(await ex.ListAsync(Today, Today));
        Assert.Equal(25_000, await Helpers.NetAsync(app, "6140"));

        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Cmd(cat, 0)));
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Cmd(cat, -5)));
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Cmd(cat, 100_000_000_01)));
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Cmd(cat, 100, payTo: "", description: "")));
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Cmd(cat, 100, description: new string('d', 201))));
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Cmd(9999, 100)));
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Cmd(cat, 100, (PaymentMode)99)));
        Assert.Single(await ex.ListAsync(Today, Today));                                                                   // nothing extra was saved
        Assert.True((await app.Get<IAuditService>().VerifyChainAsync()).Valid);
    }

    [Fact]
    public async Task Only_the_admin_can_record_or_see_expenses()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var cat = await CategoryAsync(app, "Rent");
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        var ex = app.Get<IExpenseService>();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => ex.RecordAsync(Cmd(cat, 100)));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => ex.ListAsync(Today, Today));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => ex.ListCategoriesAsync());
        await Assert.ThrowsAsync<PermissionDeniedException>(() => ex.AddCategoryAsync("Anything"));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => ex.SummaryAsync(Today, Today));
    }

    [Fact]
    public async Task The_summary_by_category_and_the_list_filters_agree_with_the_books()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ex = app.Get<IExpenseService>();
        var rent = await CategoryAsync(app, "Rent");
        var tea = await CategoryAsync(app, "Tea and refreshments");
        await ex.RecordAsync(Cmd(rent, 500_000));
        await ex.RecordAsync(Cmd(tea, 3_000));
        await ex.RecordAsync(Cmd(tea, 2_000, PaymentMode.Credit));
        var voided = await ex.RecordAsync(Cmd(tea, 9_999));
        await ex.VoidAsync(voided.Id, "mistake");

        var s = await ex.SummaryAsync(Today, Today);
        Assert.Equal(505_000, s.TotalPaise);
        Assert.Equal(3, s.Count);
        Assert.Equal("Rent", s.ByCategory[0].Category);                                                                    // biggest first
        Assert.Equal(5_000, s.ByCategory.Single(c => c.Category == "Tea and refreshments").TotalPaise);

        Assert.Equal(4, (await ex.ListAsync(Today, Today)).Count);
        Assert.Equal(3, (await ex.ListAsync(Today, Today, tea)).Count);
        Assert.Single(await ex.ListAsync(Today, Today, unpaidOnly: true));
        Assert.Empty(await ex.ListAsync(Today.AddDays(1), Today.AddDays(5)));
        var pnl = (await app.Get<IProfitLossService>().BuildAsync(Today, Today)).Current;
        Assert.Equal(s.TotalPaise, pnl.TotalExpensesPaise);                                                                // statement agrees with the screen
    }

    [Fact]
    public async Task The_gst_worksheets_mention_expenses_that_were_not_claimed()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<IExpenseService>().RecordAsync(Cmd(await CategoryAsync(app, "Rent"), 1_180_000));
        var r = await app.Get<IGstReportService>().BuildAsync(DateTime.Today.Year, DateTime.Today.Month);
        Assert.Contains(r.Warnings, w => w.Contains("business expense") && w.Contains("not claimed"));
    }

    [Fact]
    public async Task A_database_from_the_previous_release_gets_the_expense_tables_and_categories()
    {
        var app1 = TestApp.Create();
        await app1.SeedAsync();
        var dir = app1.Dir;
        var factory = app1.Get<DbFactory>();
        var dbPath = app1.Paths.DbPath;
        SqliteConnection.ClearAllPools();
        using (var raw = factory.OpenRaw(dbPath))
        using (var cmd = raw.CreateCommand())
        {
            cmd.CommandText = """
                DROP TABLE ExpensePayments; DROP TABLE Expenses; DROP TABLE ExpenseCategories;
                DELETE FROM Accounts WHERE Code = '2010' OR (Code >= '6000' AND Code < '7000');
                DELETE FROM SchemaVersions WHERE Version = 5;
                """;
            cmd.ExecuteNonQuery();
        }
        app1.Services.Dispose();

        using var app2 = TestApp.Create(dir);
        await app2.SignInAsAdminAsync();
        var ex = app2.Get<IExpenseService>();
        Assert.Equal(ExpenseDefaults.Categories.Count, (await ex.ListCategoriesAsync()).Count);
        var r = await ex.RecordAsync(Cmd(await CategoryAsync(app2, "Rent"), 10_000, PaymentMode.Credit));
        await ex.PayAsync(r.Id, 10_000, PaymentMode.Cash);
        await Helpers.AssertBooksBalancedAsync(app2);
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
