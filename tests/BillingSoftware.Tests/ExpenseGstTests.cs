using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BillingSoftware.Tests;

public class ExpenseGstTests
{
    private const string LocalSupplier = "33AAAAA1111A1Z5";      // Tamil Nadu, like the test shop
    private const string OtherState = "29AAAAA2222A1Z5";         // Karnataka
    private static DateOnly Today => Helpers.Today;

    private static async Task<long> CategoryAsync(TestApp app, string name) =>
        (await app.Get<IExpenseService>().ListCategoriesAsync()).Single(c => c.Name == name).Id;

    private static RecordExpenseCommand Bill(long category, long amount, long gst, bool claim = true, string? gstin = LocalSupplier,
        string? reference = "B-101", PaymentMode mode = PaymentMode.Upi, DateOnly? date = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), date ?? Today, category, "Landlord", "Shop rent", reference, amount, mode, gst, claim, gstin);

    private static Task<long> NetAsync(TestApp app, string code) => Helpers.NetAsync(app, code);

    // ------------------------------------------------------------------ posting

    [Fact]
    public async Task Claimed_GST_goes_to_input_credit_and_only_the_amount_before_GST_is_the_expense()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var rent = await CategoryAsync(app, "Rent");
        await app.Get<IExpenseService>().RecordAsync(Bill(rent, 1_180_000, 180_000));       // 11,800.00 paid by UPI, 1,800.00 of it GST

        Assert.Equal(1_000_000, await NetAsync(app, "6010"));                              // rent 10,000.00
        Assert.Equal(90_000, await NetAsync(app, AccountCodes.InputCgst));
        Assert.Equal(90_000, await NetAsync(app, AccountCodes.InputSgst));
        Assert.Equal(0, await NetAsync(app, AccountCodes.InputIgst));
        Assert.Equal(-1_180_000, await NetAsync(app, AccountCodes.Bank));                  // the whole bill was paid
        await Helpers.AssertBooksBalancedAsync(app);

        var list = (await app.Get<IExpenseService>().ListAsync(Today, Today)).Single();
        Assert.Equal(1_180_000, list.AmountPaise);
        Assert.Equal(180_000, list.GstPaise);
        Assert.Equal(180_000, list.ItcPaise);
        var summary = await app.Get<IExpenseService>().SummaryAsync(Today, Today);
        Assert.Equal(1_000_000, summary.TotalPaise);                                        // by-category totals agree with the profit and loss
        var pnl = (await app.Get<IProfitLossService>().BuildAsync(Today, Today)).Current;
        Assert.Equal(summary.TotalPaise, pnl.TotalExpensesPaise);
    }

    [Fact]
    public async Task A_supplier_from_another_state_gives_IGST()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<IExpenseService>().RecordAsync(Bill(await CategoryAsync(app, "Repairs and maintenance"), 590_000, 90_000, gstin: OtherState));
        Assert.Equal(90_000, await NetAsync(app, AccountCodes.InputIgst));
        Assert.Equal(0, await NetAsync(app, AccountCodes.InputCgst));
        Assert.Equal(0, await NetAsync(app, AccountCodes.InputSgst));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task An_odd_paisa_of_GST_goes_to_the_state_head()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<IExpenseService>().RecordAsync(Bill(await CategoryAsync(app, "Rent"), 1_000_001, 1_801));
        Assert.Equal(900, await NetAsync(app, AccountCodes.InputCgst));
        Assert.Equal(901, await NetAsync(app, AccountCodes.InputSgst));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task GST_that_is_not_claimed_stays_in_the_cost()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<IExpenseService>().RecordAsync(Bill(await CategoryAsync(app, "Rent"), 1_180_000, 180_000, claim: false, gstin: null, reference: null));
        Assert.Equal(1_180_000, await NetAsync(app, "6010"));
        Assert.Equal(0, await NetAsync(app, AccountCodes.InputCgst) + await NetAsync(app, AccountCodes.InputSgst) + await NetAsync(app, AccountCodes.InputIgst));
        var list = (await app.Get<IExpenseService>().ListAsync(Today, Today)).Single();
        Assert.Equal(180_000, list.GstPaise);
        Assert.Equal(0, list.ItcPaise);
    }

    [Fact]
    public async Task An_expense_without_GST_posts_exactly_as_before()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<IExpenseService>().RecordAsync(new RecordExpenseCommand(Guid.NewGuid(), Today, await CategoryAsync(app, "Rent"), "Landlord", "Rent", null, 500_000, PaymentMode.Cash));
        Assert.Equal(500_000, await NetAsync(app, "6010"));
        Assert.Equal(-500_000, await NetAsync(app, AccountCodes.Cash));
    }

    // ------------------------------------------------------------------ rules

    [Fact]
    public async Task Bad_GST_entries_are_refused_with_plain_messages()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ex = app.Get<IExpenseService>();
        var rent = await CategoryAsync(app, "Rent");

        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Bill(rent, 100_000, -1)));                                  // negative
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Bill(rent, 100_000, 100_000)));                             // all of it
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Bill(rent, 100_000, 150_000)));
        var high = await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Bill(rent, 100_000, 40_000)));                   // over 28%
        Assert.Contains("28%", high.Message);
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Bill(rent, 100_000, 0)));                                   // claim with no GST
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Bill(rent, 118_000, 18_000, gstin: null)));                  // no supplier GSTIN
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Bill(rent, 118_000, 18_000, gstin: "NOT-A-GSTIN")));
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Bill(rent, 118_000, 18_000, reference: null)));              // no bill number
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Bill(rent, 118_000, 18_000, reference: "  ")));
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Bill(rent, 118_000, 18_000, claim: false, gstin: "BAD")));  // a bad GSTIN is refused even unclaimed
        Assert.Empty(await ex.ListAsync(Today, Today));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Credit_on_food_and_drink_is_refused_but_the_expense_can_still_be_recorded_without_it()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var tea = await CategoryAsync(app, "Tea and refreshments");
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => app.Get<IExpenseService>().RecordAsync(Bill(tea, 11_800, 1_800)));
        Assert.Contains("food", ex.Message);
        await app.Get<IExpenseService>().RecordAsync(Bill(tea, 11_800, 1_800, claim: false, gstin: null, reference: null));
        Assert.Equal(11_800, await NetAsync(app, "6160"));
    }

    [Fact]
    public async Task A_composition_business_or_one_without_a_GSTIN_cannot_claim_credit()
    {
        using (var comp = TestApp.Create())
        {
            await comp.SeedAsync(composition: true);
            var compRent = await CategoryAsync(comp, "Rent");
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => comp.Get<IExpenseService>().RecordAsync(Bill(compRent, 118_000, 18_000)));
            Assert.Contains("composition", ex.Message);
        }
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<ISettingsService>().SaveCompanyAsync(new CompanyDto("Test Stores", null, "33", false, null, null));
        var rentId = await CategoryAsync(app, "Rent");
        var ex2 = await Assert.ThrowsAsync<BusinessRuleException>(() => app.Get<IExpenseService>().RecordAsync(Bill(rentId, 118_000, 18_000)));
        Assert.Contains("GSTIN", ex2.Message);
    }

    [Fact]
    public async Task Depreciation_has_no_GST()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<IOpeningBalanceService>().AddAsync(new AddOpeningCommand(Guid.NewGuid(), OpeningKind.FixedAsset, Today.AddDays(-1), null, null, "Counter", 100_000, null, null));
        var dep = await CategoryAsync(app, "Depreciation");
        await Assert.ThrowsAsync<ValidationException>(() => app.Get<IExpenseService>().RecordAsync(
            new RecordExpenseCommand(Guid.NewGuid(), Today, dep, "", "Yearly", null, 10_000, PaymentMode.NonCash, 1_000, false, null)));
    }

    // ------------------------------------------------------------------ pay later, void, repeat

    [Fact]
    public async Task A_bill_paid_later_owes_the_whole_amount_and_the_credit_is_claimed_at_once()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ex = app.Get<IExpenseService>();
        var e = await ex.RecordAsync(Bill(await CategoryAsync(app, "Rent"), 1_180_000, 180_000, mode: PaymentMode.Credit));
        Assert.Equal(-1_180_000, await NetAsync(app, AccountCodes.ExpensesPayable));
        Assert.Equal(180_000, await NetAsync(app, AccountCodes.InputCgst) + await NetAsync(app, AccountCodes.InputSgst));
        await ex.PayAsync(e.Id, 1_180_000, PaymentMode.Upi);                                  // pays the full bill, GST included
        Assert.Equal(0, await NetAsync(app, AccountCodes.ExpensesPayable));
        Assert.Equal(1_000_000, await NetAsync(app, "6010"));
        var checks = (await app.Get<IBalanceSheetService>().BuildAsync(Today, null)).Checks;
        Assert.All(checks, c => Assert.True(c.Ok, c.Message));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Cancelling_an_expense_takes_the_credit_back()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ex = app.Get<IExpenseService>();
        var e = await ex.RecordAsync(Bill(await CategoryAsync(app, "Rent"), 1_180_000, 180_000));
        await ex.VoidAsync(e.Id, "Entered twice");
        Assert.Equal(0, await NetAsync(app, AccountCodes.InputCgst));
        Assert.Equal(0, await NetAsync(app, AccountCodes.InputSgst));
        Assert.Equal(0, await NetAsync(app, "6010"));
        var g = (await app.Get<IGstReportService>().BuildAsync(Today.Year, Today.Month)).Gstr3b;
        Assert.Equal(0, g.ItcPaise);
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Saving_the_same_bill_twice_claims_the_credit_once()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var cmd = Bill(await CategoryAsync(app, "Rent"), 1_180_000, 180_000);
        await app.Get<IExpenseService>().RecordAsync(cmd);
        var again = await app.Get<IExpenseService>().RecordAsync(cmd);
        Assert.True(again.AlreadyExisted);
        Assert.Equal(180_000, await NetAsync(app, AccountCodes.InputCgst) + await NetAsync(app, AccountCodes.InputSgst));
    }

    [Fact]
    public async Task A_cash_expense_takes_the_whole_bill_out_of_the_drawer()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<IDayCloseService>().RecordMovementAsync(CashMovementType.OwnerPayIn, 2_000_000, "Float");
        await app.Get<IExpenseService>().RecordAsync(Bill(await CategoryAsync(app, "Repairs and maintenance"), 590_000, 90_000, mode: PaymentMode.Cash));
        var p = await app.Get<IDayCloseService>().PreviewAsync();
        Assert.Equal(2_000_000 - 590_000, p.Report.ExpectedCashPaise);
        Assert.Equal(await NetAsync(app, AccountCodes.Cash), p.Report.ExpectedCashPaise);
    }

    // ------------------------------------------------------------------ GST worksheets

    [Fact]
    public async Task The_GST_worksheets_include_expense_credit_and_it_ties_to_the_ledger_with_purchases()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var sup = await Helpers.AddSupplierAsync(app);
        await app.Get<IPurchaseService>().CompleteAsync(Helpers.Buy(sup, soap, 10_000, 5000, 59_000, "S-1"));
        await Helpers.SellAsync(app, soap, 5000, 59_000);
        var ex = app.Get<IExpenseService>();
        await ex.RecordAsync(Bill(await CategoryAsync(app, "Rent"), 1_180_000, 180_000));                                  // intra-state: CGST + SGST
        await ex.RecordAsync(Bill(await CategoryAsync(app, "Repairs and maintenance"), 590_000, 90_000, gstin: OtherState));   // inter-state: IGST

        var r = await app.Get<IGstReportService>().BuildAsync(Today.Year, Today.Month);
        var g = r.Gstr3b;
        Assert.Equal(await NetAsync(app, AccountCodes.InputCgst), g.ItcCgstPaise);
        Assert.Equal(await NetAsync(app, AccountCodes.InputSgst), g.ItcSgstPaise);
        Assert.Equal(await NetAsync(app, AccountCodes.InputIgst), g.ItcIgstPaise);
        Assert.True(g.ItcIgstPaise == 90_000 && g.ItcCgstPaise >= 90_000);

        var rows = r.PurchaseRegister.Where(x => x.PurchaseNo.StartsWith("EXP/")).ToList();
        Assert.Equal(2, rows.Count);
        var rentRow = rows.Single(x => x.IgstPaise == 0);
        Assert.Equal(LocalSupplier, rentRow.SupplierGstin);
        Assert.Equal("B-101", rentRow.SupplierInvoiceNo);
        Assert.Equal(1_000_000, rentRow.TaxablePaise);
        Assert.Equal(180_000, rentRow.ItcClaimedPaise);
        Assert.Equal(0, rentRow.ItcNotClaimedPaise);
        Assert.Equal(1_180_000, rentRow.TotalPaise);
        // every expense that was claimed is listed, so the register's credit equals the 3B credit
        Assert.Equal(g.ItcPaise, r.PurchaseRegister.Sum(x => x.ItcClaimedPaise));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("business expense") && w.Contains("not claimed"));
        Assert.Equal(g.OutCgstPaise - g.ItcCgstPaise, g.NetCgstPaise);
    }

    [Fact]
    public async Task An_unclaimed_expense_is_still_warned_about_and_shown_when_it_had_GST()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ex = app.Get<IExpenseService>();
        await ex.RecordAsync(Bill(await CategoryAsync(app, "Rent"), 1_180_000, 180_000, claim: false, gstin: null, reference: null));
        await ex.RecordAsync(new RecordExpenseCommand(Guid.NewGuid(), Today, await CategoryAsync(app, "Electricity"), "Board", "Bill", null, 300_000, PaymentMode.Upi));
        var r = await app.Get<IGstReportService>().BuildAsync(Today.Year, Today.Month);
        Assert.Contains(r.Warnings, w => w.Contains("2 business expense") && w.Contains("not claimed"));
        var row = r.PurchaseRegister.Single(x => x.PurchaseNo.StartsWith("EXP/"));
        Assert.Equal(0, row.ItcClaimedPaise);
        Assert.Equal(180_000, row.ItcNotClaimedPaise);
        Assert.Equal(0, r.Gstr3b.ItcPaise);
    }

    [Fact]
    public async Task An_expense_belongs_to_the_month_of_its_date()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var prevMonth = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-1);
        await app.Get<IExpenseService>().RecordAsync(Bill(await CategoryAsync(app, "Rent"), 1_180_000, 180_000, date: prevMonth.AddDays(2)));
        Assert.Equal(180_000, (await app.Get<IGstReportService>().BuildAsync(prevMonth.Year, prevMonth.Month)).Gstr3b.ItcPaise);
        Assert.Equal(0, (await app.Get<IGstReportService>().BuildAsync(Today.Year, Today.Month)).Gstr3b.ItcPaise);
    }

    [Fact]
    public async Task The_balance_sheet_shows_the_credit_as_an_asset_and_still_balances()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<IExpenseService>().RecordAsync(Bill(await CategoryAsync(app, "Rent"), 1_180_000, 180_000));
        var bs = (await app.Get<IBalanceSheetService>().BuildAsync(Today, null)).Current;
        Assert.Equal(0, bs.DifferencePaise);
        Assert.Equal(180_000, bs.Assets.Where(a => a.Code is AccountCodes.InputCgst or AccountCodes.InputSgst or AccountCodes.InputIgst).Sum(a => a.AmountPaise));
    }

    // ------------------------------------------------------------------ upgrade

    [Fact]
    public async Task A_database_from_the_previous_release_gets_the_expense_GST_columns()
    {
        var app1 = TestApp.Create();
        await app1.SeedAsync();
        await app1.Get<IExpenseService>().RecordAsync(new RecordExpenseCommand(Guid.NewGuid(), Today, await CategoryAsync(app1, "Rent"), "Landlord", "Rent", null, 500_000, PaymentMode.Cash));
        var dir = app1.Dir;
        var factory = app1.Get<DbFactory>();
        var dbPath = app1.Paths.DbPath;
        SqliteConnection.ClearAllPools();
        using (var raw = factory.OpenRaw(dbPath))
        using (var cmd = raw.CreateCommand())
        {
            cmd.CommandText = "ALTER TABLE Expenses DROP COLUMN SupplierGstin; ALTER TABLE Expenses DROP COLUMN GstPaise; ALTER TABLE Expenses DROP COLUMN CgstPaise; "
                            + "ALTER TABLE Expenses DROP COLUMN SgstPaise; ALTER TABLE Expenses DROP COLUMN IgstPaise; DELETE FROM SchemaVersions WHERE Version = 9;";
            cmd.ExecuteNonQuery();
        }
        app1.Services.Dispose();

        using var app2 = TestApp.Create(dir);
        await app2.SignInAsAdminAsync();
        var old = (await app2.Get<IExpenseService>().ListAsync(Today, Today)).Single();
        Assert.Equal(500_000, old.AmountPaise);
        Assert.Equal(0, old.GstPaise);
        await app2.Get<IExpenseService>().RecordAsync(Bill(await CategoryAsync(app2, "Rent"), 1_180_000, 180_000));
        Assert.Equal(180_000, (await app2.Get<IGstReportService>().BuildAsync(Today.Year, Today.Month)).Gstr3b.ItcPaise);
        await Helpers.AssertBooksBalancedAsync(app2);
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
