using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Export;
using ClosedXML.Excel;
using Xunit;

namespace BillingSoftware.Tests;

public class ProfitLossTests
{
    private static DateOnly Today => Helpers.Today;

    private static Task<ProfitLossReport> ReportAsync(TestApp app, DateOnly? from = null, DateOnly? to = null) =>
        app.Get<IProfitLossService>().BuildAsync(from ?? Today.AddDays(-1), to ?? Today.AddDays(1));

    private static async Task<long> ProductAsync(TestApp app, string sku, string name, int rateBp, long price, long cost)
    {
        var p = await app.Get<ICatalogService>().SaveAsync(new ProductDto(0, sku, null, name, "9999", rateBp, "pcs", price, price, true, 0, 0, true));
        await app.Get<IStockService>().ReceiveOpeningStockAsync(p.Id, 100_000, cost);
        return p.Id;
    }

    private static long Total(long price, long qtyMilli) =>
        InvoiceRounding.ToNearestRupee(Money.RoundHalfUp((decimal)price * qtyMilli / 1000m)).Total;

    /// <summary>A busy day: sales, a voided bill, a restocked return, a damaged return, a loss-making item, round-off, stock loss, petty cash and a short cash count.</summary>
    private static async Task<(TestApp App, long Soap, long Loss)> BusyDayAsync()
    {
        var app = TestApp.Create();
        var soap = await app.SeedAsync();                                                       // 118.00 inclusive of 18%
        await app.Get<IStockService>().ReceiveOpeningStockAsync(soap, 100_000, 6000);
        var loss = await ProductAsync(app, "LOSS", "Loss leader", 1800, 5000, 7000);            // sold for 50.00, cost 70.00
        var odd = await ProductAsync(app, "ODD", "Odd price", 1800, 10049, 5000);               // round-off on the bill

        var sales = app.Get<ISalesService>();
        var (s1, l1) = await Helpers.SellAsync(app, soap, 5000, 59_000);                        // 5 soaps
        await sales.CompleteSaleAsync(TestApp.Sale(loss, 3000, Total(5000, 3000)));
        await sales.CompleteSaleAsync(TestApp.Sale(odd, 1000, Total(10049, 1000)));
        var (voidMe, _) = await Helpers.SellAsync(app, soap, 1000, 11_800);
        await sales.VoidSaleAsync(voidMe, "mistake", null);
        await app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(s1, l1, 1000));                       // restocked
        await app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(s1, l1, 1000, restock: false, reason: "Broken"));   // damaged
        await app.Get<IStockService>().AdjustAsync(soap, -1000, "Damaged in store");           // stock loss
        var dc = app.Get<IDayCloseService>();
        await dc.RecordMovementAsync(CashMovementType.Expense, 12_000, "Tea and courier");
        var p = await dc.PreviewAsync();
        await dc.CloseAsync(new DayCloseCommand(Guid.NewGuid(), p.Report.ExpectedCashPaise - 5000, 0, "Short"));
        return (app, soap, loss);
    }

    private static async Task<long> SumAsync(TestApp app, string prefix, bool credit)
    {
        var tb = await app.Get<IReportService>().TrialBalanceAsync(Today.AddDays(1));
        // Expenses are accounts 5xxx (cost of goods, losses, round-off ...) and 6xxx (rent, salaries ...): asking for "5" means both.
        bool Match(string code) => prefix == "5" ? code.StartsWith("5") || code.StartsWith("6") : code.StartsWith(prefix);
        return tb.Rows.Where(r => Match(r.Code)).Sum(r => credit ? r.CreditPaise - r.DebitPaise : r.DebitPaise - r.CreditPaise);
    }

    [Fact]
    public async Task Profit_equals_income_less_expense_in_the_books_and_satisfies_the_accounting_equation()
    {
        var (app, _, _) = await BusyDayAsync();
        using var _ = app;
        var r = await ReportAsync(app);
        var c = r.Current;

        var income = await SumAsync(app, "4", credit: true);
        var expense = await SumAsync(app, "5", credit: false);
        Assert.Equal(income - expense, c.NetProfitPaise);

        // assets = liabilities + capital + profit
        var assets = await SumAsync(app, "1", credit: false);
        var liabilities = await SumAsync(app, "2", credit: true);
        var equity = await SumAsync(app, "3", credit: true);
        Assert.Equal(assets - liabilities - equity, c.NetProfitPaise);
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Each_line_of_the_statement_matches_its_account_and_the_totals_add_up()
    {
        var (app, _, _) = await BusyDayAsync();
        using var _ = app;
        var c = (await ReportAsync(app)).Current;

        Assert.Equal(-await Helpers.NetAsync(app, AccountCodes.Sales), c.NetSalesPaise);
        Assert.Equal(await Helpers.NetAsync(app, AccountCodes.Cogs), c.CogsPaise);
        Assert.Equal(c.GrossSalesPaise - c.VoidedPaise - c.ReturnsPaise, c.NetSalesPaise);
        Assert.Equal(c.NetSalesPaise - c.CogsPaise, c.GrossProfitPaise);
        Assert.Equal(c.GrossProfitPaise - c.TotalExpensesPaise, c.NetProfitPaise);
        Assert.Equal(c.Expenses.Sum(e => e.AmountPaise), c.TotalExpensesPaise);

        Assert.Equal(12_000, c.Expenses.Single(e => e.Code == AccountCodes.ShopExpenses).AmountPaise);
        Assert.Equal(5_000, c.Expenses.Single(e => e.Code == AccountCodes.CashOverShort).AmountPaise);         // cash short = expense
        Assert.Equal(6_000, c.Expenses.Single(e => e.Code == AccountCodes.InventoryLoss).AmountPaise);          // one soap lost at cost 60.00
        Assert.Equal(await Helpers.NetAsync(app, AccountCodes.RoundOff), c.Expenses.Single(e => e.Code == AccountCodes.RoundOff).AmountPaise);
        Assert.DoesNotContain(c.Expenses, e => e.AmountPaise == 0);                                             // empty lines are left out

        Assert.True(c.GrossMarginBp > 0 && c.GrossMarginBp < 10000);
        Assert.Equal(c.GrossProfitPaise * 10000 / c.NetSalesPaise, c.GrossMarginBp);
    }

    [Fact]
    public async Task Voids_and_returns_reduce_sales_and_a_damaged_return_keeps_its_cost()
    {
        var (app, _, _) = await BusyDayAsync();
        using var _ = app;
        var c = (await ReportAsync(app)).Current;
        Assert.Equal(10_000, c.VoidedPaise);                                                                    // taxable value of the voided bill (100.00)
        Assert.Equal(2 * 10_000, c.ReturnsPaise);                                                               // two returned soaps (100.00 taxable each)
        // cost of goods sold: sold 5 + 1 (void reversed) + loss 3 + odd 1; the restocked return gives back one soap's cost, the damaged one does not
        var soapCost = 6_000; var lossCost = 7_000; var oddCost = 5_000;
        Assert.Equal(5 * soapCost - 1 * soapCost + 3 * lossCost + 1 * oddCost, c.CogsPaise);
    }

    [Fact]
    public async Task Profit_by_item_adds_up_to_gross_profit_and_shows_a_loss_making_item()
    {
        var (app, soap, loss) = await BusyDayAsync();
        using var _ = app;
        var r = await ReportAsync(app);
        Assert.Equal(r.Current.GrossProfitPaise, r.Items.Sum(i => i.ProfitPaise));                             // ties to the statement exactly
        Assert.Equal(r.Current.NetSalesPaise, r.Items.Sum(i => i.SalesPaise));
        Assert.Equal(r.Current.CogsPaise, r.Items.Sum(i => i.CostPaise));

        var leader = r.Items.Single(i => i.ProductId == loss);
        Assert.True(leader.ProfitPaise < 0 && leader.MarginBp < 0, "selling at 50 with cost 70 must show a loss");
        Assert.Equal(3_000, leader.QtyMilli);
        var soapRow = r.Items.Single(i => i.ProductId == soap);
        Assert.Equal(5_000 - 2_000, soapRow.QtyMilli);                                                          // sold 5 and 2 returned; the voided bill nets to zero
        Assert.Equal(r.Items.OrderByDescending(i => i.ProfitPaise).First().ProductId, r.Items[0].ProductId);    // best first
    }

    [Fact]
    public async Task The_previous_period_is_the_same_length_just_before()
    {
        var (app, _, _) = await BusyDayAsync();
        using var _ = app;
        var today = (await ReportAsync(app, Today, Today)).Current;
        var tomorrow = await ReportAsync(app, Today.AddDays(1), Today.AddDays(1));
        Assert.Equal(0, tomorrow.Current.NetSalesPaise);
        Assert.Equal(0, tomorrow.Current.NetProfitPaise);
        Assert.Equal(today.NetProfitPaise, tomorrow.Previous.NetProfitPaise);                                    // yesterday's column for tomorrow is today
        Assert.Equal(today.NetSalesPaise, tomorrow.Previous.NetSalesPaise);
        Assert.Equal(Today, tomorrow.Previous.From);
        Assert.Equal(Today, tomorrow.Previous.To);

        var week = await ReportAsync(app, Today.AddDays(-6), Today);
        Assert.Equal(Today.AddDays(-13), week.Previous.From);
        Assert.Equal(Today.AddDays(-7), week.Previous.To);
        Assert.Equal(0, week.Previous.NetSalesPaise);
        Assert.Equal(today.NetProfitPaise, week.Current.NetProfitPaise);
    }

    [Fact]
    public async Task The_excel_file_has_the_statement_items_and_notes_with_numbers_matching_the_report()
    {
        var (app, _, loss) = await BusyDayAsync();
        using var _ = app;
        var r = await ReportAsync(app);
        var file = Path.Combine(Path.GetTempPath(), $"pnl-test-{Guid.NewGuid():N}.xlsx");
        try
        {
            PnlExcelExporter.Write(r, file);
            using var wb = new XLWorkbook(file);
            Assert.True(wb.Worksheets.Contains("Profit and loss") && wb.Worksheets.Contains("Profit by item") && wb.Worksheets.Contains("Notes"));

            var ws = wb.Worksheet("Profit and loss");
            var net = ws.RowsUsed().First(x => x.Cell(1).GetString() == "NET PROFIT");
            Assert.Equal(XLDataType.Number, net.Cell(2).DataType);
            Assert.Equal(r.Current.NetProfitPaise / 100m, net.Cell(2).GetValue<decimal>());
            Assert.Equal(r.Previous.NetProfitPaise / 100m, net.Cell(3).GetValue<decimal>());
            Assert.Equal((r.Current.NetProfitPaise - r.Previous.NetProfitPaise) / 100m, net.Cell(4).GetValue<decimal>());
            var gm = ws.RowsUsed().First(x => x.Cell(1).GetString() == "Gross margin");
            Assert.Equal(r.Current.GrossMarginBp / 10000.0, gm.Cell(2).GetValue<double>(), 6);

            var items = wb.Worksheet("Profit by item");
            Assert.Equal(r.Items.Count, items.RowsUsed().Count() - 1);
            Assert.Equal(r.Items.Sum(i => i.ProfitPaise) / 100m, items.Range(2, 5, 1 + r.Items.Count, 5).CellsUsed().Sum(c => c.GetValue<decimal>()));
            Assert.True(items.Range(2, 5, 1 + r.Items.Count, 5).CellsUsed().Any(c => c.GetValue<decimal>() < 0), "the loss-making item must show a negative profit");
            Assert.Equal(r.Notes.Count, wb.Worksheet("Notes").RowsUsed().Count() - 1);
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    [Fact]
    public async Task The_statement_layout_lists_every_expense_line_from_either_period()
    {
        var (app, _, _) = await BusyDayAsync();
        using var _ = app;
        var tomorrow = await ReportAsync(app, Today.AddDays(1), Today.AddDays(1));          // this period empty, previous period has everything
        var rows = PnlStatement.Rows(tomorrow);
        Assert.Contains(rows, x => x.Label.StartsWith("Shop expenses") && x.Current == 0 && x.Previous == 12_000);
        Assert.Equal(PnlRowKind.Total, rows.Single(x => x.Label == "NET PROFIT").Kind);
        Assert.Equal(tomorrow.Previous.NetProfitPaise, rows.Single(x => x.Label == "NET PROFIT").Previous);
        Assert.Equal(rows.Count(x => x.Kind == PnlRowKind.Heading), 2);
    }

    [Fact]
    public async Task A_new_business_shows_zero_everywhere_and_bad_requests_are_refused()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var r = await ReportAsync(app);
        Assert.Equal(0, r.Current.NetProfitPaise);
        Assert.Equal(0, r.Current.NetSalesPaise);
        Assert.Equal(0, r.Current.GrossMarginBp);                                                                // no divide-by-zero
        Assert.Empty(r.Items);
        Assert.NotEmpty(r.Notes);
        await Assert.ThrowsAsync<ValidationException>(() => ReportAsync(app, Today, Today.AddDays(-1)));
        await Assert.ThrowsAsync<ValidationException>(() => ReportAsync(app, Today.AddYears(-5), Today));
    }

    [Fact]
    public async Task Only_the_admin_can_see_profit_and_loss()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => ReportAsync(app));
    }

    [Fact]
    public async Task Cash_over_is_income_like_and_stock_gain_reduces_expense()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 1000, 11_800));
        var dc = app.Get<IDayCloseService>();
        await dc.CloseAsync(new DayCloseCommand(Guid.NewGuid(), 11_800 + 2_000, 0, "Tip"));                      // cash over by 20.00
        await app.Get<IStockService>().AdjustAsync(pid, 1000, "Found one");                                      // stock gain at cost 60.00
        var c = (await ReportAsync(app)).Current;
        Assert.Equal(-2_000, c.Expenses.Single(e => e.Code == AccountCodes.CashOverShort).AmountPaise);          // negative expense = gain
        Assert.Equal(-6_000, c.Expenses.Single(e => e.Code == AccountCodes.InventoryLoss).AmountPaise);
        Assert.Equal(c.GrossProfitPaise - c.TotalExpensesPaise, c.NetProfitPaise);
        Assert.Equal(c.NetProfitPaise, await SumAsync(app, "4", true) - await SumAsync(app, "5", false));
    }
}
