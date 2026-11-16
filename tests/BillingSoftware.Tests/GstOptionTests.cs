using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BillingSoftware.Tests;

public class GstOptionTests
{
    private static DateOnly Today => Helpers.Today;

    private static Task SetGstAsync(TestApp app, bool on, bool composition = false) =>
        app.Get<ISettingsService>().SaveCompanyAsync(new CompanyDto("Test Stores", "33ABCDE1234F1Z5", "33", composition, "Main Road", "9000000000", on));

    private static async Task<SaleDetailDto> BillAsync(TestApp app, long product, long qtyMilli, long total)
    {
        var r = await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(product, qtyMilli, total));
        return (await app.Get<ISalesService>().GetAsync(r.SaleId))!;
    }

    [Fact]
    public async Task GST_is_on_by_default()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var company = (await app.Get<ISettingsService>().GetCompanyAsync())!;
        Assert.True(company.GstEnabled);
        Assert.True(company.CollectsTax);
        var bill = await BillAsync(app, soap, 1000, 11_800);
        Assert.StartsWith("INV/", bill.InvoiceNumber);
        Assert.Equal(900, bill.CgstPaise);                                          // 18% inside 118.00
        Assert.Equal(900, bill.SgstPaise);
        Assert.Equal(-10_000, await Helpers.NetAsync(app, AccountCodes.Sales));
    }

    [Fact]
    public async Task With_GST_off_bills_carry_no_tax_and_are_bills_of_supply()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        await SetGstAsync(app, false);
        Assert.False((await app.Get<ISettingsService>().GetCompanyAsync())!.CollectsTax);

        var bill = await BillAsync(app, soap, 1000, 11_800);                        // the item is priced 118.00 and GST is not charged on top
        Assert.StartsWith("BOS/", bill.InvoiceNumber);
        Assert.Equal(0, bill.CgstPaise + bill.SgstPaise + bill.IgstPaise);
        Assert.Equal(11_800, bill.TotalPaise);
        Assert.Equal(11_800, bill.TaxablePaise);
        Assert.Equal(-11_800, await Helpers.NetAsync(app, AccountCodes.Sales));     // all of it is sales
        Assert.Equal(0, await Helpers.NetAsync(app, AccountCodes.CgstPayable) + await Helpers.NetAsync(app, AccountCodes.SgstPayable) + await Helpers.NetAsync(app, AccountCodes.IgstPayable));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task With_GST_off_a_customer_in_another_state_is_not_charged_IGST()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        await SetGstAsync(app, false);
        var r = await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(soap, 1000, 11_800, buyerState: "29"));
        var d = (await app.Get<ISalesService>().GetAsync(r.SaleId))!;
        Assert.Equal(0, d.IgstPaise + d.CgstPaise + d.SgstPaise);
    }

    [Fact]
    public async Task Turning_GST_back_on_resumes_tax_invoices_and_earlier_bills_do_not_change()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var first = await BillAsync(app, soap, 1000, 11_800);                       // GST on
        await SetGstAsync(app, false);
        var second = await BillAsync(app, soap, 1000, 11_800);
        await SetGstAsync(app, true);
        var third = await BillAsync(app, soap, 1000, 11_800);

        Assert.StartsWith("INV/", first.InvoiceNumber);
        Assert.StartsWith("BOS/", second.InvoiceNumber);
        Assert.StartsWith("INV/", third.InvoiceNumber);
        Assert.EndsWith("000002", third.InvoiceNumber);                               // each series stays gapless
        Assert.EndsWith("000001", second.InvoiceNumber);
        Assert.Equal(1_800, first.CgstPaise + first.SgstPaise);
        Assert.Equal(0, second.CgstPaise + second.SgstPaise);
        Assert.Equal(1_800, third.CgstPaise + third.SgstPaise);
        Assert.Equal(-(10_000 + 11_800 + 10_000), await Helpers.NetAsync(app, AccountCodes.Sales));
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task A_bill_made_with_GST_can_still_be_returned_with_its_tax_after_GST_is_turned_off()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var (sale, line) = await Helpers.SellAsync(app, soap, 2000, 23_600);
        await SetGstAsync(app, false);
        await app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(sale, line, 1000));
        Assert.Equal(-(20_000 - 10_000), await Helpers.NetAsync(app, AccountCodes.Sales));
        Assert.Equal(-1_800, await Helpers.NetAsync(app, AccountCodes.CgstPayable) + await Helpers.NetAsync(app, AccountCodes.SgstPayable));   // half the original 36.00 tax stays
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task With_GST_off_the_GST_on_a_purchase_is_part_of_the_cost_and_no_credit_is_claimed()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var sup = await Helpers.AddSupplierAsync(app);
        await SetGstAsync(app, false);
        var inventoryBefore = await Helpers.NetAsync(app, AccountCodes.Inventory);
        await app.Get<IPurchaseService>().CompleteAsync(Helpers.Buy(sup, soap, 10_000, 5_000, 59_000, "S-1"));   // 10 units at 50.00 + 18%
        Assert.Equal(0, await Helpers.NetAsync(app, AccountCodes.InputCgst) + await Helpers.NetAsync(app, AccountCodes.InputSgst) + await Helpers.NetAsync(app, AccountCodes.InputIgst));
        Assert.Equal(inventoryBefore + 59_000, await Helpers.NetAsync(app, AccountCodes.Inventory));                         // tax went into the cost
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task With_GST_off_input_credit_on_an_expense_is_refused_and_the_GST_worksheets_are_not_available()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await SetGstAsync(app, false);
        var rent = (await app.Get<IExpenseService>().ListCategoriesAsync()).Single(c => c.Name == "Rent").Id;
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => app.Get<IExpenseService>().RecordAsync(
            new RecordExpenseCommand(Guid.NewGuid(), Today, rent, "Landlord", "Rent", "B-1", 118_000, PaymentMode.Upi, 18_000, true, "33AAAAA1111A1Z5")));
        Assert.Contains("GST is turned off", ex.Message);
        await app.Get<IExpenseService>().RecordAsync(new RecordExpenseCommand(Guid.NewGuid(), Today, rent, "Landlord", "Rent", null, 118_000, PaymentMode.Upi));   // without a claim it is fine

        var gst = await Assert.ThrowsAsync<BusinessRuleException>(() => app.Get<IGstReportService>().BuildAsync(Today.Year, Today.Month));
        Assert.Contains("turned off", gst.Message);
        await SetGstAsync(app, true);
        Assert.NotNull(await app.Get<IGstReportService>().BuildAsync(Today.Year, Today.Month));                  // back on: the worksheets work again
    }

    [Fact]
    public async Task Turning_GST_off_also_ends_the_composition_scheme_and_a_cashier_cannot_change_it()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await SetGstAsync(app, true, composition: true);
        Assert.False((await app.Get<ISettingsService>().GetCompanyAsync())!.CollectsTax);
        await SetGstAsync(app, false, composition: false);
        Assert.False((await app.Get<ISettingsService>().GetCompanyAsync())!.GstEnabled);

        await using (var db = app.Get<DbFactory>().Create())
            Assert.Contains(db.AuditEvents.Select(e => e.Details).ToList(), d => d.Contains("gst=off"));

        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => SetGstAsync(app, true));
    }

    [Fact]
    public async Task A_composition_business_still_gets_bills_of_supply_with_GST_on()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync(composition: true);
        var bill = await BillAsync(app, soap, 1000, 11_800);
        Assert.StartsWith("BOS/", bill.InvoiceNumber);
        Assert.Equal(0, bill.CgstPaise + bill.SgstPaise);
    }

    [Fact]
    public async Task A_database_from_the_previous_release_has_GST_on()
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
            cmd.CommandText = "ALTER TABLE Companies DROP COLUMN GstEnabled; DELETE FROM SchemaVersions WHERE Version = 11;";
            cmd.ExecuteNonQuery();
        }
        app1.Services.Dispose();

        using var app2 = TestApp.Create(dir);
        await app2.SignInAsAdminAsync();
        Assert.True((await app2.Get<ISettingsService>().GetCompanyAsync())!.GstEnabled);
        var soap = (await app2.Get<ICatalogService>().SearchAsync("SKU1")).Single().Id;
        Assert.StartsWith("INV/", (await BillAsync(app2, soap, 1000, 11_800)).InvoiceNumber);
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
