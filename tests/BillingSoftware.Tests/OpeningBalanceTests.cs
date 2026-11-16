using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BillingSoftware.Tests;

public class OpeningBalanceTests
{
    private static DateOnly Today => Helpers.Today;

    private static AddOpeningCommand Add(OpeningKind kind, long amount, string name = "", long? party = null, long? supplier = null,
        long? cost = null, DateOnly? date = null, Guid? id = null, string? note = null) =>
        new(id ?? Guid.NewGuid(), kind, date ?? Today.AddDays(-1), party, supplier, name, amount, cost, note);

    private static async Task<long> CategoryAsync(TestApp app, string name) =>
        (await app.Get<IExpenseService>().ListCategoriesAsync()).Single(c => c.Name == name).Id;

    private static RecordExpenseCommand Expense(long category, long amount, PaymentMode mode) =>
        new(Guid.NewGuid(), Today, category, "Someone", "Something", null, amount, mode);

    private static async Task<BsColumn> SheetAsync(TestApp app) => (await app.Get<IBalanceSheetService>().BuildAsync(Today, null)).Current;

    private static long Line(IEnumerable<BsLine> lines, string code) => lines.FirstOrDefault(l => l.Code == code)?.AmountPaise ?? 0;

    // ------------------------------------------------------------------ bank and owner's funds

    [Fact]
    public async Task A_bank_balance_stops_the_bank_showing_negative_and_the_sheet_still_balances()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ex = app.Get<IExpenseService>();
        await ex.RecordAsync(Expense(await CategoryAsync(app, "Rent"), 1_000_000, PaymentMode.Upi));
        var before = await app.Get<IBalanceSheetService>().BuildAsync(Today, null);
        Assert.Contains(before.Warnings, w => w.Contains("Bank and UPI/card is below zero"));

        await app.Get<IOpeningBalanceService>().AddAsync(Add(OpeningKind.Bank, 5_000_000, "State Bank current account"));

        var after = await app.Get<IBalanceSheetService>().BuildAsync(Today, null);
        Assert.Equal(4_000_000, Line(after.Current.Assets, AccountCodes.Bank));
        Assert.DoesNotContain(after.Warnings, w => w.Contains("Bank and UPI/card is below zero"));
        Assert.Equal(0, after.Current.DifferencePaise);
        Assert.Equal(5_000_000, Line(after.Current.OwnersFunds, AccountCodes.OpeningEquity) - 0 - (await app.Get<IOpeningBalanceService>().GetSummaryAsync()).OpeningStockPaise);
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Owners_funds_at_the_start_equal_assets_less_liabilities_recorded()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();                                   // 10 units at cost 60.00 = 600.00 of opening stock
        var op = app.Get<IOpeningBalanceService>();
        var ravi = await app.Get<IPartyService>().SaveAsync(new PartyDto(0, "Ravi Traders", null, null, "33", 0));
        var sup = await Helpers.AddSupplierAsync(app);
        await op.AddAsync(Add(OpeningKind.Cash, 100_000));
        await op.AddAsync(Add(OpeningKind.Bank, 300_000, "Bank"));
        await op.AddAsync(Add(OpeningKind.CustomerDue, 40_000, party: ravi.Id));
        await op.AddAsync(Add(OpeningKind.FixedAsset, 200_000, "Shelves", cost: 250_000));
        await op.AddAsync(Add(OpeningKind.SupplierDue, 70_000, supplier: sup));
        await op.AddAsync(Add(OpeningKind.Loan, 150_000, "Friend"));

        var s = await op.GetSummaryAsync();
        Assert.Equal(60_000, s.OpeningStockPaise);
        Assert.Equal(100_000 + 300_000 + 40_000 + 200_000 + 60_000, s.TotalAssetsPaise);
        Assert.Equal(70_000 + 150_000, s.TotalLiabilitiesPaise);
        Assert.Equal(s.NetPaise, s.OwnerFundsPaise);                        // what the books hold in Opening balance equity
        Assert.Equal(6, s.Items.Count);
        Assert.Equal(0, (await SheetAsync(app)).DifferencePaise);
        await Helpers.AssertBooksBalancedAsync(app);
        _ = soap;
    }

    // ------------------------------------------------------------------ customer and supplier dues

    [Fact]
    public async Task A_customer_due_is_part_of_what_the_customer_owes_and_can_be_paid_down()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var parties = app.Get<IPartyService>();
        var ravi = await parties.SaveAsync(new PartyDto(0, "Ravi Traders", null, null, "33", 0));
        await app.Get<IOpeningBalanceService>().AddAsync(Add(OpeningKind.CustomerDue, 50_000, party: ravi.Id));
        Assert.Equal(50_000, (await parties.SearchAsync("Ravi")).Single().OutstandingPaise);

        await parties.ReceivePaymentAsync(ravi.Id, 20_000, PaymentMode.Cash);
        Assert.Equal(30_000, (await parties.SearchAsync("Ravi")).Single().OutstandingPaise);
        await Assert.ThrowsAsync<ValidationException>(() => parties.ReceivePaymentAsync(ravi.Id, 40_000, PaymentMode.Cash));

        // a new credit sale adds to it
        await Helpers.SellAsync(app, soap, 1000, 11_800, PaymentMode.Credit, ravi.Id);
        Assert.Equal(30_000 + 11_800, (await parties.SearchAsync("Ravi")).Single().OutstandingPaise);

        // the books and the customer accounts agree
        var r = await app.Get<IBalanceSheetService>().BuildAsync(Today, null);
        Assert.All(r.Checks, c => Assert.True(c.Ok, c.Message));
        Assert.Equal(41_800, Line(r.Current.Assets, AccountCodes.Receivables));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task A_credit_note_to_the_account_can_use_an_opening_due()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var ravi = await app.Get<IPartyService>().SaveAsync(new PartyDto(0, "Ravi Traders", null, null, "33", 0));
        var (sale, line) = await Helpers.SellAsync(app, soap, 1000, 11_800, PaymentMode.Credit, ravi.Id);
        await app.Get<IOpeningBalanceService>().AddAsync(Add(OpeningKind.CustomerDue, 5_000, party: ravi.Id));
        await app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(sale, line, 1000, PaymentMode.Credit));   // 118.00 against an account owing 168.00
        Assert.Equal(5_000, (await app.Get<IPartyService>().SearchAsync("Ravi")).Single().OutstandingPaise);
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task A_supplier_due_is_part_of_what_you_owe_and_is_paid_in_parts()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var sup = await Helpers.AddSupplierAsync(app);
        var suppliers = app.Get<ISupplierService>();
        await app.Get<IOpeningBalanceService>().AddAsync(Add(OpeningKind.SupplierDue, 90_000, supplier: sup));
        Assert.Equal(90_000, (await suppliers.SearchAsync("Metro")).Single().OutstandingPaise);

        await suppliers.PayAsync(sup, 25_000, PaymentMode.Upi);
        Assert.Equal(65_000, (await suppliers.SearchAsync("Metro")).Single().OutstandingPaise);
        await Assert.ThrowsAsync<ValidationException>(() => suppliers.PayAsync(sup, 70_000, PaymentMode.Cash));

        await app.Get<IPurchaseService>().CompleteAsync(Helpers.Buy(sup, soap, 10_000, 5000, 59_000, "S-9", PaymentMode.Credit));
        Assert.Equal(65_000 + 59_000, (await suppliers.SearchAsync("Metro")).Single().OutstandingPaise);

        var r = await app.Get<IBalanceSheetService>().BuildAsync(Today, null);
        Assert.All(r.Checks, c => Assert.True(c.Ok, c.Message));
        Assert.Equal(124_000, Line(r.Current.Liabilities, AccountCodes.Payables));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Cancelling_a_due_is_refused_once_it_has_been_paid_against_and_allowed_before()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var op = app.Get<IOpeningBalanceService>();
        var parties = app.Get<IPartyService>();
        var ravi = await parties.SaveAsync(new PartyDto(0, "Ravi Traders", null, null, "33", 0));
        var asha = await parties.SaveAsync(new PartyDto(0, "Asha Stores", null, null, "33", 0));
        var raviDue = await op.AddAsync(Add(OpeningKind.CustomerDue, 50_000, party: ravi.Id));
        var ashaDue = await op.AddAsync(Add(OpeningKind.CustomerDue, 10_000, party: asha.Id));
        await parties.ReceivePaymentAsync(ravi.Id, 20_000, PaymentMode.Cash);

        await Assert.ThrowsAsync<BusinessRuleException>(() => op.VoidAsync(raviDue, "Wrong customer"));
        await op.VoidAsync(ashaDue, "Wrong customer");
        Assert.Equal(0, (await parties.SearchAsync("Asha")).Single().OutstandingPaise);
        Assert.Equal(30_000, (await parties.SearchAsync("Ravi")).Single().OutstandingPaise);
        await Assert.ThrowsAsync<ValidationException>(() => op.VoidAsync(ashaDue, "Again"));          // already cancelled

        var sup = await Helpers.AddSupplierAsync(app);
        var supDue = await op.AddAsync(Add(OpeningKind.SupplierDue, 40_000, supplier: sup));
        await app.Get<ISupplierService>().PayAsync(sup, 10_000, PaymentMode.Cash);
        await Assert.ThrowsAsync<BusinessRuleException>(() => op.VoidAsync(supDue, "Mistake"));

        var r = await app.Get<IBalanceSheetService>().BuildAsync(Today, null);
        Assert.All(r.Checks, c => Assert.True(c.Ok, c.Message));
        Assert.Equal(0, r.Current.DifferencePaise);
        await Helpers.AssertBooksBalancedAsync(app);
    }

    // ------------------------------------------------------------------ loans

    [Fact]
    public async Task A_loan_is_a_liability_that_cash_repayments_reduce_and_day_end_follows()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var op = app.Get<IOpeningBalanceService>();
        var dc = app.Get<IDayCloseService>();
        await op.AddAsync(Add(OpeningKind.Cash, 1_000_000));
        await op.AddAsync(Add(OpeningKind.Loan, 400_000, "Cooperative bank"));
        Assert.Equal(400_000, Line((await SheetAsync(app)).Liabilities, AccountCodes.Loans));

        await Assert.ThrowsAsync<ValidationException>(() => dc.RecordMovementAsync(CashMovementType.LoanRepaid, 500_000, "Too much"));
        await dc.RecordMovementAsync(CashMovementType.LoanRepaid, 100_000, "Instalment");
        await dc.RecordMovementAsync(CashMovementType.LoanReceived, 50_000, "Top-up");
        Assert.Equal(350_000, Line((await SheetAsync(app)).Liabilities, AccountCodes.Loans));

        // expected cash = opening cash 10,000.00 - 1,000.00 + 500.00
        var p = await dc.PreviewAsync();
        Assert.Equal(1_000_000 - 100_000 + 50_000, p.Report.ExpectedCashPaise);
        Assert.Contains(p.Report.CashLines, l => l.Label == "Loan repaid in cash" && l.AmountPaise == -100_000);
        Assert.Contains(p.Report.CashLines, l => l.Label == "Loan received in cash" && l.AmountPaise == 50_000);
        Assert.Equal(0, (await app.Get<IReportService>().TrialBalanceAsync(Today)).Rows.Where(r => r.Code == AccountCodes.Cash).Sum(r => r.DebitPaise - r.CreditPaise) - p.Report.ExpectedCashPaise);

        // part of it was repaid, so the original entry can no longer be cancelled
        var loan = (await op.GetSummaryAsync()).Items.Single(i => i.Kind == OpeningKind.Loan);
        await Assert.ThrowsAsync<BusinessRuleException>(() => op.VoidAsync(loan.Id, "Mistake"));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task With_no_loan_recorded_there_is_nothing_to_repay()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ex = await Assert.ThrowsAsync<ValidationException>(() => app.Get<IDayCloseService>().RecordMovementAsync(CashMovementType.LoanRepaid, 1_000, "Oops"));
        Assert.Contains("No loan", ex.Message);
    }

    [Fact]
    public async Task An_unused_loan_entry_can_be_cancelled_and_the_liability_goes_away()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var op = app.Get<IOpeningBalanceService>();
        var id = await op.AddAsync(Add(OpeningKind.Loan, 400_000, "Friend"));
        await op.VoidAsync(id, "Entered twice");
        var s = await op.GetSummaryAsync();
        Assert.Equal(0, s.LoansPaise);
        Assert.True(s.Items.Single().IsVoided);
        Assert.Equal(0, Line((await SheetAsync(app)).Liabilities, AccountCodes.Loans));
        Assert.Equal(0, s.OwnerFundsPaise - s.OpeningStockPaise);
        await Helpers.AssertBooksBalancedAsync(app);
    }

    // ------------------------------------------------------------------ fixed assets and depreciation

    [Fact]
    public async Task A_fixed_asset_is_carried_at_its_value_and_depreciation_reduces_it_and_profit()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var op = app.Get<IOpeningBalanceService>();
        var ex = app.Get<IExpenseService>();
        await op.AddAsync(Add(OpeningKind.FixedAsset, 800_000, "Shelves and counter", cost: 1_000_000));
        Assert.Equal(800_000, Line((await SheetAsync(app)).Assets, AccountCodes.FixedAssets));

        var dep = await CategoryAsync(app, "Depreciation");
        var before = await app.Get<IProfitLossService>().BuildAsync(Today.AddDays(-30), Today);
        await ex.RecordAsync(Expense(dep, 100_000, PaymentMode.NonCash));
        Assert.Equal(700_000, Line((await SheetAsync(app)).Assets, AccountCodes.FixedAssets));
        var after = await app.Get<IProfitLossService>().BuildAsync(Today.AddDays(-30), Today);
        Assert.Equal(before.Current.NetProfitPaise - 100_000, after.Current.NetProfitPaise);
        Assert.Equal(0, (await SheetAsync(app)).DifferencePaise);

        // no money moved: the day-end cash is unchanged
        var p = await app.Get<IDayCloseService>().PreviewAsync();
        Assert.Equal(0, p.Report.ExpectedCashPaise);
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Depreciation_and_no_money_expenses_follow_their_rules()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var ex = app.Get<IExpenseService>();
        var dep = await CategoryAsync(app, "Depreciation");
        var rent = await CategoryAsync(app, "Rent");

        // nothing to depreciate yet
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Expense(dep, 1_000, PaymentMode.NonCash)));
        await app.Get<IOpeningBalanceService>().AddAsync(Add(OpeningKind.FixedAsset, 50_000, "Fridge"));
        // more than the value left
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Expense(dep, 60_000, PaymentMode.NonCash)));
        // depreciation must use "no money", and "no money" is only for depreciation
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Expense(dep, 1_000, PaymentMode.Cash)));
        await Assert.ThrowsAsync<ValidationException>(() => ex.RecordAsync(Expense(rent, 1_000, PaymentMode.NonCash)));
        // an unpaid expense cannot be "paid" with no money
        var unpaid = await ex.RecordAsync(Expense(rent, 5_000, PaymentMode.Credit));
        await Assert.ThrowsAsync<ValidationException>(() => ex.PayAsync(unpaid.Id, 1_000, PaymentMode.NonCash));

        // cancelling a depreciation entry puts the value back
        var d = await ex.RecordAsync(Expense(dep, 20_000, PaymentMode.NonCash));
        await ex.VoidAsync(d.Id, "Wrong month");
        Assert.Equal(50_000, Line((await SheetAsync(app)).Assets, AccountCodes.FixedAssets));

        // the asset cannot be cancelled once depreciation has been charged
        var asset = (await app.Get<IOpeningBalanceService>().GetSummaryAsync()).Items.Single(i => i.Kind == OpeningKind.FixedAsset);
        await ex.RecordAsync(Expense(dep, 10_000, PaymentMode.NonCash));
        await Assert.ThrowsAsync<BusinessRuleException>(() => app.Get<IOpeningBalanceService>().VoidAsync(asset.Id, "Mistake"));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task The_GST_warning_about_expenses_ignores_depreciation()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<IOpeningBalanceService>().AddAsync(Add(OpeningKind.FixedAsset, 50_000, "Fridge"));
        await app.Get<IExpenseService>().RecordAsync(Expense(await CategoryAsync(app, "Depreciation"), 10_000, PaymentMode.NonCash));
        var r = await app.Get<IGstReportService>().BuildAsync(Today.Year, Today.Month);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("business expense"));
    }

    // ------------------------------------------------------------------ payment mode safety

    [Fact]
    public async Task No_money_is_refused_wherever_a_real_payment_is_taken_or_made()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var parties = app.Get<IPartyService>();
        var ravi = await parties.SaveAsync(new PartyDto(0, "Ravi Traders", null, null, "33", 0));
        var sup = await Helpers.AddSupplierAsync(app);
        await app.Get<IOpeningBalanceService>().AddAsync(Add(OpeningKind.CustomerDue, 50_000, party: ravi.Id));
        await app.Get<IOpeningBalanceService>().AddAsync(Add(OpeningKind.SupplierDue, 50_000, supplier: sup));

        await Assert.ThrowsAsync<ValidationException>(() => parties.ReceivePaymentAsync(ravi.Id, 1_000, PaymentMode.NonCash));
        await Assert.ThrowsAsync<ValidationException>(() => app.Get<ISupplierService>().PayAsync(sup, 1_000, PaymentMode.NonCash));
        await Assert.ThrowsAsync<ValidationException>(() => app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(soap, 1000, 11_800, PaymentMode.NonCash)));
        await Assert.ThrowsAsync<ValidationException>(() => app.Get<IPurchaseService>().CompleteAsync(Helpers.Buy(sup, soap, 1000, 5000, 5_900, "N-1", PaymentMode.NonCash)));
        await Assert.ThrowsAsync<ValidationException>(() => app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(soap, 1000, 11_800, (PaymentMode)99)));

        var (sale, line) = await Helpers.SellAsync(app, soap, 2000, 23_600);
        await Assert.ThrowsAsync<ValidationException>(() => app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(sale, line, 1000, PaymentMode.NonCash)));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    // ------------------------------------------------------------------ cash and the first day end

    [Fact]
    public async Task Recorded_opening_cash_is_the_first_closes_starting_cash_and_the_typed_float_is_ignored()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var dc = app.Get<IDayCloseService>();
        await app.Get<IOpeningBalanceService>().AddAsync(Add(OpeningKind.Cash, 200_000));

        var p = await dc.PreviewAsync(999_999);                              // a typed float must not be added on top
        Assert.True(p.OpeningCashRecorded);
        Assert.Equal(200_000, p.Report.OpeningCashPaise);
        Assert.Equal(200_000, p.Report.ExpectedCashPaise);

        var r = await dc.CloseAsync(new DayCloseCommand(Guid.NewGuid(), 200_000, 999_999, null));
        Assert.Equal(0, r.VariancePaise);
        var cash = (await app.Get<IReportService>().TrialBalanceAsync(Today)).Rows.Where(x => x.Code == AccountCodes.Cash).Sum(x => x.DebitPaise - x.CreditPaise);
        Assert.Equal(200_000, cash);                                          // counted once, not twice
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Without_recorded_cash_the_first_close_still_takes_the_typed_float()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var p = await app.Get<IDayCloseService>().PreviewAsync(150_000);
        Assert.False(p.OpeningCashRecorded);
        Assert.Equal(150_000, p.Report.OpeningCashPaise);
    }

    [Fact]
    public async Task Cash_cannot_be_added_or_cancelled_once_the_drawer_has_been_counted()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var op = app.Get<IOpeningBalanceService>();
        var cash = await op.AddAsync(Add(OpeningKind.Cash, 200_000));
        await app.Get<IDayCloseService>().CloseAsync(new DayCloseCommand(Guid.NewGuid(), 200_000));
        await Assert.ThrowsAsync<BusinessRuleException>(() => op.AddAsync(Add(OpeningKind.Cash, 1_000)));
        await Assert.ThrowsAsync<BusinessRuleException>(() => op.VoidAsync(cash, "Mistake"));
        Assert.True((await op.GetSummaryAsync()).DayClosed);
    }

    [Fact]
    public async Task Cancelling_cash_before_any_close_takes_it_out_of_the_drawer()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var op = app.Get<IOpeningBalanceService>();
        var id = await op.AddAsync(Add(OpeningKind.Cash, 200_000));
        await op.VoidAsync(id, "Counted wrongly");
        var p = await app.Get<IDayCloseService>().PreviewAsync();
        Assert.False(p.OpeningCashRecorded);
        Assert.Equal(0, p.Report.ExpectedCashPaise);
        await Helpers.AssertBooksBalancedAsync(app);
    }

    // ------------------------------------------------------------------ dates

    [Fact]
    public async Task The_balance_is_shown_from_its_as_at_date_and_not_before()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<IOpeningBalanceService>().AddAsync(Add(OpeningKind.Bank, 1_000_000, "Bank", date: Today.AddDays(-20)));
        var bs = app.Get<IBalanceSheetService>();
        Assert.Equal(0, Line((await bs.BuildAsync(Today.AddDays(-25), null)).Current.Assets, AccountCodes.Bank));
        Assert.Equal(1_000_000, Line((await bs.BuildAsync(Today.AddDays(-20), null)).Current.Assets, AccountCodes.Bank));
        Assert.Equal(1_000_000, Line((await bs.BuildAsync(Today, null)).Current.Assets, AccountCodes.Bank));
    }

    // ------------------------------------------------------------------ validation, permissions, idempotency

    [Fact]
    public async Task Bad_entries_are_refused_with_plain_messages()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var op = app.Get<IOpeningBalanceService>();
        await Assert.ThrowsAsync<ValidationException>(() => op.AddAsync(Add(OpeningKind.Bank, 0, "Bank")));
        await Assert.ThrowsAsync<ValidationException>(() => op.AddAsync(Add(OpeningKind.Bank, -5, "Bank")));
        await Assert.ThrowsAsync<ValidationException>(() => op.AddAsync(Add(OpeningKind.Bank, 1_000, "")));
        await Assert.ThrowsAsync<ValidationException>(() => op.AddAsync(Add(OpeningKind.Bank, 100_000_000_01, "Bank")));
        await Assert.ThrowsAsync<ValidationException>(() => op.AddAsync(Add(OpeningKind.Bank, 1_000, "Bank", date: Today.AddDays(1))));
        await Assert.ThrowsAsync<ValidationException>(() => op.AddAsync(Add(OpeningKind.Bank, 1_000, "Bank", date: Today.AddYears(-20))));
        await Assert.ThrowsAsync<ValidationException>(() => op.AddAsync(Add((OpeningKind)42, 1_000, "Bank")));
        await Assert.ThrowsAsync<ValidationException>(() => op.AddAsync(Add(OpeningKind.CustomerDue, 1_000)));                    // no customer
        await Assert.ThrowsAsync<ValidationException>(() => op.AddAsync(Add(OpeningKind.CustomerDue, 1_000, party: 9999)));
        await Assert.ThrowsAsync<ValidationException>(() => op.AddAsync(Add(OpeningKind.SupplierDue, 1_000)));                    // no supplier
        await Assert.ThrowsAsync<ValidationException>(() => op.AddAsync(Add(OpeningKind.FixedAsset, 100_000, "Shelves", cost: 50_000)));   // cost below value
        await Assert.ThrowsAsync<ValidationException>(() => op.VoidAsync(12345, "No such entry"));
        await Assert.ThrowsAsync<ValidationException>(() => op.AddAsync(Add(OpeningKind.Bank, 1_000, "Bank", note: new string('x', 201))));
        Assert.Empty((await op.GetSummaryAsync()).Items);
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task A_cashier_cannot_use_opening_balances()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.AddCashierAsync();
        var op = app.Get<IOpeningBalanceService>();
        var id = await op.AddAsync(Add(OpeningKind.Bank, 1_000, "Bank"));
        await app.SignInAsCashierAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => op.GetSummaryAsync());
        await Assert.ThrowsAsync<PermissionDeniedException>(() => op.AddAsync(Add(OpeningKind.Bank, 1_000, "Bank")));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => op.VoidAsync(id, "Nope"));
    }

    [Fact]
    public async Task Saving_the_same_entry_twice_records_it_once()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var op = app.Get<IOpeningBalanceService>();
        var cmd = Add(OpeningKind.Bank, 1_000_000, "Bank");
        var a = await op.AddAsync(cmd);
        var b = await op.AddAsync(cmd);
        Assert.Equal(a, b);
        var s = await op.GetSummaryAsync();
        Assert.Single(s.Items);
        Assert.Equal(1_000_000, s.BankPaise);
        Assert.Equal(1_000_000, Line((await SheetAsync(app)).Assets, AccountCodes.Bank));
    }

    [Fact]
    public async Task A_locked_period_refuses_a_back_dated_entry()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<ISettingsService>().SetAsync(SettingKeys.LockedThrough, Today.AddDays(-5).ToString("yyyy-MM-dd"));
        var op = app.Get<IOpeningBalanceService>();
        await Assert.ThrowsAsync<BusinessRuleException>(() => op.AddAsync(Add(OpeningKind.Bank, 1_000, "Bank", date: Today.AddDays(-10))));
        await op.AddAsync(Add(OpeningKind.Bank, 1_000, "Bank", date: Today.AddDays(-2)));
    }

    [Fact]
    public async Task Every_entry_is_in_the_audit_log()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var op = app.Get<IOpeningBalanceService>();
        var id = await op.AddAsync(Add(OpeningKind.Loan, 1_000, "Friend"));
        await op.VoidAsync(id, "Entered twice");
        await using (var db = app.Get<DbFactory>().Create())
        {
            var actions = db.AuditEvents.Select(e => e.Action).ToList();
            Assert.Contains("opening.added", actions);
            Assert.Contains("opening.voided", actions);
        }
        Assert.True((await app.Get<IAuditService>().VerifyChainAsync()).Valid);
    }

    // ------------------------------------------------------------------ upgrade

    [Fact]
    public async Task A_database_from_the_previous_release_gets_the_opening_balance_table_and_accounts()
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
                DROP TABLE OpeningBalances;
                DELETE FROM Accounts WHERE Code IN ('1500', '2300');
                DELETE FROM ExpenseCategories WHERE AccountCode = '6180';
                DELETE FROM Accounts WHERE Code = '6180';
                DELETE FROM SchemaVersions WHERE Version = 6;
                """;
            cmd.ExecuteNonQuery();
        }
        app1.Services.Dispose();

        using var app2 = TestApp.Create(dir);
        await app2.SignInAsAdminAsync();
        var op = app2.Get<IOpeningBalanceService>();
        await op.AddAsync(Add(OpeningKind.FixedAsset, 100_000, "Counter"));
        await op.AddAsync(Add(OpeningKind.Loan, 50_000, "Friend"));
        await app2.Get<IExpenseService>().RecordAsync(Expense(await CategoryAsync(app2, "Depreciation"), 10_000, PaymentMode.NonCash));
        await Helpers.AssertBooksBalancedAsync(app2);
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
