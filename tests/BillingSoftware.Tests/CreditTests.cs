using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Export;
using BillingSoftware.Infrastructure.Persistence;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BillingSoftware.Tests;

public class CreditTests
{
    private static DateOnly Today => Helpers.Today;

    private static async Task<PartyDto> CustomerAsync(TestApp app, string name = "Ravi Traders", long limit = 0) =>
        await app.Get<IPartyService>().SaveAsync(new PartyDto(0, name, "9000000001", null, "33", 0, limit));

    private static Task<long> OwedAsync(TestApp app, long id) =>
        app.Get<IPartyService>().SearchAsync("", 100).ContinueWith(t => t.Result.Single(p => p.Id == id).OutstandingPaise);

    private static async Task OpeningDueAsync(TestApp app, long partyId, long amount, int daysAgo) =>
        await app.Get<IOpeningBalanceService>().AddAsync(new AddOpeningCommand(Guid.NewGuid(), OpeningKind.CustomerDue, Today.AddDays(-daysAgo), partyId, null, "", amount, null, null));

    private static async Task<long> ReceivablesBooksAsync(TestApp app) =>
        (await app.Get<IReportService>().TrialBalanceAsync(Today.AddDays(1))).Rows.Where(r => r.Code == AccountCodes.Receivables).Sum(r => r.DebitPaise - r.CreditPaise);

    // ------------------------------------------------------------------ credit limit

    [Fact]
    public async Task A_credit_limit_is_saved_shown_and_audited()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var c = await CustomerAsync(app, limit: 50_000);
        Assert.Equal(50_000, c.CreditLimitPaise);
        var listed = (await app.Get<IPartyService>().SearchAsync("Ravi")).Single();
        Assert.Equal(50_000, listed.CreditLimitPaise);
        Assert.False(listed.IsOverLimit);

        var changed = await app.Get<IPartyService>().SaveAsync(c with { CreditLimitPaise = 80_000 });
        Assert.Equal(80_000, changed.CreditLimitPaise);
        await using var db = app.Get<DbFactory>().Create();
        Assert.Contains(db.AuditEvents.Select(e => e.Details).ToList(), d => d.Contains("credit limit 80000"));
    }

    [Fact]
    public async Task Bad_credit_limits_are_refused_and_a_cashier_cannot_set_one()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.AddCashierAsync();
        var parties = app.Get<IPartyService>();
        await Assert.ThrowsAsync<ValidationException>(() => parties.SaveAsync(new PartyDto(0, "A", null, null, "33", 0, -1)));
        await Assert.ThrowsAsync<ValidationException>(() => parties.SaveAsync(new PartyDto(0, "A", null, null, "33", 0, 100_000_000_01)));
        await app.SignInAsCashierAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => parties.SaveAsync(new PartyDto(0, "New customer", null, null, "33", 0, 10_000)));
        var ok = await parties.SaveAsync(new PartyDto(0, "New customer", null, null, "33", 0));                 // no limit: fine
        Assert.Equal(0, ok.CreditLimitPaise);
    }

    [Fact]
    public async Task A_cashier_needs_the_Admin_for_a_credit_sale_that_goes_over_the_limit()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();                                                   // 118.00 a piece
        var ravi = await CustomerAsync(app, limit: 30_000);                                // limit 300.00
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        var sales = app.Get<ISalesService>();

        await sales.CompleteSaleAsync(TestApp.Sale(soap, 1000, 11_800, PaymentMode.Credit, partyId: ravi.Id));      // owes 118.00
        await sales.CompleteSaleAsync(TestApp.Sale(soap, 1000, 11_800, PaymentMode.Credit, partyId: ravi.Id));      // owes 236.00
        Assert.Equal(23_600, await OwedAsync(app, ravi.Id));

        var over = TestApp.Sale(soap, 1000, 11_800, PaymentMode.Credit, partyId: ravi.Id);                           // would be 354.00
        var ex = await Assert.ThrowsAsync<ApprovalRequiredException>(() => sales.CompleteSaleAsync(over));
        Assert.Equal(ApprovalReason.CreditLimit, ex.Reason);
        Assert.Contains("credit limit", ex.Message);
        Assert.Equal(23_600, await OwedAsync(app, ravi.Id));                                                        // nothing was saved

        var approver = await app.Get<IAuthService>().VerifyAdminApprovalAsync(TestApp.AdminPassword);
        await sales.CompleteSaleAsync(over with { ApprovedByUserId = approver, ApprovalNote = "Pays on Friday" });
        Assert.Equal(35_400, await OwedAsync(app, ravi.Id));
        Assert.True((await app.Get<IPartyService>().SearchAsync("Ravi")).Single().IsOverLimit);

        // paying cash for the same goods does not touch the limit
        await sales.CompleteSaleAsync(TestApp.Sale(soap, 1000, 11_800, PaymentMode.Cash, partyId: ravi.Id));
        await app.SignInAsAdminAsync();
        Assert.Equal(35_400, await OwedAsync(app, ravi.Id));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task The_Admin_may_sell_over_the_limit_and_customers_without_a_limit_are_never_stopped()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var limited = await CustomerAsync(app, "Limited", 10_000);
        var free = await CustomerAsync(app, "No limit");
        var sales = app.Get<ISalesService>();
        await sales.CompleteSaleAsync(TestApp.Sale(soap, 2000, 23_600, PaymentMode.Credit, partyId: limited.Id));  // the Admin: allowed
        Assert.Equal(23_600, await OwedAsync(app, limited.Id));

        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        await sales.CompleteSaleAsync(TestApp.Sale(soap, 3000, 35_400, PaymentMode.Credit, partyId: free.Id));
        Assert.Equal(35_400, await OwedAsync(app, free.Id));
    }

    [Fact]
    public async Task Opening_dues_count_towards_the_limit_and_a_part_payment_makes_room()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var ravi = await CustomerAsync(app, limit: 30_000);
        await OpeningDueAsync(app, ravi.Id, 25_000, 10);                                    // already owes 250.00
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        var sales = app.Get<ISalesService>();
        var bill = TestApp.Sale(soap, 1000, 11_800, PaymentMode.Credit, partyId: ravi.Id);
        await Assert.ThrowsAsync<ApprovalRequiredException>(() => sales.CompleteSaleAsync(bill));
        await app.Get<IPartyService>().ReceivePaymentAsync(ravi.Id, 10_000, PaymentMode.Cash);   // owes 150.00; 150 + 118 = 268 is within 300
        await sales.CompleteSaleAsync(bill);
        Assert.Equal(26_800, await OwedAsync(app, ravi.Id));
    }

    // ------------------------------------------------------------------ statement

    [Fact]
    public async Task A_statement_lists_every_movement_and_ends_on_the_balance_the_customer_owes()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var ravi = await CustomerAsync(app);
        await OpeningDueAsync(app, ravi.Id, 100_000, 40);
        var (sale, line) = await Helpers.SellAsync(app, soap, 2000, 23_600, PaymentMode.Credit, ravi.Id);
        await app.Get<IPartyService>().ReceivePaymentAsync(ravi.Id, 30_000, PaymentMode.Upi);
        await app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(sale, line, 1000, PaymentMode.Credit));           // 118.00 credit note
        await Helpers.SellAsync(app, soap, 1000, 11_800, PaymentMode.Cash, ravi.Id);                                   // a cash bill: not on the account

        var st = await app.Get<ICreditService>().GetStatementAsync(ravi.Id, Today.AddDays(-60), Today);
        Assert.Equal(0, st.BroughtForwardPaise);
        Assert.Equal(new[] { "Opening balance", "Credit sale", "Receipt", "Credit note" }, st.Entries.Select(e => e.Kind).ToArray());
        Assert.Equal(new long[] { 100_000, 123_600, 93_600, 81_800 }, st.Entries.Select(e => e.BalancePaise).ToArray());
        Assert.Equal(100_000 + 23_600, st.TotalDebitPaise);
        Assert.Equal(30_000 + 11_800, st.TotalCreditPaise);
        Assert.Equal(await OwedAsync(app, ravi.Id), st.ClosingPaise);
        Assert.Equal(st.BroughtForwardPaise + st.TotalDebitPaise - st.TotalCreditPaise, st.ClosingPaise);
        Assert.StartsWith("INV/", st.Entries[1].Reference);
        Assert.StartsWith("CN/", st.Entries[3].Reference);
    }

    [Fact]
    public async Task A_later_period_starts_from_the_balance_brought_forward()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var ravi = await CustomerAsync(app);
        await OpeningDueAsync(app, ravi.Id, 100_000, 40);
        await Helpers.SellAsync(app, soap, 1000, 11_800, PaymentMode.Credit, ravi.Id);
        var credit = app.Get<ICreditService>();

        var recent = await credit.GetStatementAsync(ravi.Id, Today.AddDays(-5), Today);
        Assert.Equal(100_000, recent.BroughtForwardPaise);
        Assert.Single(recent.Entries);
        Assert.Equal(111_800, recent.ClosingPaise);

        var earlier = await credit.GetStatementAsync(ravi.Id, Today.AddDays(-60), Today.AddDays(-10));          // before the bill
        Assert.Single(earlier.Entries);
        Assert.Equal(100_000, earlier.ClosingPaise);

        var before = await credit.GetStatementAsync(ravi.Id, Today.AddDays(-90), Today.AddDays(-50));
        Assert.Empty(before.Entries);
        Assert.Equal(0, before.ClosingPaise);
    }

    [Fact]
    public async Task A_cancelled_bill_shows_as_charged_and_cancelled_and_nets_to_nothing()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var ravi = await CustomerAsync(app);
        var (sale, _) = await Helpers.SellAsync(app, soap, 1000, 11_800, PaymentMode.Credit, ravi.Id);
        await app.Get<ISalesService>().VoidSaleAsync(sale, "Wrong customer", null);
        var st = await app.Get<ICreditService>().GetStatementAsync(ravi.Id, Today.AddDays(-1), Today);
        Assert.Equal(new[] { "Credit sale", "Bill cancelled" }, st.Entries.Select(e => e.Kind).ToArray());
        Assert.Contains("Wrong customer", st.Entries[1].Description);
        Assert.Equal(0, st.ClosingPaise);
        Assert.Equal(0, await OwedAsync(app, ravi.Id));
    }

    [Fact]
    public async Task Statement_dates_and_customers_are_checked_and_only_the_Admin_can_see_them()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ravi = await CustomerAsync(app);
        var credit = app.Get<ICreditService>();
        await Assert.ThrowsAsync<ValidationException>(() => credit.GetStatementAsync(ravi.Id, Today, Today.AddDays(-1)));
        await Assert.ThrowsAsync<ValidationException>(() => credit.GetStatementAsync(ravi.Id, Today.AddYears(-12), Today));
        await Assert.ThrowsAsync<ValidationException>(() => credit.GetStatementAsync(9999, Today.AddDays(-1), Today));
        var empty = await credit.GetStatementAsync(ravi.Id, Today.AddDays(-1), Today);
        Assert.Empty(empty.Entries);

        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => credit.GetStatementAsync(ravi.Id, Today.AddDays(-1), Today));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => credit.GetAgeingAsync(Today));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => credit.GetReminderAsync(ravi.Id));
    }

    // ------------------------------------------------------------------ ageing

    [Fact]
    public async Task Ageing_splits_what_is_owed_by_age_taking_payments_against_the_oldest_first()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ravi = await CustomerAsync(app);
        await OpeningDueAsync(app, ravi.Id, 10_000, 100);          // over 90 days
        await OpeningDueAsync(app, ravi.Id, 20_000, 70);           // 61-90
        await OpeningDueAsync(app, ravi.Id, 30_000, 40);           // 31-60
        await OpeningDueAsync(app, ravi.Id, 40_000, 5);            // current
        await app.Get<IPartyService>().ReceivePaymentAsync(ravi.Id, 15_000, PaymentMode.Cash);   // clears the oldest and part of the next

        var r = await app.Get<ICreditService>().GetAgeingAsync(Today);
        var row = r.Rows.Single();
        Assert.Equal(85_000, row.OutstandingPaise);
        Assert.Equal(0, row.Over90Paise);
        Assert.Equal(15_000, row.Days61To90Paise);
        Assert.Equal(30_000, row.Days31To60Paise);
        Assert.Equal(40_000, row.CurrentPaise);
        Assert.Equal(row.OutstandingPaise, row.CurrentPaise + row.Days31To60Paise + row.Days61To90Paise + row.Over90Paise);
        Assert.Equal(70, row.OldestDays);
        Assert.Equal(85_000, r.TotalPaise);
        Assert.Equal(row.OutstandingPaise, await OwedAsync(app, ravi.Id));
    }

    [Fact]
    public async Task Ageing_buckets_change_with_the_date_and_edges_fall_in_the_right_bucket()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ravi = await CustomerAsync(app);
        await OpeningDueAsync(app, ravi.Id, 1_000, 30);            // exactly 30 days: still current
        await OpeningDueAsync(app, ravi.Id, 2_000, 31);            // 31-60
        await OpeningDueAsync(app, ravi.Id, 4_000, 60);            // exactly 60: 31-60
        await OpeningDueAsync(app, ravi.Id, 8_000, 61);            // 61-90
        await OpeningDueAsync(app, ravi.Id, 16_000, 90);           // exactly 90: 61-90
        await OpeningDueAsync(app, ravi.Id, 32_000, 91);           // over 90
        var row = (await app.Get<ICreditService>().GetAgeingAsync(Today)).Rows.Single();
        Assert.Equal(1_000, row.CurrentPaise);
        Assert.Equal(6_000, row.Days31To60Paise);
        Assert.Equal(24_000, row.Days61To90Paise);
        Assert.Equal(32_000, row.Over90Paise);

        // looking at an earlier date: the debts that did not exist then are left out
        var past = (await app.Get<ICreditService>().GetAgeingAsync(Today.AddDays(-60))).Rows.Single();
        // 60 days ago only the 60-, 61-, 90- and 91-day-old debts existed: 4,000 + 8,000 + 16,000 + 32,000
        Assert.Equal(60_000, past.OutstandingPaise);
    }

    [Fact]
    public async Task Ageing_lists_only_customers_who_owe_most_overdue_first_and_flags_those_over_their_limit()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var oldDebtor = await CustomerAsync(app, "Old debtor", 5_000);
        var newDebtor = await CustomerAsync(app, "New debtor");
        var clear = await CustomerAsync(app, "Paid up");
        var voided = await CustomerAsync(app, "Cancelled bill");
        await OpeningDueAsync(app, oldDebtor.Id, 20_000, 120);
        await Helpers.SellAsync(app, soap, 2000, 23_600, PaymentMode.Credit, newDebtor.Id);
        await Helpers.SellAsync(app, soap, 1000, 11_800, PaymentMode.Credit, clear.Id);
        await app.Get<IPartyService>().ReceivePaymentAsync(clear.Id, 11_800, PaymentMode.Cash);
        var (cancelled, _) = await Helpers.SellAsync(app, soap, 1000, 11_800, PaymentMode.Credit, voided.Id);
        await app.Get<ISalesService>().VoidSaleAsync(cancelled, "Mistake", null);

        var r = await app.Get<ICreditService>().GetAgeingAsync(Today);
        Assert.Equal(new[] { "Old debtor", "New debtor" }, r.Rows.Select(x => x.Name).ToArray());
        Assert.True(r.Rows[0].OverLimit);
        Assert.False(r.Rows[1].OverLimit);
        Assert.Equal(20_000, r.Over90Paise);
        Assert.Equal(23_600, r.CurrentPaise);
        Assert.Equal(43_600, r.TotalPaise);
    }

    [Fact]
    public async Task Ageing_adds_up_to_the_customers_balances_and_the_receivables_in_the_books()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var a = await CustomerAsync(app, "A");
        var b = await CustomerAsync(app, "B");
        var c = await CustomerAsync(app, "C");
        await OpeningDueAsync(app, a.Id, 50_000, 95);
        await OpeningDueAsync(app, c.Id, 12_300, 20);
        var (sale, line) = await Helpers.SellAsync(app, soap, 3000, 35_400, PaymentMode.Credit, a.Id);
        await Helpers.SellAsync(app, soap, 2000, 23_600, PaymentMode.Credit, b.Id);
        await app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(sale, line, 1000, PaymentMode.Credit));
        await app.Get<IPartyService>().ReceivePaymentAsync(a.Id, 20_000, PaymentMode.Upi);
        await app.Get<IPartyService>().ReceivePaymentAsync(c.Id, 2_300, PaymentMode.Cash);
        var (gone, _) = await Helpers.SellAsync(app, soap, 1000, 11_800, PaymentMode.Credit, c.Id);
        await app.Get<ISalesService>().VoidSaleAsync(gone, "Mistake", null);

        var r = await app.Get<ICreditService>().GetAgeingAsync(Today);
        var owed = (await app.Get<IPartyService>().SearchAsync("", 100)).Sum(p => p.OutstandingPaise);
        Assert.Equal(owed, r.TotalPaise);
        Assert.Equal(await ReceivablesBooksAsync(app), r.TotalPaise);
        Assert.Equal(r.TotalPaise, r.CurrentPaise + r.Days31To60Paise + r.Days61To90Paise + r.Over90Paise);
        Assert.All(r.Rows, x => Assert.Equal(x.OutstandingPaise, x.CurrentPaise + x.Days31To60Paise + x.Days61To90Paise + x.Over90Paise));
        // each statement ends on the same figure
        foreach (var p in new[] { a, b, c })
        {
            var st = await app.Get<ICreditService>().GetStatementAsync(p.Id, Today.AddDays(-200), Today);
            Assert.Equal(await OwedAsync(app, p.Id), st.ClosingPaise);
        }
    }

    [Fact]
    public async Task Ageing_for_a_future_date_is_refused()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await Assert.ThrowsAsync<ValidationException>(() => app.Get<ICreditService>().GetAgeingAsync(Today.AddDays(5)));
        Assert.Empty((await app.Get<ICreditService>().GetAgeingAsync(Today)).Rows);
    }

    // ------------------------------------------------------------------ reminders

    [Fact]
    public async Task A_reminder_lists_the_bills_still_unpaid_and_adds_up_to_what_is_owed()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var ravi = await CustomerAsync(app);
        await OpeningDueAsync(app, ravi.Id, 10_000, 50);
        await Helpers.SellAsync(app, soap, 1000, 11_800, PaymentMode.Credit, ravi.Id);
        await Helpers.SellAsync(app, soap, 2000, 23_600, PaymentMode.Credit, ravi.Id);
        await app.Get<IPartyService>().ReceivePaymentAsync(ravi.Id, 15_000, PaymentMode.Cash);    // clears the opening 100.00 and 50.00 of the first bill

        var rem = await app.Get<ICreditService>().GetReminderAsync(ravi.Id);
        Assert.Equal("Ravi Traders", rem.CustomerName);
        Assert.Equal(2, rem.Items.Count);
        Assert.Equal(new long[] { 6_800, 23_600 }, rem.Items.Select(i => i.UnpaidPaise).ToArray());
        Assert.Equal(rem.OutstandingPaise, rem.Items.Sum(i => i.UnpaidPaise));
        Assert.Equal(await OwedAsync(app, ravi.Id), rem.OutstandingPaise);
        Assert.All(rem.Items, i => Assert.StartsWith("INV/", i.Reference));
    }

    [Fact]
    public async Task A_reminder_for_a_customer_who_owes_nothing_is_refused()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ravi = await CustomerAsync(app);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => app.Get<ICreditService>().GetReminderAsync(ravi.Id));
        Assert.Contains("owes nothing", ex.Message);
        await Assert.ThrowsAsync<ValidationException>(() => app.Get<ICreditService>().GetReminderAsync(9999));
    }

    [Fact]
    public async Task An_opening_balance_shows_as_an_earlier_balance_in_a_reminder()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ravi = await CustomerAsync(app);
        await OpeningDueAsync(app, ravi.Id, 10_000, 50);
        var rem = await app.Get<ICreditService>().GetReminderAsync(ravi.Id);
        Assert.Equal("Earlier balance", rem.Items.Single().Reference);
        Assert.Equal(50, rem.Items.Single().Days);
    }

    // ------------------------------------------------------------------ Excel

    [Fact]
    public async Task The_ageing_and_statement_Excel_files_hold_real_numbers_and_match_the_reports()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var ravi = await CustomerAsync(app);
        await OpeningDueAsync(app, ravi.Id, 10_000, 100);
        await Helpers.SellAsync(app, soap, 1000, 11_800, PaymentMode.Credit, ravi.Id);
        var credit = app.Get<ICreditService>();
        var ageing = await credit.GetAgeingAsync(Today);
        var st = await credit.GetStatementAsync(ravi.Id, Today.AddDays(-200), Today);

        var f1 = Path.Combine(Path.GetTempPath(), $"ageing-{Guid.NewGuid():N}.xlsx");
        var f2 = Path.Combine(Path.GetTempPath(), $"statement-{Guid.NewGuid():N}.xlsx");
        try
        {
            CreditExcelExporter.WriteAgeing(ageing, f1);
            CreditExcelExporter.WriteStatement(st, f2);
            using var wb1 = new XLWorkbook(f1);
            var ws1 = wb1.Worksheet("Who owes you");
            Assert.Equal(ageing.TotalPaise / 100m, ws1.Cell(6, 3).GetValue<decimal>());
            Assert.Equal("Ravi Traders", ws1.Cell(6, 1).GetString());
            Assert.Equal(ageing.Over90Paise / 100m, ws1.Cell(6, 7).GetValue<decimal>());
            using var wb2 = new XLWorkbook(f2);
            var ws2 = wb2.Worksheet("Statement");
            Assert.Equal(st.ClosingPaise / 100m, ws2.Cell(7 + st.Entries.Count, 7).GetValue<decimal>());
        }
        finally { try { File.Delete(f1); File.Delete(f2); } catch (IOException) { } }
    }

    // ------------------------------------------------------------------ upgrade

    [Fact]
    public async Task A_database_from_the_previous_release_gets_the_credit_limit_column()
    {
        var app1 = TestApp.Create();
        await app1.SeedAsync();
        await CustomerAsync(app1);
        var dir = app1.Dir;
        var factory = app1.Get<DbFactory>();
        var dbPath = app1.Paths.DbPath;
        SqliteConnection.ClearAllPools();
        using (var raw = factory.OpenRaw(dbPath))
        using (var cmd = raw.CreateCommand())
        {
            cmd.CommandText = "ALTER TABLE Parties DROP COLUMN CreditLimitPaise; DELETE FROM SchemaVersions WHERE Version = 8;";
            cmd.ExecuteNonQuery();
        }
        app1.Services.Dispose();

        using var app2 = TestApp.Create(dir);
        await app2.SignInAsAdminAsync();
        var ravi = (await app2.Get<IPartyService>().SearchAsync("Ravi")).Single();
        Assert.Equal(0, ravi.CreditLimitPaise);                                    // existing customers have no limit
        var saved = await app2.Get<IPartyService>().SaveAsync(ravi with { CreditLimitPaise = 20_000 });
        Assert.Equal(20_000, saved.CreditLimitPaise);
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
