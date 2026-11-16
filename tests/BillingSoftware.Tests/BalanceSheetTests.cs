using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Export;
using BillingSoftware.Infrastructure.Persistence;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BillingSoftware.Tests;

public class BalanceSheetTests
{
    private static DateOnly Today => Helpers.Today;

    private static Task<BalanceSheetReport> ReportAsync(TestApp app, DateOnly? asAt = null, DateOnly? compare = null) =>
        app.Get<IBalanceSheetService>().BuildAsync(asAt ?? Today, compare);

    /// <summary>A business with a bit of everything: cash and credit sales, receipts, returns, purchases on cash and credit, supplier payments, expenses (one unpaid), owner money, bank deposit and a short day-end count.</summary>
    private static async Task<TestApp> BusyBusinessAsync()
    {
        var app = TestApp.Create();
        var soap = await app.SeedAsync();                                                         // 118.00 incl. 18%
        await app.Get<IStockService>().ReceiveOpeningStockAsync(soap, 100_000, 6000);
        var dc = app.Get<IDayCloseService>();
        await dc.RecordMovementAsync(CashMovementType.OwnerPayIn, 5_000_000, "Owner added money");

        var ravi = await app.Get<IPartyService>().SaveAsync(new PartyDto(0, "Ravi Traders", null, null, "33", 0));
        await Helpers.SellAsync(app, soap, 5000, 59_000);                                          // cash
        var (credit, creditLine) = await Helpers.SellAsync(app, soap, 3000, 35_400, PaymentMode.Credit, ravi.Id);
        await app.Get<IPartyService>().ReceivePaymentAsync(ravi.Id, 10_000, PaymentMode.Cash);
        await app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(credit, creditLine, 1000, PaymentMode.Credit));   // credit note against the account

        var sup = await Helpers.AddSupplierAsync(app);
        var ps = app.Get<IPurchaseService>();
        await ps.CompleteAsync(Helpers.Buy(sup, soap, 10_000, 5000, 59_000, "S-1"));              // paid in cash
        var onCredit = await ps.CompleteAsync(Helpers.Buy(sup, soap, 10_000, 5000, 59_000, "S-2", PaymentMode.Credit));
        await app.Get<ISupplierService>().PayAsync(sup, 20_000, PaymentMode.Cash);
        var line = (await ps.GetReturnableAsync(onCredit.PurchaseId))[0].LineId;
        await ps.ReturnAsync(new PurchaseReturnCommand(Guid.NewGuid(), onCredit.PurchaseId, new[] { new ReturnLineInput(line, 2000, false) }, PaymentMode.Credit, "Damaged"));

        var ex = app.Get<IExpenseService>();
        var cats = await ex.ListCategoriesAsync();
        await ex.RecordAsync(new RecordExpenseCommand(Guid.NewGuid(), Today, cats.Single(c => c.Name == "Rent").Id, "Landlord", "Rent", null, 1_800_000, PaymentMode.Upi));
        var salary = await ex.RecordAsync(new RecordExpenseCommand(Guid.NewGuid(), Today, cats.Single(c => c.Name == "Salaries and wages").Id, "Priya", "Salary", null, 1_000_000, PaymentMode.Credit));
        await ex.PayAsync(salary.Id, 400_000, PaymentMode.Cash);

        await dc.RecordMovementAsync(CashMovementType.BankDeposit, 1_000_000, "Deposit");
        await dc.RecordMovementAsync(CashMovementType.OwnerWithdrawal, 200_000, "Owner took cash");
        var p = await dc.PreviewAsync();
        await dc.CloseAsync(new DayCloseCommand(Guid.NewGuid(), p.Report.ExpectedCashPaise - 1_000, 0, "Short"));
        return app;
    }

    [Fact]
    public async Task Assets_equal_liabilities_plus_owners_funds_and_every_line_matches_the_ledger()
    {
        using var app = await BusyBusinessAsync();
        var r = await ReportAsync(app);
        var c = r.Current;

        Assert.Equal(0, c.DifferencePaise);
        Assert.Equal(c.TotalAssetsPaise, c.TotalLiabilitiesPaise + c.TotalOwnersFundsPaise);
        Assert.Equal(c.Assets.Sum(l => l.AmountPaise), c.TotalAssetsPaise);

        var tb = (await app.Get<IReportService>().TrialBalanceAsync(Today.AddDays(1))).Rows.ToDictionary(x => x.Code);
        long Debit(string code) => tb.TryGetValue(code, out var x) ? x.DebitPaise - x.CreditPaise : 0;
        foreach (var l in c.Assets) Assert.Equal(Debit(l.Code), l.AmountPaise);
        foreach (var l in c.Liabilities) Assert.Equal(-Debit(l.Code), l.AmountPaise);
        foreach (var l in c.OwnersFunds.Where(l => l.Code != BalanceSheetServiceCodes.Previous && l.Code != BalanceSheetServiceCodes.Current)) Assert.Equal(-Debit(l.Code), l.AmountPaise);

        Assert.Contains(c.Assets, l => l.Code == AccountCodes.Cash);
        Assert.Contains(c.Liabilities, l => l.Code == AccountCodes.ExpensesPayable && l.AmountPaise == 600_000);        // salary still to pay
        Assert.Contains(c.OwnersFunds, l => l.Code == AccountCodes.OwnerDrawings && l.AmountPaise == -200_000);          // drawings reduce owner's funds
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Profit_inside_owners_funds_equals_the_profit_and_loss()
    {
        using var app = await BusyBusinessAsync();
        var r = await ReportAsync(app);
        var pnl = (await app.Get<IProfitLossService>().BuildAsync(FinancialYears.For(DateTime.Today).Start is var s ? DateOnly.FromDateTime(s) : Today, Today)).Current;
        var current = r.Current.OwnersFunds.Single(l => l.Code == BalanceSheetServiceCodes.Current).AmountPaise;
        var previous = r.Current.OwnersFunds.Single(l => l.Code == BalanceSheetServiceCodes.Previous).AmountPaise;
        Assert.Equal(pnl.NetProfitPaise, current);
        Assert.Equal(0, previous);
        Assert.NotEqual(0, current);
    }

    [Fact]
    public async Task After_the_financial_year_ends_the_profit_moves_to_earlier_years_and_the_sheet_still_balances()
    {
        using var app = await BusyBusinessAsync();
        var inThisYear = await ReportAsync(app);
        var profit = inThisYear.Current.OwnersFunds.Single(l => l.Code == BalanceSheetServiceCodes.Current).AmountPaise;

        var nextYear = DateOnly.FromDateTime(FinancialYears.For(DateTime.Today).End.AddDays(1));                         // 1 April
        var r = await ReportAsync(app, nextYear, Today);
        Assert.Equal(profit, r.Current.OwnersFunds.Single(l => l.Code == BalanceSheetServiceCodes.Previous).AmountPaise);
        Assert.Equal(0, r.Current.OwnersFunds.Single(l => l.Code == BalanceSheetServiceCodes.Current).AmountPaise);
        Assert.Equal(0, r.Current.DifferencePaise);
        Assert.Equal(inThisYear.Current.TotalOwnersFundsPaise, r.Current.TotalOwnersFundsPaise);                          // closing the year moves nothing
        Assert.Equal(inThisYear.Current.TotalAssetsPaise, r.Current.TotalAssetsPaise);
    }

    [Fact]
    public async Task The_checks_against_the_detailed_records_pass_for_a_healthy_business()
    {
        using var app = await BusyBusinessAsync();
        var r = await ReportAsync(app);
        Assert.Equal(4, r.Checks.Count);
        Assert.All(r.Checks, c => Assert.True(c.Ok, c.Name + ": " + c.Message));
        Assert.Contains(r.Checks, c => c.Name == "What customers owe you" && c.BooksPaise == c.RecordsPaise && c.BooksPaise > 0);
        Assert.Contains(r.Checks, c => c.Name == "What you owe suppliers" && c.BooksPaise == c.RecordsPaise && c.BooksPaise > 0);
        Assert.Contains(r.Checks, c => c.Name == "Expenses not paid yet" && c.BooksPaise == 600_000);
        Assert.Empty(r.Warnings.Where(w => w.Contains("does not balance")));
    }

    [Fact]
    public async Task A_difference_between_the_books_and_the_records_is_reported()
    {
        using var app = await BusyBusinessAsync();
        var factory = app.Get<DbFactory>();
        SqliteConnection.ClearAllPools();
        using (var raw = factory.OpenRaw(app.Paths.DbPath, full: true))
        using (var cmd = raw.CreateCommand())
        {
            // simulate a record changed outside the software: extra stock with no entry, and a receipt with no entry
            cmd.CommandText = """
                UPDATE Products SET QtyOnHandMilli = QtyOnHandMilli + 500000;
                INSERT INTO Receipts (PartyId, Date, Mode, AmountPaise, ReceivedByUserId) VALUES (1, '2026-01-01 10:00:00', 1, 5000, 1);
                """;
            cmd.ExecuteNonQuery();
        }
        var r = await ReportAsync(app);
        var stock = r.Checks.Single(c => c.Name == "Stock");
        Assert.False(stock.Ok);
        Assert.Contains("Look into this", stock.Message);
        Assert.False(r.Checks.Single(c => c.Name == "What customers owe you").Ok);
        Assert.True(r.Checks.Single(c => c.Name == "What you owe suppliers").Ok);                                        // untouched checks stay OK
    }

    [Fact]
    public async Task Past_dates_have_no_checks_an_empty_business_is_zero_and_the_comparison_is_one_month_earlier()
    {
        using var app = await BusyBusinessAsync();
        var yesterday = await ReportAsync(app, Today.AddDays(-1));
        Assert.Equal(0, yesterday.Current.TotalAssetsPaise);                                                              // nothing existed yet
        Assert.Equal(0, yesterday.Current.DifferencePaise);
        Assert.Empty(yesterday.Checks);
        Assert.Contains(yesterday.Warnings, w => w.Contains("only made for today"));

        var now = await ReportAsync(app);
        Assert.Equal(Today.AddMonths(-1), now.Compare.AsAt);
        Assert.Equal(0, now.Compare.TotalAssetsPaise);
        Assert.True(now.Current.TotalAssetsPaise > 0);
        var chosen = await ReportAsync(app, Today.AddDays(1), Today);
        Assert.Equal(now.Current.TotalAssetsPaise, chosen.Compare.TotalAssetsPaise);                                      // compare to today's closing
    }

    [Fact]
    public async Task Bad_dates_are_refused_and_negative_balances_are_called_out()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var bs = app.Get<IBalanceSheetService>();
        await Assert.ThrowsAsync<ValidationException>(() => bs.BuildAsync(Today.AddDays(400)));
        await Assert.ThrowsAsync<ValidationException>(() => bs.BuildAsync(new DateOnly(2010, 1, 1)));
        await Assert.ThrowsAsync<ValidationException>(() => bs.BuildAsync(Today.AddDays(-5), Today));

        var rent = (await app.Get<IExpenseService>().ListCategoriesAsync()).Single(c => c.Name == "Rent");
        await app.Get<IExpenseService>().RecordAsync(new RecordExpenseCommand(Guid.NewGuid(), Today, rent.Id, "Landlord", "Rent", null, 1_000_000, PaymentMode.Cash));
        var r = await bs.BuildAsync(Today);
        Assert.Equal(0, r.Current.DifferencePaise);
        Assert.Contains(r.Warnings, w => w.Contains("Cash in hand is below zero"));
    }

    [Fact]
    public async Task Only_the_admin_can_see_the_balance_sheet()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => ReportAsync(app));
    }

    [Fact]
    public async Task The_statement_layout_and_excel_file_agree_with_the_report()
    {
        using var app = await BusyBusinessAsync();
        var r = await ReportAsync(app);
        var rows = BalanceSheetStatement.Rows(r);
        Assert.Equal(r.Current.TotalAssetsPaise, rows.Single(x => x.Label == "Total assets").Current);
        var last = rows[^1];
        Assert.Equal(PnlRowKind.Total, last.Kind);
        Assert.Equal(r.Current.TotalAssetsPaise, last.Current);                                                           // both sides add up to the same figure
        Assert.Equal(3, rows.Count(x => x.Kind == PnlRowKind.Heading));

        var file = Path.Combine(Path.GetTempPath(), $"bs-test-{Guid.NewGuid():N}.xlsx");
        try
        {
            BalanceSheetExcelExporter.Write(r, file);
            using var wb = new XLWorkbook(file);
            Assert.True(wb.Worksheets.Contains("Balance sheet") && wb.Worksheets.Contains("Checks") && wb.Worksheets.Contains("Notes"));
            var ws = wb.Worksheet("Balance sheet");
            var total = ws.RowsUsed().First(x => x.Cell(1).GetString() == "Total assets");
            Assert.Equal(XLDataType.Number, total.Cell(2).DataType);
            Assert.Equal(r.Current.TotalAssetsPaise / 100m, total.Cell(2).GetValue<decimal>());
            var checks = wb.Worksheet("Checks");
            Assert.Equal(r.Checks.Count, checks.RowsUsed().Count(x => x.Cell(4).GetString() == "OK"));
            Assert.Equal(r.Notes.Count, wb.Worksheet("Notes").RowsUsed().Count() - 1);
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }
}

/// <summary>The two computed profit lines inside owner's funds (their codes are fixed by the service).</summary>
internal static class BalanceSheetServiceCodes
{
    public const string Previous = "9998";
    public const string Current = "9999";
}
