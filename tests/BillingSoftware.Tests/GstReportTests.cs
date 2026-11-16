using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using BillingSoftware.Infrastructure.Export;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BillingSoftware.Tests;

public class GstReportTests
{
    private sealed record Ctx(TestApp App, long A, long B, long C, long D, long Sale3, long Sale5);

    private static long Total(long price, long qtyMilli) =>
        InvoiceRounding.ToNearestRupee(Money.RoundHalfUp((decimal)price * qtyMilli / 1000m)).Total;

    private static async Task<long> ProductAsync(TestApp app, string sku, string name, string? hsn, int rateBp, long price)
    {
        var p = await app.Get<ICatalogService>().SaveAsync(new ProductDto(0, sku, null, name, hsn, rateBp, "pcs", price, price, true, 0, 0, true));
        await app.Get<IStockService>().ReceiveOpeningStockAsync(p.Id, 100_000, price / 2);
        return p.Id;
    }

    private static async Task<long> SellAsync(TestApp app, long pid, long price, long qtyMilli, long? partyId = null, string? name = null, string? gstin = null, string? state = null)
    {
        var cmd = new CompleteSaleCommand(Guid.NewGuid(), new[] { new SaleLineInput(pid, qtyMilli) },
            new[] { new PaymentInput(PaymentMode.Cash, Total(price, qtyMilli)) }, partyId, name, gstin, state);
        return (await app.Get<ISalesService>().CompleteSaleAsync(cmd)).SaleId;
    }

    private static async Task ReturnAsync(TestApp app, long saleId, long qtyMilli)
    {
        var line = (await app.Get<ISalesReturnService>().GetReturnableAsync(saleId))[0].LineId;
        await app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(saleId, line, qtyMilli));
    }

    private static async Task<Ctx> BuildAsync()
    {
        var app = TestApp.Create();
        var a = await app.SeedAsync();                                                         // 18%, HSN 3401, price 118.00
        await app.Get<IStockService>().ReceiveOpeningStockAsync(a, 100_000, 6000);
        var b = await ProductAsync(app, "SKU-B", "Salt", "2501", 500, 2800);                  // 5%
        var c = await ProductAsync(app, "SKU-C", "Notebook", "4820", 0, 5000);                // 0%
        var d = await ProductAsync(app, "SKU-D", "Mystery item", null, 1200, 11200);          // 12%, no HSN
        await app.Get<ISettingsService>().SetAsync(SettingKeys.B2ClThresholdPaise, "100000");   // large-invoice limit 1,000.00 for the test

        var mysore = await app.Get<IPartyService>().SaveAsync(new PartyDto(0, "Mysore Traders", null, null, "29", 0));

        await SellAsync(app, a, 11800, 2000);                                                  // s1 walk-in, B2CS intra 18%
        await SellAsync(app, b, 2800, 3000);                                                   // s2 walk-in, B2CS intra 5%
        var s3 = await SellAsync(app, a, 11800, 1000, null, "Kumar Stores", "33AAAAA1111A1Z5", "33");   // s3 B2B intra
        await SellAsync(app, a, 11800, 1000, null, "Bangalore Mart", "29AAAAA2222A1Z5", null);          // s4 B2B inter, state from GSTIN
        var s5 = await SellAsync(app, a, 11800, 10_000, mysore.Id, "Mysore Traders", null, "29");       // s5 B2CL (118000 > 100000)
        await SellAsync(app, a, 11800, 1000, mysore.Id, "Mysore Traders", null, "29");                   // s6 small inter-state consumer: B2CS
        await SellAsync(app, c, 5000, 4000);                                                   // s7 0% supply
        var s8 = await SellAsync(app, a, 11800, 1000);                                         // s8 cancelled
        await app.Get<ISalesService>().VoidSaleAsync(s8, "typo", null);
        await SellAsync(app, d, 11200, 1000);                                                  // s9 item without HSN

        var s1 = (await app.Get<ISalesService>().ListAsync(Helpers.Today)).Last().Id;
        await ReturnAsync(app, s1, 1000);                                                      // credit note netted into B2CS
        await ReturnAsync(app, s3, 1000);                                                      // credit note to a registered customer
        await ReturnAsync(app, s5, 2000);                                                      // credit note against a B2CL invoice

        var sup1 = await Helpers.AddSupplierAsync(app, "Metro Wholesale", "33AAAAA0000A1Z5", "33");
        var sup2 = await Helpers.AddSupplierAsync(app, "Bangalore Traders", "29BBBBB0000B1Z5", "29");
        var sup3 = await Helpers.AddSupplierAsync(app, "Local Seller", null, "33");
        var ps = app.Get<IPurchaseService>();
        var p1 = await ps.CompleteAsync(Helpers.Buy(sup1, a, 20_000, 5000, 118_000, "S-1"));
        await ps.CompleteAsync(Helpers.Buy(sup2, b, 10_000, 2000, 21_000, "S-2"));
        await ps.CompleteAsync(Helpers.Buy(sup1, d, 10_000, 3000, 33_600, "S-3", itc: false));
        await ps.CompleteAsync(Helpers.Buy(sup1, c, 10_000, 2000, 20_000, "S-4", rate: 0));
        await ps.CompleteAsync(Helpers.Buy(sup3, a, 5_000, 5000, 29_500, "S-5"));
        var line = (await ps.GetReturnableAsync(p1.PurchaseId))[0].LineId;
        await ps.ReturnAsync(new PurchaseReturnCommand(Guid.NewGuid(), p1.PurchaseId, new[] { new ReturnLineInput(line, 5000, false) }, PaymentMode.Cash, "Damaged"));
        return new Ctx(app, a, b, c, d, s3, s5);
    }

    private static Task<GstReport> ReportAsync(TestApp app) => app.Get<IGstReportService>().BuildAsync(DateTime.Today.Year, DateTime.Today.Month);

    [Fact]
    public async Task Every_gst_figure_ties_back_to_the_ledger()
    {
        using var ctx = (await BuildAsync()).App;
        var app = ctx;
        var r = await ReportAsync(app);
        var g = r.Gstr3b;

        // output tax equals the GST payable accounts, input credit equals the input-credit accounts, taxable value equals Sales
        Assert.Equal(-await Helpers.NetAsync(app, AccountCodes.CgstPayable), g.OutCgstPaise);
        Assert.Equal(-await Helpers.NetAsync(app, AccountCodes.SgstPayable), g.OutSgstPaise);
        Assert.Equal(-await Helpers.NetAsync(app, AccountCodes.IgstPayable), g.OutIgstPaise);
        Assert.Equal(await Helpers.NetAsync(app, AccountCodes.InputCgst), g.ItcCgstPaise);
        Assert.Equal(await Helpers.NetAsync(app, AccountCodes.InputSgst), g.ItcSgstPaise);
        Assert.Equal(await Helpers.NetAsync(app, AccountCodes.InputIgst), g.ItcIgstPaise);
        Assert.Equal(-await Helpers.NetAsync(app, AccountCodes.Sales), g.OutTaxablePaise + g.NilRatedTaxablePaise);

        // the GSTR-1 tables add up to the same totals as the 3B summary
        var tables = r.B2B.Sum(x => x.TaxablePaise) + r.B2Cl.Sum(x => x.TaxablePaise) + r.B2Cs.Sum(x => x.TaxablePaise) - r.Notes.Sum(x => x.TaxablePaise);
        Assert.Equal(g.OutTaxablePaise, tables);
        var hsn = r.Hsn.Sum(x => x.SoldTaxablePaise) - r.Hsn.Sum(x => x.ReturnTaxablePaise);
        Assert.Equal(g.OutTaxablePaise + g.NilRatedTaxablePaise, hsn);

        Assert.Equal(g.OutCgstPaise - g.ItcCgstPaise, g.NetCgstPaise);
        Assert.Equal(g.OutIgstPaise - g.ItcIgstPaise, g.NetIgstPaise);
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Outward_supplies_land_in_the_right_gstr1_tables()
    {
        using var ctx = (await BuildAsync()).App;
        var r = await ReportAsync(ctx);

        Assert.Equal(2, r.B2B.Count);
        var intra = r.B2B.Single(x => x.Gstin == "33AAAAA1111A1Z5");
        Assert.Equal("33", intra.PlaceOfSupply);
        Assert.Equal(10000, intra.TaxablePaise);
        Assert.Equal(0, intra.IgstPaise);
        var inter = r.B2B.Single(x => x.Gstin == "29AAAAA2222A1Z5");
        Assert.Equal("29", inter.PlaceOfSupply);                                  // taken from the GSTIN, no state was entered
        Assert.Equal(1800, inter.IgstPaise);
        Assert.Equal(0, inter.CgstPaise + inter.SgstPaise);

        var big = Assert.Single(r.B2Cl);
        Assert.Equal("29", big.PlaceOfSupply);
        Assert.Equal(100_000, big.TaxablePaise);
        Assert.Equal(18_000, big.IgstPaise);

        // small consumer sales are added up by place of supply and rate, with credit notes already taken off
        var intra18 = r.B2Cs.Single(x => x.PlaceOfSupply == "33" && x.RateBp == 1800);
        Assert.Equal(20000 - 10000, intra18.TaxablePaise);                         // s1 sold 2 (200.00 taxable) less 1 returned
        Assert.Equal(900, intra18.CgstPaise);                                      // 100.00 taxable at 18% intra-state: 9.00 + 9.00
        Assert.Equal(900, intra18.SgstPaise);
        var inter18 = r.B2Cs.Single(x => x.PlaceOfSupply == "29");
        Assert.Equal(10000, inter18.TaxablePaise);
        Assert.Equal(1800, inter18.IgstPaise);
        Assert.Contains(r.B2Cs, x => x.PlaceOfSupply == "33" && x.RateBp == 500);
        Assert.Contains(r.B2Cs, x => x.PlaceOfSupply == "33" && x.RateBp == 1200);

        // credit notes: one to a registered customer, one against the large inter-state invoice
        Assert.Equal(2, r.Notes.Count);
        Assert.Contains(r.Notes, n => n.Kind == "Registered" && n.Gstin == "33AAAAA1111A1Z5" && n.TaxablePaise == 10000);
        Assert.Contains(r.Notes, n => n.Kind == "Unregistered" && n.IgstPaise == 3600 && n.OriginalNo.StartsWith("INV/"));

        // zero-rated supplies are kept out of the tax tables and listed on their own
        var nil = Assert.Single(r.NilRated);
        Assert.Equal(20000, nil.TaxablePaise);
        Assert.Contains("Intra-state", nil.Description);
        Assert.DoesNotContain(r.B2Cs, x => x.RateBp == 0);
    }

    [Fact]
    public async Task Document_summary_counts_cancelled_bills_and_numbers_the_range()
    {
        using var ctx = (await BuildAsync()).App;
        var r = await ReportAsync(ctx);
        var inv = r.Documents.Single(d => d.Description.StartsWith("Invoices"));
        Assert.Equal(9, inv.Total);
        Assert.Equal(1, inv.Cancelled);
        Assert.EndsWith("/000001", inv.From);
        Assert.EndsWith("/000009", inv.To);
        var cn = r.Documents.Single(d => d.Description == "Credit notes");
        Assert.Equal(3, cn.Total);

        var cancelled = r.SalesRegister.Single(x => x.Status == "Cancelled");
        Assert.Equal(9, r.SalesRegister.Count);
        Assert.NotNull(cancelled);
    }

    [Fact]
    public async Task Input_credit_is_net_of_returns_and_split_into_claimed_and_not_claimed()
    {
        using var ctx = (await BuildAsync()).App;
        var g = (await ReportAsync(ctx)).Gstr3b;
        Assert.Equal(9000, g.ItcCgstPaise);          // 9000 on the 20-unit buy, less 2250 returned, plus 2250 on the 5-unit buy
        Assert.Equal(9000, g.ItcSgstPaise);
        Assert.Equal(1000, g.ItcIgstPaise);          // interstate purchase of salt
        Assert.Equal(1800, g.IneligibleCgstPaise);   // purchase where credit was not claimed: its tax is not in ITC
        Assert.Equal(1800, g.IneligibleSgstPaise);
        Assert.Equal(20000, g.InwardNilIntraPaise);  // 0% purchase
        Assert.Equal(0, g.InwardNilInterPaise);
    }

    [Fact]
    public async Task Inter_state_supplies_to_consumers_are_listed_by_state_for_3b()
    {
        using var ctx = (await BuildAsync()).App;
        var g = (await ReportAsync(ctx)).Gstr3b;
        var row = Assert.Single(g.InterStateToUnregistered);
        Assert.Equal("29", row.PlaceOfSupply);
        // large bill 1000.00 + small bill 100.00 taxable, less 2 units returned (200.00)
        Assert.Equal(100_000 + 10_000 - 20_000, row.TaxablePaise);
        Assert.Equal(18_000 + 1_800 - 3_600, row.IgstPaise);
    }

    [Fact]
    public async Task The_report_warns_about_missing_hsn_unregistered_suppliers_and_cancelled_bills()
    {
        using var ctx = (await BuildAsync()).App;
        var r = await ReportAsync(ctx);
        Assert.Contains(r.Warnings, w => w.Contains("no HSN") && w.Contains("Mystery item"));
        Assert.Contains(r.Warnings, w => w.Contains("no GSTIN") && w.Contains("Local Seller"));
        Assert.Contains(r.Warnings, w => w.Contains("cancelled"));
        Assert.Contains(r.Warnings, w => w.Contains("Reverse-charge"));
        Assert.Contains(r.Warnings, w => w.Contains("large inter-state"));
        Assert.Contains(r.Hsn, h => h.Hsn == "" && h.RateBp == 1200);
        Assert.All(r.Hsn.Where(h => h.Unit == "pcs"), h => Assert.Equal("PCS", h.Uqc));
    }

    [Fact]
    public async Task The_excel_file_contains_every_worksheet_with_real_numbers_that_match_the_report()
    {
        using var app = (await BuildAsync()).App;
        var r = await ReportAsync(app);
        var file = Path.Combine(Path.GetTempPath(), $"gst-test-{Guid.NewGuid():N}.xlsx");
        try
        {
            GstExcelExporter.Write(r, file);
            using var wb = new XLWorkbook(file);
            foreach (var name in new[] { "Summary 3B", "GSTR1 B2B", "GSTR1 B2CL", "GSTR1 B2CS", "GSTR1 Credit notes", "GSTR1 HSN", "GSTR1 Documents", "Nil rated",
                                         "Sales register", "Purchase register", "Credit and debit notes", "Checks and notes" })
                Assert.True(wb.Worksheets.Contains(name), "missing sheet " + name);

            Assert.Equal(r.B2B.Count, wb.Worksheet("GSTR1 B2B").RowsUsed().Count() - 1);        // minus the header
            Assert.Equal(r.SalesRegister.Count, wb.Worksheet("Sales register").RowsUsed().Count() - 1);
            Assert.Equal(r.Warnings.Count, wb.Worksheet("Checks and notes").RowsUsed().Count() - 1);

            // money is stored as a number, not text, and equals the report in rupees
            var b2b = wb.Worksheet("GSTR1 B2B");
            Assert.Equal(XLDataType.Number, b2b.Cell(2, 8).DataType);
            Assert.Equal(r.B2B.Sum(x => x.TaxablePaise) / 100m, b2b.Range(2, 8, 1 + r.B2B.Count, 8).CellsUsed().Sum(c => c.GetValue<decimal>()));

            var summary = wb.Worksheet("Summary 3B");
            var line = summary.RowsUsed().First(x => x.Cell(1).GetString().StartsWith("(a) Taxable supplies"));
            Assert.Equal(r.Gstr3b.OutTaxablePaise / 100m, line.Cell(2).GetValue<decimal>());
            Assert.Equal(r.Gstr3b.OutCgstPaise / 100m, line.Cell(4).GetValue<decimal>());
            Assert.Contains("Nothing here has been filed", summary.Cell(4, 1).GetString());
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    [Fact]
    public async Task Another_month_is_empty_and_bad_months_are_refused()
    {
        using var ctx = (await BuildAsync()).App;
        var next = DateTime.Today.AddMonths(1);
        var r = await ctx.Get<IGstReportService>().BuildAsync(next.Year, next.Month);
        Assert.Empty(r.B2B); Assert.Empty(r.B2Cl); Assert.Empty(r.B2Cs); Assert.Empty(r.Hsn); Assert.Empty(r.SalesRegister);
        Assert.Equal(0, r.Gstr3b.OutTaxablePaise);
        await Assert.ThrowsAsync<ValidationException>(() => ctx.Get<IGstReportService>().BuildAsync(2026, 13));
        await Assert.ThrowsAsync<ValidationException>(() => ctx.Get<IGstReportService>().BuildAsync(1999, 5));
    }

    [Fact]
    public async Task Only_the_admin_can_prepare_gst_reports()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => ReportAsync(app));
    }

    [Fact]
    public async Task A_composition_dealer_gets_turnover_not_gstr1_and_3b_tax()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync(composition: true);
        var (saleId, lineId) = await Helpers.SellAsync(app, pid, 3000, 35400);
        await app.Get<ISalesReturnService>().ReturnAsync(Helpers.Ret(saleId, lineId, 1000));
        var r = await ReportAsync(app);
        Assert.True(r.IsComposition);
        Assert.Equal(35400 - 11800, r.CompositionTurnoverPaise);
        Assert.Empty(r.B2B); Assert.Empty(r.B2Cs);
        Assert.Equal(0, r.Gstr3b.OutTaxPaise);
        Assert.Contains(r.Warnings, w => w.Contains("composition"));
    }

    [Fact]
    public async Task The_buyer_state_is_kept_on_the_bill_and_a_customer_state_must_match_the_gstin()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var parties = app.Get<IPartyService>();
        var p = await parties.SaveAsync(new PartyDto(0, "Gstin Only", null, "29AAAAA2222A1Z5", null, 0));
        Assert.Equal("29", p.StateCode);                                                      // filled in from the GSTIN
        await Assert.ThrowsAsync<ValidationException>(() => parties.SaveAsync(new PartyDto(0, "Mismatch", null, "29AAAAA2222A1Z5", "33", 0)));

        var cmd = new CompleteSaleCommand(Guid.NewGuid(), new[] { new SaleLineInput(pid, 1000) }, new[] { new PaymentInput(PaymentMode.Cash, 11800) },
            null, "Direct GSTIN", "29AAAAA2222A1Z5", null);
        var r = await app.Get<ISalesService>().CompleteSaleAsync(cmd);
        var d = (await app.Get<ISalesService>().GetAsync(r.SaleId))!;
        Assert.True(d.IsInterState);
        Assert.Equal(1800, d.IgstPaise);

        var local = await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800));
        Assert.Equal("33", (await Helpers.RegisterAsync(app))[local.InvoiceNumber]);
        Assert.Equal("29", (await Helpers.RegisterAsync(app))[r.InvoiceNumber]);
    }

    [Fact]
    public async Task Bills_made_before_place_of_supply_was_recorded_still_report_correctly_after_upgrade()
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
            cmd.CommandText = "ALTER TABLE Sales DROP COLUMN PlaceOfSupply; DELETE FROM SchemaVersions WHERE Version = 3;";   // what release 2 produced
            cmd.ExecuteNonQuery();
        }
        app1.Services.Dispose();

        using var app2 = TestApp.Create(dir);
        await app2.SignInAsAdminAsync();
        var r = await ReportAsync(app2);                                                       // upgrade added the column, old bill has none
        Assert.Equal(10000, r.Gstr3b.OutTaxablePaise);
        Assert.Equal("33", r.SalesRegister.Single().PlaceOfSupply);                            // falls back to the business state
        // new bills record it
        await app2.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800));
        Assert.Equal(2, (await ReportAsync(app2)).SalesRegister.Count);
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
