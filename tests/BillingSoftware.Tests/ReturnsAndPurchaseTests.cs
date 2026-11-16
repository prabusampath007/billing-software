using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BillingSoftware.Tests;

internal static class Helpers
{
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    public static async Task<long> NetAsync(TestApp app, string code)
    {
        var tb = await app.Get<IReportService>().TrialBalanceAsync(Today);
        return tb.Rows.Where(r => r.Code == code).Sum(r => r.DebitPaise - r.CreditPaise);
    }

    /// <summary>Invoice number to place of supply, from the GST sales register (used to check what each bill recorded).</summary>
    public static async Task<Dictionary<string, string>> RegisterAsync(TestApp app)
    {
        var r = await app.Get<IGstReportService>().BuildAsync(DateTime.Today.Year, DateTime.Today.Month);
        return r.SalesRegister.ToDictionary(x => x.DocNo, x => x.PlaceOfSupply);
    }

    public static async Task AssertBooksBalancedAsync(TestApp app) =>
        Assert.True((await app.Get<IReportService>().TrialBalanceAsync(Today)).IsBalanced, "trial balance must balance");

    public static async Task<(long SaleId, long LineId)> SellAsync(TestApp app, long productId, long qtyMilli, long total,
        PaymentMode mode = PaymentMode.Cash, long? partyId = null, string? buyerState = null)
    {
        var r = await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(productId, qtyMilli, total, mode, partyId: partyId, buyerState: buyerState));
        var lines = await app.Get<ISalesReturnService>().GetReturnableAsync(r.SaleId);
        return (r.SaleId, lines[0].LineId);
    }

    public static SaleReturnCommand Ret(long saleId, long lineId, long qtyMilli, PaymentMode mode = PaymentMode.Cash, bool restock = true,
        string reason = "Customer changed mind", Guid? id = null, long? approvedBy = null) =>
        new(id ?? Guid.NewGuid(), saleId, new[] { new ReturnLineInput(lineId, qtyMilli, restock) }, mode, reason, approvedBy);

    public static async Task<long> AddSupplierAsync(TestApp app, string name = "Metro Wholesale", string? gstin = "33AAAAA0000A1Z5", string? state = "33")
    {
        var s = await app.Get<ISupplierService>().SaveAsync(new SupplierDto(0, name, "9000000002", gstin, state, 0));
        return s.Id;
    }

    public static CompletePurchaseCommand Buy(long supplierId, long productId, long qtyMilli, long unitCostPaise, long total, string invoiceNo = "INV-001",
        PaymentMode mode = PaymentMode.Cash, bool itc = true, int? rate = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), supplierId, invoiceNo, DateTime.Today,
            new[] { new PurchaseLineInput(productId, qtyMilli, unitCostPaise, rate, 0, itc) }, new[] { new PaymentInput(mode, total) });
}

public class SalesReturnTests
{
    [Fact]
    public async Task Full_return_gives_money_back_restocks_and_cancels_the_sale_in_the_books()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var (saleId, lineId) = await Helpers.SellAsync(app, pid, 2000, 23600);
        var r = await app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(saleId, lineId, 2000));

        Assert.Equal($"CN/{FinancialYears.For(DateTime.Now).Label}/000001", r.ReturnNumber);
        Assert.Equal(23600, r.TotalPaise);
        Assert.Equal(10000, (await app.Get<ICatalogService>().GetAsync(pid))!.QtyOnHandMilli);
        await Helpers.AssertBooksBalancedAsync(app);
        Assert.Equal(0, await Helpers.NetAsync(app, AccountCodes.Sales));
        Assert.Equal(0, await Helpers.NetAsync(app, AccountCodes.Cash));
        Assert.Equal(0, await Helpers.NetAsync(app, AccountCodes.Cogs));
        Assert.Equal(0, await Helpers.NetAsync(app, AccountCodes.CgstPayable));
        Assert.Equal(60000, await Helpers.NetAsync(app, AccountCodes.Inventory));
    }

    [Fact]
    public async Task Partial_returns_add_up_exactly_to_the_original_tax_and_cannot_exceed_what_was_sold()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync(priceInclusivePaise: 3333);      // awkward price: tax splits do not divide evenly
        var (saleId, lineId) = await Helpers.SellAsync(app, pid, 3000, 10000);
        var original = (await app.Get<ISalesService>().GetAsync(saleId))!;
        var rs = app.Get<ISalesReturnService>();

        long taxable = 0, tax = 0;
        for (var i = 0; i < 3; i++)
        {
            var preview = await rs.PreviewAsync(Helpers.Ret(saleId, lineId, 1000));
            var ret = await rs.ReturnAsync(Helpers.Ret(saleId, lineId, 1000));
            var d = (await rs.GetAsync(ret.ReturnId))!;
            taxable += d.TaxablePaise; tax += d.CgstPaise + d.SgstPaise + d.IgstPaise;
            Assert.Equal(preview.TotalPaise, ret.TotalPaise);
        }
        Assert.Equal(original.TaxablePaise, taxable);
        Assert.Equal(original.CgstPaise + original.SgstPaise + original.IgstPaise, tax);
        await Assert.ThrowsAsync<ValidationException>(() => rs.ReturnAsync(Helpers.Ret(saleId, lineId, 1000)));
        Assert.Equal(0, (await rs.GetReturnableAsync(saleId))[0].RemainingMilli);
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Returning_more_than_sold_or_bad_input_is_refused()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var (saleId, lineId) = await Helpers.SellAsync(app, pid, 2000, 23600);
        var rs = app.Get<ISalesReturnService>();
        await Assert.ThrowsAsync<ValidationException>(() => rs.ReturnAsync(Helpers.Ret(saleId, lineId, 3000)));
        await Assert.ThrowsAsync<ValidationException>(() => rs.ReturnAsync(Helpers.Ret(saleId, lineId, 0)));
        await Assert.ThrowsAsync<ValidationException>(() => rs.ReturnAsync(Helpers.Ret(saleId, lineId, -1000)));
        await Assert.ThrowsAsync<ValidationException>(() => rs.ReturnAsync(Helpers.Ret(saleId, 99999, 1000)));
        await Assert.ThrowsAsync<ValidationException>(() => rs.ReturnAsync(Helpers.Ret(saleId, lineId, 1000, reason: " ")));
        await Assert.ThrowsAsync<ValidationException>(() => rs.ReturnAsync(Helpers.Ret(9999, lineId, 1000)));
        await Assert.ThrowsAsync<ValidationException>(() => rs.ReturnAsync(new SaleReturnCommand(Guid.NewGuid(),
            saleId, new[] { new ReturnLineInput(lineId, 500), new ReturnLineInput(lineId, 500) }, PaymentMode.Cash, "dup")));
        await Assert.ThrowsAsync<ValidationException>(() => rs.ReturnAsync(new SaleReturnCommand(Guid.NewGuid(), saleId, Array.Empty<ReturnLineInput>(), PaymentMode.Cash, "none")));
        Assert.Equal(8000, (await app.Get<ICatalogService>().GetAsync(pid))!.QtyOnHandMilli);      // nothing changed
    }

    [Fact]
    public async Task A_voided_bill_cannot_be_returned_and_a_bill_with_returns_cannot_be_voided()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var sales = app.Get<ISalesService>();
        var rs = app.Get<ISalesReturnService>();
        var a = await Helpers.SellAsync(app, pid, 1000, 11800);
        await sales.VoidSaleAsync(a.SaleId, "mistake", null);
        await Assert.ThrowsAsync<ValidationException>(() => rs.ReturnAsync(Helpers.Ret(a.SaleId, a.LineId, 1000)));

        var b = await Helpers.SellAsync(app, pid, 2000, 23600);
        await rs.ReturnAsync(Helpers.Ret(b.SaleId, b.LineId, 1000));
        await Assert.ThrowsAsync<ValidationException>(() => sales.VoidSaleAsync(b.SaleId, "too late", null));
    }

    [Fact]
    public async Task Damaged_goods_are_refunded_but_not_put_back_in_stock()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var (saleId, lineId) = await Helpers.SellAsync(app, pid, 1000, 11800);
        await app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(saleId, lineId, 1000, restock: false, reason: "Broken"));
        Assert.Equal(9000, (await app.Get<ICatalogService>().GetAsync(pid))!.QtyOnHandMilli);
        Assert.Equal(6000, await Helpers.NetAsync(app, AccountCodes.Cogs));       // the cost stays expensed: the goods are gone
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task The_same_return_request_twice_gives_one_credit_note()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var (saleId, lineId) = await Helpers.SellAsync(app, pid, 2000, 23600);
        var cmd = Helpers.Ret(saleId, lineId, 1000);
        var a = await app.Get<ISalesReturnService>().ReturnAsync(cmd);
        var b = await app.Get<ISalesReturnService>().ReturnAsync(cmd);
        Assert.False(a.AlreadyExisted);
        Assert.True(b.AlreadyExisted);
        Assert.Equal(a.ReturnNumber, b.ReturnNumber);
        Assert.Equal(9000, (await app.Get<ICatalogService>().GetAsync(pid))!.QtyOnHandMilli);
        Assert.Single(await app.Get<ISalesReturnService>().ListAsync(Helpers.Today));
    }

    [Fact]
    public async Task A_cashier_needs_admin_approval_for_a_return()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var (saleId, lineId) = await Helpers.SellAsync(app, pid, 1000, 11800);
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        var rs = app.Get<ISalesReturnService>();
        var cmd = Helpers.Ret(saleId, lineId, 1000);
        var ex = await Assert.ThrowsAsync<ApprovalRequiredException>(() => rs.ReturnAsync(cmd));
        Assert.Equal(ApprovalReason.SaleReturn, ex.Reason);
        var approver = await app.Get<IAuthService>().VerifyAdminApprovalAsync(TestApp.AdminPassword);
        var r = await rs.ReturnAsync(cmd with { ApprovedByUserId = approver });
        Assert.Equal(11800, r.TotalPaise);
    }

    [Fact]
    public async Task Refund_to_a_customer_account_reduces_what_they_owe_and_is_limited_to_what_they_owe()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var parties = app.Get<IPartyService>();
        var ravi = await parties.SaveAsync(new PartyDto(0, "Ravi Traders", null, null, "33", 0));
        var (saleId, lineId) = await Helpers.SellAsync(app, pid, 2000, 23600, PaymentMode.Credit, ravi.Id);
        var rs = app.Get<ISalesReturnService>();

        await rs.ReturnAsync(Helpers.Ret(saleId, lineId, 1000, PaymentMode.Credit));
        Assert.Equal(11800, (await parties.SearchAsync("Ravi"))[0].OutstandingPaise);

        await parties.ReceivePaymentAsync(ravi.Id, 11000, PaymentMode.Cash);                  // customer now owes only 8.00
        await Assert.ThrowsAsync<ValidationException>(() => rs.ReturnAsync(Helpers.Ret(saleId, lineId, 1000, PaymentMode.Credit)));
        await rs.ReturnAsync(Helpers.Ret(saleId, lineId, 1000, PaymentMode.Cash));            // cash refund still allowed
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Credit_refund_is_not_possible_for_a_walk_in_cash_sale()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var (saleId, lineId) = await Helpers.SellAsync(app, pid, 1000, 11800);
        await Assert.ThrowsAsync<ValidationException>(() => app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(saleId, lineId, 1000, PaymentMode.Credit)));
    }

    [Fact]
    public async Task Inter_state_return_reverses_igst()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var (saleId, lineId) = await Helpers.SellAsync(app, pid, 1000, 11800, buyerState: "29");
        var r = await app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(saleId, lineId, 1000, PaymentMode.Upi));
        var d = (await app.Get<ISalesReturnService>().GetAsync(r.ReturnId))!;
        Assert.Equal(1800, d.IgstPaise);
        Assert.Equal(0, await Helpers.NetAsync(app, AccountCodes.IgstPayable));
        Assert.Equal(-11800, await Helpers.NetAsync(app, AccountCodes.Bank));           // refunded by UPI
        Assert.Equal(11800, await Helpers.NetAsync(app, AccountCodes.Cash));            // the original cash is still in the till
    }

    [Fact]
    public async Task Rounding_of_a_return_is_balanced_with_a_round_off_entry()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync(priceInclusivePaise: 10049);
        var (saleId, lineId) = await Helpers.SellAsync(app, pid, 1000, 10000);
        var r = await app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(saleId, lineId, 1000));
        Assert.Equal(10000, r.TotalPaise);
        await Helpers.AssertBooksBalancedAsync(app);
        Assert.Equal(0, await Helpers.NetAsync(app, AccountCodes.RoundOff));
    }

    [Fact]
    public async Task Day_summary_shows_returns_and_reduces_the_payment_mode_that_was_refunded()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var (saleId, lineId) = await Helpers.SellAsync(app, pid, 2000, 23600);
        await app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(saleId, lineId, 1000));
        var s = await app.Get<IReportService>().DailySummaryAsync(Helpers.Today);
        Assert.Equal(1, s.ReturnCount);
        Assert.Equal(11800, s.ReturnTotalPaise);
        Assert.Equal(11800, s.ByPaymentMode.Single(x => x.Mode == PaymentMode.Cash).AmountPaise);   // 236.00 taken, 118.00 refunded
    }

    [Fact]
    public async Task A_locked_period_blocks_returns()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var (saleId, lineId) = await Helpers.SellAsync(app, pid, 1000, 11800);
        await app.Get<ISettingsService>().SetAsync(SettingKeys.LockedThrough, DateTime.Today.ToString("yyyy-MM-dd"));
        await Assert.ThrowsAsync<BusinessRuleException>(() => app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(saleId, lineId, 1000)));
    }
}

public class PurchaseTests
{
    [Fact]
    public async Task Cash_purchase_adds_stock_at_cost_posts_input_credit_and_balances()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();                                       // 10 units @ 60.00
        var sup = await Helpers.AddSupplierAsync(app);
        var r = await app.Get<IPurchaseService>().CompleteAsync(Helpers.Buy(sup, pid, 20_000, 5000, 118_000));   // 20 @ 50.00 + 18%

        Assert.Equal($"PUR/{FinancialYears.For(DateTime.Now).Label}/000001", r.PurchaseNumber);
        var p = (await app.Get<ICatalogService>().GetAsync(pid))!;
        Assert.Equal(30_000, p.QtyOnHandMilli);
        Assert.Equal(5333, p.AvgCostPaise);                                    // (10x60 + 20x50) / 30 = 53.33
        Assert.Equal(60000 + 100000, await Helpers.NetAsync(app, AccountCodes.Inventory));
        Assert.Equal(9000, await Helpers.NetAsync(app, AccountCodes.InputCgst));
        Assert.Equal(9000, await Helpers.NetAsync(app, AccountCodes.InputSgst));
        Assert.Equal(-118_000, await Helpers.NetAsync(app, AccountCodes.Cash));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task The_same_supplier_invoice_cannot_be_entered_twice_but_another_supplier_can_use_the_number()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var s1 = await Helpers.AddSupplierAsync(app, "Supplier One");
        var s2 = await Helpers.AddSupplierAsync(app, "Supplier Two");
        var ps = app.Get<IPurchaseService>();
        await ps.CompleteAsync(Helpers.Buy(s1, pid, 1000, 5000, 5900, invoiceNo: "A-100"));
        await Assert.ThrowsAsync<ValidationException>(() => ps.CompleteAsync(Helpers.Buy(s1, pid, 1000, 5000, 5900, invoiceNo: "a-100")));
        await ps.CompleteAsync(Helpers.Buy(s2, pid, 1000, 5000, 5900, invoiceNo: "A-100"));
    }

    [Fact]
    public async Task Retrying_the_same_purchase_command_never_duplicates_it()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var sup = await Helpers.AddSupplierAsync(app);
        var cmd = Helpers.Buy(sup, pid, 5000, 5000, 29500);
        var a = await app.Get<IPurchaseService>().CompleteAsync(cmd);
        var b = await app.Get<IPurchaseService>().CompleteAsync(cmd);
        Assert.True(b.AlreadyExisted);
        Assert.Equal(a.PurchaseNumber, b.PurchaseNumber);
        Assert.Equal(15_000, (await app.Get<ICatalogService>().GetAsync(pid))!.QtyOnHandMilli);
    }

    [Fact]
    public async Task Credit_purchase_creates_a_payable_that_can_be_paid_in_parts()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var sup = await Helpers.AddSupplierAsync(app);
        await app.Get<IPurchaseService>().CompleteAsync(Helpers.Buy(sup, pid, 10_000, 5000, 59000, mode: PaymentMode.Credit));
        var suppliers = app.Get<ISupplierService>();
        Assert.Equal(59000, (await suppliers.SearchAsync("Metro"))[0].OutstandingPaise);
        Assert.Equal(-59000, await Helpers.NetAsync(app, AccountCodes.Payables));

        await suppliers.PayAsync(sup, 20000, PaymentMode.Cash);
        Assert.Equal(39000, (await suppliers.SearchAsync("Metro"))[0].OutstandingPaise);
        await Assert.ThrowsAsync<ValidationException>(() => suppliers.PayAsync(sup, 39001, PaymentMode.Cash));
        await Assert.ThrowsAsync<ValidationException>(() => suppliers.PayAsync(sup, 100, PaymentMode.Credit));
        await Assert.ThrowsAsync<ValidationException>(() => suppliers.PayAsync(sup, 0, PaymentMode.Cash));
        await suppliers.PayAsync(sup, 39000, PaymentMode.Upi);
        Assert.Equal(0, (await suppliers.SearchAsync("Metro"))[0].OutstandingPaise);
        Assert.Equal(0, await Helpers.NetAsync(app, AccountCodes.Payables));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Supplier_from_another_state_charges_igst_input()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var sup = await Helpers.AddSupplierAsync(app, "Bangalore Traders", "29AAAAA0000A1Z5", "29");
        await app.Get<IPurchaseService>().CompleteAsync(Helpers.Buy(sup, pid, 10_000, 5000, 59000));
        Assert.Equal(9000, await Helpers.NetAsync(app, AccountCodes.InputIgst));
        Assert.Equal(0, await Helpers.NetAsync(app, AccountCodes.InputCgst));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Tax_that_cannot_be_claimed_becomes_part_of_the_stock_cost()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var sup = await Helpers.AddSupplierAsync(app);
        await app.Get<IPurchaseService>().CompleteAsync(Helpers.Buy(sup, pid, 10_000, 5000, 59000, itc: false));
        Assert.Equal(0, await Helpers.NetAsync(app, AccountCodes.InputCgst));
        Assert.Equal(60000 + 59000, await Helpers.NetAsync(app, AccountCodes.Inventory));       // cost includes the 18%
        Assert.Equal(5950, (await app.Get<ICatalogService>().GetAsync(pid))!.AvgCostPaise);      // (10x60 + 10x59) / 20
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task A_composition_dealer_can_never_claim_input_credit()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync(composition: true);
        var sup = await Helpers.AddSupplierAsync(app);
        await app.Get<IPurchaseService>().CompleteAsync(Helpers.Buy(sup, pid, 10_000, 5000, 59000, itc: true));
        Assert.Equal(0, await Helpers.NetAsync(app, AccountCodes.InputCgst));
        Assert.Equal(0, await Helpers.NetAsync(app, AccountCodes.InputSgst));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Purchase_rounding_and_per_line_gst_override_balance()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var sup = await Helpers.AddSupplierAsync(app);
        // 3 x 33.33 at 5% = 99.99 + 5.00 = 104.99 -> 105.00 (round-off 0.01)
        var r = await app.Get<IPurchaseService>().CompleteAsync(Helpers.Buy(sup, pid, 3000, 3333, 10500, rate: 500));
        Assert.Equal(10500, r.TotalPaise);
        var d = (await app.Get<IPurchaseService>().GetAsync(r.PurchaseId))!;
        Assert.Equal(1, d.RoundOffPaise);
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Purchase_rules_payment_must_match_and_inputs_are_validated()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var sup = await Helpers.AddSupplierAsync(app);
        var ps = app.Get<IPurchaseService>();
        await Assert.ThrowsAsync<ValidationException>(() => ps.CompleteAsync(Helpers.Buy(sup, pid, 1000, 5000, 5000)));                 // pays 50.00 of 59.00
        await Assert.ThrowsAsync<ValidationException>(() => ps.CompleteAsync(Helpers.Buy(sup, pid, 1000, 5000, 5900, invoiceNo: " ")));
        await Assert.ThrowsAsync<ValidationException>(() => ps.CompleteAsync(Helpers.Buy(sup, pid, 0, 5000, 5900)));
        await Assert.ThrowsAsync<ValidationException>(() => ps.CompleteAsync(Helpers.Buy(sup, pid, 1000, -5, 5900)));
        await Assert.ThrowsAsync<ValidationException>(() => ps.CompleteAsync(Helpers.Buy(9999, pid, 1000, 5000, 5900)));
        await Assert.ThrowsAsync<ValidationException>(() => ps.CompleteAsync(Helpers.Buy(sup, 9999, 1000, 5000, 5900)));
        await Assert.ThrowsAsync<ValidationException>(() => ps.CompleteAsync(Helpers.Buy(sup, pid, 1000, 5000, 5900) with { SupplierInvoiceDate = DateTime.Today.AddDays(2) }));
        await Assert.ThrowsAsync<ValidationException>(() => ps.CompleteAsync(Helpers.Buy(sup, pid, 1000, 5000, 5900, rate: 9000)));
        await Assert.ThrowsAsync<ValidationException>(() => ps.CompleteAsync(new CompletePurchaseCommand(Guid.NewGuid(), sup, "X", null, Array.Empty<PurchaseLineInput>(), Array.Empty<PaymentInput>())));
        Assert.Equal(10_000, (await app.Get<ICatalogService>().GetAsync(pid))!.QtyOnHandMilli);       // nothing was saved
        Assert.Empty(await ps.ListAsync(Helpers.Today, Helpers.Today));
        // a failed attempt does not burn a number
        var ok = await ps.CompleteAsync(Helpers.Buy(sup, pid, 1000, 5000, 5900));
        Assert.EndsWith("/000001", ok.PurchaseNumber);
    }

    [Fact]
    public async Task A_cashier_cannot_enter_purchases_or_suppliers()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var sup = await Helpers.AddSupplierAsync(app);
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => app.Get<IPurchaseService>().CompleteAsync(Helpers.Buy(sup, pid, 1000, 5000, 5900)));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => app.Get<ISupplierService>().SearchAsync(""));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => app.Get<ISupplierService>().PayAsync(sup, 100, PaymentMode.Cash));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => app.Get<IPurchaseService>().ListAsync(Helpers.Today, Helpers.Today));
    }

    [Fact]
    public async Task Buying_stock_that_was_already_sold_below_zero_posts_a_cost_variance()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();                                                    // 10 @ 60.00
        await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 12_000, 141_600));   // -2 in stock, costed at 60.00
        var sup = await Helpers.AddSupplierAsync(app);
        await app.Get<IPurchaseService>().CompleteAsync(Helpers.Buy(sup, pid, 5000, 7000, 41300, itc: true));   // 5 @ 70.00
        Assert.Equal(7000, (await app.Get<ICatalogService>().GetAsync(pid))!.AvgCostPaise);
        Assert.Equal(2000, await Helpers.NetAsync(app, AccountCodes.InventoryCostVariance));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Duplicate_supplier_or_bad_supplier_details_are_rejected()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var s = app.Get<ISupplierService>();
        await Assert.ThrowsAsync<ValidationException>(() => s.SaveAsync(new SupplierDto(0, " ", null, null, null, 0)));
        await Assert.ThrowsAsync<ValidationException>(() => s.SaveAsync(new SupplierDto(0, "X", null, "BADGSTIN", null, 0)));
        await Assert.ThrowsAsync<ValidationException>(() => s.SaveAsync(new SupplierDto(0, "X", null, "29AAAAA0000A1Z5", "33", 0)));
        var ok = await s.SaveAsync(new SupplierDto(0, "Good Supplier", null, "29AAAAA0000A1Z5", null, 0));
        Assert.Equal("29", ok.StateCode);                                                   // state taken from the GSTIN
    }
}

public class PurchaseReturnTests
{
    private static async Task<(TestApp App, long Pid, long Sup, long PurchaseId, long LineId)> SetupAsync(PaymentMode mode = PaymentMode.Cash)
    {
        var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var sup = await Helpers.AddSupplierAsync(app);
        var r = await app.Get<IPurchaseService>().CompleteAsync(Helpers.Buy(sup, pid, 20_000, 5000, 118_000, mode: mode));
        var lines = await app.Get<IPurchaseService>().GetReturnableAsync(r.PurchaseId);
        return (app, pid, sup, r.PurchaseId, lines[0].LineId);
    }

    private static PurchaseReturnCommand Ret(long purchaseId, long lineId, long qty, PaymentMode mode = PaymentMode.Cash, string reason = "Damaged in transit", Guid? id = null) =>
        new(id ?? Guid.NewGuid(), purchaseId, new[] { new ReturnLineInput(lineId, qty) }, mode, reason);

    [Fact]
    public async Task Sending_goods_back_reduces_stock_and_refunds_with_correct_books()
    {
        var (app, pid, _, purchaseId, lineId) = await SetupAsync();
        using var _ = app;
        var ps = app.Get<IPurchaseService>();
        var r = await ps.ReturnAsync(Ret(purchaseId, lineId, 5000));
        Assert.StartsWith("DN/", r.ReturnNumber);
        Assert.Equal(29_500, r.TotalPaise);                                                  // 5 x 50.00 + 18%
        var p = (await app.Get<ICatalogService>().GetAsync(pid))!;
        Assert.Equal(25_000, p.QtyOnHandMilli);
        Assert.Equal(5400, p.AvgCostPaise);                                                  // (10 x 60 + 15 x 50) / 25 = 54.00 once 5 @ 50.00 go back
        Assert.Equal(60000 + 75000, await Helpers.NetAsync(app, AccountCodes.Inventory));
        Assert.Equal(6750, await Helpers.NetAsync(app, AccountCodes.InputCgst));
        Assert.Equal(-118_000 + 29_500, await Helpers.NetAsync(app, AccountCodes.Cash));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Cannot_send_back_more_than_bought_more_than_in_stock_or_twice_the_same_command()
    {
        var (app, pid, _, purchaseId, lineId) = await SetupAsync();
        using var _ = app;
        var ps = app.Get<IPurchaseService>();
        await Assert.ThrowsAsync<ValidationException>(() => ps.ReturnAsync(Ret(purchaseId, lineId, 21_000)));
        await Assert.ThrowsAsync<ValidationException>(() => ps.ReturnAsync(Ret(purchaseId, lineId, 0)));
        await Assert.ThrowsAsync<ValidationException>(() => ps.ReturnAsync(Ret(purchaseId, lineId, 1000, reason: "")));

        // sell most of the stock; only what is left on the shelf can go back
        await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 28_000, 330_400));
        await Assert.ThrowsAsync<ValidationException>(() => ps.ReturnAsync(Ret(purchaseId, lineId, 5000)));
        var cmd = Ret(purchaseId, lineId, 2000);
        var a = await ps.ReturnAsync(cmd);
        var b = await ps.ReturnAsync(cmd);
        Assert.True(b.AlreadyExisted);
        Assert.Equal(a.ReturnNumber, b.ReturnNumber);
        Assert.Equal(0, (await app.Get<ICatalogService>().GetAsync(pid))!.QtyOnHandMilli);
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task A_return_credited_to_the_account_reduces_what_we_owe_and_is_limited_to_it()
    {
        var (app, _, sup, purchaseId, lineId) = await SetupAsync(PaymentMode.Credit);
        using var _ = app;
        var ps = app.Get<IPurchaseService>();
        var suppliers = app.Get<ISupplierService>();
        Assert.Equal(118_000, (await suppliers.SearchAsync("Metro"))[0].OutstandingPaise);
        await ps.ReturnAsync(Ret(purchaseId, lineId, 5000, PaymentMode.Credit));
        Assert.Equal(88_500, (await suppliers.SearchAsync("Metro"))[0].OutstandingPaise);

        await suppliers.PayAsync(sup, 88_000, PaymentMode.Cash);                              // we owe only 5.00 now
        await Assert.ThrowsAsync<ValidationException>(() => ps.ReturnAsync(Ret(purchaseId, lineId, 5000, PaymentMode.Credit)));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Partial_purchase_returns_add_up_exactly_to_the_original_tax()
    {
        var app = TestApp.Create();
        using var _ = app;
        var pid = await app.SeedAsync();
        var sup = await Helpers.AddSupplierAsync(app);
        var r = await app.Get<IPurchaseService>().CompleteAsync(Helpers.Buy(sup, pid, 3000, 3333, 11800));
        var lineId = (await app.Get<IPurchaseService>().GetReturnableAsync(r.PurchaseId))[0].LineId;
        var original = (await app.Get<IPurchaseService>().GetAsync(r.PurchaseId))!;
        long taxable = 0, tax = 0;
        for (var i = 0; i < 3; i++)
        {
            var ret = await app.Get<IPurchaseService>().ReturnAsync(Ret(r.PurchaseId, lineId, 1000));
            var d = (await app.Get<IPurchaseService>().GetReturnAsync(ret.ReturnId))!;
            taxable += d.TaxablePaise; tax += d.CgstPaise + d.SgstPaise + d.IgstPaise;
        }
        Assert.Equal(original.TaxablePaise, taxable);
        Assert.Equal(original.CgstPaise + original.SgstPaise + original.IgstPaise, tax);
        await Helpers.AssertBooksBalancedAsync(app);
        Assert.Equal(0, await Helpers.NetAsync(app, AccountCodes.InputCgst));
    }
}

public class SchemaUpgradeTests
{
    [Fact]
    public async Task A_database_from_the_previous_release_is_upgraded_without_losing_data()
    {
        var app1 = TestApp.Create();
        var pid = await app1.SeedAsync();
        var (saleId, _) = await Helpers.SellAsync(app1, pid, 1000, 11800);
        var dir = app1.Dir;
        var dbPath = app1.Paths.DbPath;
        var factory = app1.Get<DbFactory>();

        // Turn the database back into what release 1 produced: no new tables, no new accounts, no version 2 record.
        SqliteConnection.ClearAllPools();
        using (var raw = factory.OpenRaw(dbPath))
        using (var cmd = raw.CreateCommand())
        {
            cmd.CommandText = """
                DROP TABLE PurchaseReturnLines; DROP TABLE PurchaseReturns; DROP TABLE PurchasePayments; DROP TABLE PurchaseLines;
                DROP TABLE SupplierPayments; DROP TABLE Purchases; DROP TABLE Suppliers; DROP TABLE SaleReturnLines; DROP TABLE SaleReturns;
                DELETE FROM Accounts WHERE Code IN ('1410','1420','1430');
                DELETE FROM SchemaVersions WHERE Version = 2;
                """;
            cmd.ExecuteNonQuery();
        }
        app1.Services.Dispose();                       // keep the folder: this is the "customer's PC"

        using var app2 = TestApp.Create(dir);
        await app2.SignInAsAdminAsync();
        // old data intact
        Assert.Single(await app2.Get<ISalesService>().ListAsync(Helpers.Today));
        Assert.Equal(9000, (await app2.Get<ICatalogService>().GetAsync(pid))!.QtyOnHandMilli);
        // new features work on the upgraded database
        var sup = await Helpers.AddSupplierAsync(app2);
        await app2.Get<IPurchaseService>().CompleteAsync(Helpers.Buy(sup, pid, 10_000, 5000, 59000));
        var lines = await app2.Get<ISalesReturnService>().GetReturnableAsync(saleId);
        await app2.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(saleId, lines[0].LineId, 1000));
        await Helpers.AssertBooksBalancedAsync(app2);
        Assert.True((await app2.Get<IAuditService>().VerifyChainAsync()).Valid);

        // starting again must be harmless (idempotent upgrade)
        app2.Services.Dispose();
        using var app3 = TestApp.Create(dir);
        await app3.SignInAsAdminAsync();
        Assert.Single(await app3.Get<ISupplierService>().SearchAsync(""));
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
