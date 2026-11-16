using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BillingSoftware.Tests;

public class BarcodeTests
{
    private const string ValidEan = "4006381333931";

    private static async Task<long> ItemAsync(TestApp app, string sku, string name, string unit = "pcs", string? barcode = null, long price = 10_000, bool active = true)
        => (await app.Get<ICatalogService>().SaveAsync(new ProductDto(0, sku, barcode, name, null, 0, unit, price, null, true, 0, 0, active))).Id;

    private static async Task<ProductDto> GetAsync(TestApp app, long id) => (await app.Get<ICatalogService>().GetAsync(id))!;

    private static async Task<long> InventoryBooksAsync(TestApp app) =>
        (await app.Get<IReportService>().TrialBalanceAsync(Helpers.Today)).Rows.Where(r => r.Code == AccountCodes.Inventory).Sum(r => r.DebitPaise - r.CreditPaise);

    private static async Task<long> StockListValueAsync(TestApp app)
    {
        var all = await app.Get<ICatalogService>().ListAsync(0, 500, true);
        return all.Sum(p => Costing.Value(p.QtyOnHandMilli, p.AvgCostPaise));
    }

    // ------------------------------------------------------------------ EAN-13

    [Fact]
    public void Check_digits_and_validity_follow_the_EAN13_rule()
    {
        Assert.Equal(1, Ean13.CheckDigit("400638133393"));
        Assert.True(Ean13.IsValid(ValidEan));
        Assert.False(Ean13.IsValid("4006381333932"));
        Assert.False(Ean13.IsValid("400638133393"));          // twelve digits
        Assert.False(Ean13.IsValid("40063813339A1"));
        Assert.False(Ean13.IsValid(null));
        Assert.True(Ean13.IsValid("8901234567890"));
        Assert.True(Ean13.IsValid("5901234123457"));
    }

    [Fact]
    public void Shop_made_codes_are_valid_in_the_reserved_range_and_carry_their_number()
    {
        var one = Ean13.FromSequence(1);
        Assert.Equal(13, one.Length);
        Assert.StartsWith("29", one);
        Assert.True(Ean13.IsValid(one));
        Assert.Equal(1, Ean13.SequenceOf(one));
        Assert.Equal(1234567, Ean13.SequenceOf(Ean13.FromSequence(1234567)));
        Assert.Null(Ean13.SequenceOf(ValidEan));                  // a manufacturer's code has no shop number
        Assert.Throws<ArgumentOutOfRangeException>(() => Ean13.FromSequence(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ean13.FromSequence(10_000_000_000));
        Assert.Equal(Enumerable.Range(1, 500).Count(), Enumerable.Range(1, 500).Select(n => Ean13.FromSequence(n)).Distinct().Count());
    }

    [Fact]
    public void The_bar_pattern_has_the_right_structure()
    {
        var bits = Ean13.Pattern("5901234123457");
        Assert.Equal(95, bits.Length);
        string S(int from, int len) => new(bits.Skip(from).Take(len).Select(b => b ? '1' : '0').ToArray());
        Assert.Equal("101", S(0, 3));                // start guard
        Assert.Equal("01010", S(45, 5));             // centre guard
        Assert.Equal("101", S(92, 3));               // end guard
        Assert.Equal("0001011", S(3, 7));            // 9 coded with the L set (first digit 5 = LGGLLG)
        Assert.Equal("0100111", S(10, 7));           // 0 coded with the G set
        Assert.Equal("1100110", S(50, 7));           // 1 on the right-hand side
        Assert.Throws<ArgumentException>(() => Ean13.Pattern("5901234123458"));
        // the left half starts with a dark-to-light edge in every code and the two halves always have seven modules per digit
        foreach (var code in new[] { "4006381333931", "8901234567890", Ean13.FromSequence(77) })
            Assert.Equal(95, Ean13.Pattern(code).Length);
    }

    // ------------------------------------------------------------------ extra barcodes and scanning

    [Fact]
    public async Task Scanning_finds_an_item_by_its_barcode_its_code_or_a_pack_barcode_with_the_right_quantity()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();                                       // barcode 8901234567890, SKU1
        var bc = app.Get<IBarcodeService>();
        var pack = await bc.AddAsync(soap, ValidEan, 12_000, "Carton of 12");

        var own = await bc.ScanAsync("8901234567890");
        Assert.NotNull(own); Assert.Equal(soap, own!.Product.Id); Assert.Equal(Qty.Unit, own.QtyMilli); Assert.False(own.IsPack);
        var bySku = await bc.ScanAsync("SKU1");
        Assert.Equal(Qty.Unit, bySku!.QtyMilli);
        var carton = await bc.ScanAsync(ValidEan);
        Assert.Equal(12_000, carton!.QtyMilli); Assert.True(carton.IsPack); Assert.Equal(soap, carton.Product.Id);
        Assert.Null(await bc.ScanAsync("999999999"));
        Assert.Null(await bc.ScanAsync("   "));

        // searching also finds the item by a pack barcode
        Assert.Contains(await app.Get<ICatalogService>().SearchAsync(ValidEan), p => p.Id == soap);

        await bc.RemoveAsync(pack.Id);
        Assert.Null(await bc.ScanAsync(ValidEan));
    }

    [Fact]
    public async Task A_pack_barcode_of_an_inactive_item_finds_nothing()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var bc = app.Get<IBarcodeService>();
        await bc.AddAsync(soap, ValidEan, 6_000, null);
        var p = await GetAsync(app, soap);
        await app.Get<ICatalogService>().SaveAsync(p with { IsActive = false });
        Assert.Null(await bc.ScanAsync(ValidEan));
        Assert.Null(await bc.ScanAsync("8901234567890"));
    }

    [Fact]
    public async Task Cashiers_can_scan_but_never_see_cost_or_manage_barcodes()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        await app.AddCashierAsync();
        var bc = app.Get<IBarcodeService>();
        var pack = await bc.AddAsync(soap, ValidEan, 6_000, null);
        await app.SignInAsCashierAsync();
        var scan = await bc.ScanAsync(ValidEan);
        Assert.Equal(6_000, scan!.QtyMilli);
        Assert.Equal(0, scan.Product.AvgCostPaise);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => bc.AddAsync(soap, "ABC123", 1_000, null));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => bc.ListAsync(soap));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => bc.RemoveAsync(pack.Id));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => bc.GenerateAsync());
    }

    [Fact]
    public async Task A_barcode_can_belong_to_only_one_item_or_pack()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();                                       // 8901234567890 / SKU1
        var rice = await ItemAsync(app, "RICE1", "Rice", "kg", price: 6_000);
        var bc = app.Get<IBarcodeService>();
        var cat = app.Get<ICatalogService>();

        await bc.AddAsync(soap, ValidEan, 6_000, null);
        await Assert.ThrowsAsync<ValidationException>(() => bc.AddAsync(rice, ValidEan, 1_000, null));            // used by a pack
        await Assert.ThrowsAsync<ValidationException>(() => bc.AddAsync(soap, ValidEan, 1_000, null));            // used by this item's own pack
        await Assert.ThrowsAsync<ValidationException>(() => bc.AddAsync(rice, "8901234567890", 1_000, null));     // used as an item barcode
        await Assert.ThrowsAsync<ValidationException>(() => bc.AddAsync(rice, "SKU1", 1_000, null));              // used as an item code
        // an item cannot take a barcode or code that a pack already uses
        var rp = await GetAsync(app, rice);
        await Assert.ThrowsAsync<ValidationException>(() => cat.SaveAsync(rp with { Barcode = ValidEan }));
        await Assert.ThrowsAsync<ValidationException>(() => cat.SaveAsync(rp with { Sku = ValidEan }));
        // an item may keep using what is already its own
        var sp = await GetAsync(app, soap);
        await cat.SaveAsync(sp with { Name = "Test Soap Renamed" });
    }

    [Fact]
    public async Task Typed_barcodes_are_checked()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var bc = app.Get<IBarcodeService>();
        await Assert.ThrowsAsync<ValidationException>(() => bc.AddAsync(soap, "ab", 1_000, null));                // too short
        await Assert.ThrowsAsync<ValidationException>(() => bc.AddAsync(soap, new string('9', 41), 1_000, null)); // too long
        await Assert.ThrowsAsync<ValidationException>(() => bc.AddAsync(soap, "AB 123", 1_000, null));            // a space
        await Assert.ThrowsAsync<ValidationException>(() => bc.AddAsync(soap, "4006381333932", 1_000, null));     // 13 digits, wrong check digit
        await Assert.ThrowsAsync<ValidationException>(() => bc.AddAsync(soap, "ABC123", 0, null));                // zero quantity
        await Assert.ThrowsAsync<ValidationException>(() => bc.AddAsync(soap, "ABC123", -5, null));
        await Assert.ThrowsAsync<ValidationException>(() => bc.AddAsync(soap, "ABC123", 2_000_000_000, null));
        await Assert.ThrowsAsync<ValidationException>(() => bc.AddAsync(soap, "ABC123", 1_000, new string('x', 81)));
        await Assert.ThrowsAsync<ValidationException>(() => bc.AddAsync(99999, "ABC123", 1_000, null));           // no such item
        await bc.AddAsync(soap, "ABC123", 1_000, " loose ");                                                      // a short code of letters is fine
        Assert.Equal("loose", (await bc.ListAsync(soap)).Single().Note);

        // the same rules apply when typing an item's own barcode
        var p = await GetAsync(app, soap);
        await Assert.ThrowsAsync<ValidationException>(() => app.Get<ICatalogService>().SaveAsync(p with { Barcode = "4006381333932" }));
    }

    [Fact]
    public async Task An_item_saved_earlier_with_an_odd_code_can_still_be_edited_but_a_new_odd_code_is_refused()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        await using (var db = app.Get<DbFactory>().Create())
        {
            var p = db.Products.Single(x => x.Id == soap);
            p.Barcode = "1234567890123";                      // 13 digits, wrong check digit: what an older release may have stored
            await db.SaveChangesAsync();
        }
        var cat = app.Get<ICatalogService>();
        var item = await GetAsync(app, soap);
        var renamed = await cat.SaveAsync(item with { Name = "Renamed soap", SellingPricePaise = 12_000 });
        Assert.Equal("1234567890123", renamed.Barcode);
        await Assert.ThrowsAsync<ValidationException>(() => cat.SaveAsync(renamed with { Barcode = "1234567890124" }));
    }

    [Fact]
    public async Task An_item_can_have_at_most_twenty_extra_barcodes()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var bc = app.Get<IBarcodeService>();
        for (var i = 0; i < 20; i++) await bc.AddAsync(soap, "PK" + i, 1_000 + i, null);
        await Assert.ThrowsAsync<BusinessRuleException>(() => bc.AddAsync(soap, "PK20", 1_000, null));
        Assert.Equal(20, (await bc.ListAsync(soap)).Count);
    }

    [Fact]
    public async Task Generated_codes_are_valid_unique_and_count_pack_barcodes_too()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var rice = await ItemAsync(app, "RICE1", "Rice", "kg");
        var oil = await ItemAsync(app, "OIL1", "Oil", "l");
        var bc = app.Get<IBarcodeService>();
        var cat = app.Get<ICatalogService>();

        var first = await bc.GenerateAsync();
        Assert.Equal(Ean13.FromSequence(1), first);
        Assert.True(Ean13.IsValid(first));
        await cat.SaveAsync((await GetAsync(app, rice)) with { Barcode = first });

        var second = await bc.GenerateAsync();
        Assert.Equal(Ean13.FromSequence(2), second);
        await bc.AddAsync(oil, second, 500, "half litre");                               // a pack barcode uses a number as well
        Assert.Equal(Ean13.FromSequence(3), await bc.GenerateAsync());

        // a scanned manufacturer's code does not disturb the numbering
        await bc.AddAsync(soap, ValidEan, 6_000, null);
        Assert.Equal(Ean13.FromSequence(3), await bc.GenerateAsync());
    }

    [Fact]
    public async Task Barcode_changes_are_audited()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var bc = app.Get<IBarcodeService>();
        var b = await bc.AddAsync(soap, ValidEan, 6_000, null);
        await bc.RemoveAsync(b.Id);
        await Assert.ThrowsAsync<ValidationException>(() => bc.RemoveAsync(b.Id));
        await using var db = app.Get<DbFactory>().Create();
        var actions = db.AuditEvents.Select(e => e.Action).ToList();
        Assert.Contains("barcode.added", actions);
        Assert.Contains("barcode.removed", actions);
        Assert.True((await app.Get<IAuditService>().VerifyChainAsync()).Valid);
    }

    [Fact]
    public async Task A_bill_for_a_pack_quantity_prices_that_quantity_of_the_item()
    {
        using var app = TestApp.Create();
        var rice = await app.SeedAsync();                                       // 118.00 per piece including 18% GST
        var scan = await app.Get<IBarcodeService>().AddAsync(rice, ValidEan, 500, "Half pack");
        var s = await app.Get<IBarcodeService>().ScanAsync(ValidEan);
        var sale = await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(rice, s!.QtyMilli, 5_900));
        Assert.True(sale.TotalPaise == 5_900);
        Assert.Equal(9_500, (await GetAsync(app, rice)).QtyOnHandMilli);
        Assert.Equal(scan.Code, ValidEan);
        await Helpers.AssertBooksBalancedAsync(app);
    }

    // ------------------------------------------------------------------ repacking

    private static async Task<(long Bulk, long Pack)> RiceAsync(TestApp app, long kgMilli = 25_000, long costPerKg = 6_000)
    {
        var bulk = await ItemAsync(app, "RICE-BULK", "Rice (loose)", "kg", price: 8_000);
        var pack = await ItemAsync(app, "RICE-250", "Rice 250 g pack", "pcs", price: 2_500);
        await app.Get<IStockService>().ReceiveOpeningStockAsync(bulk, kgMilli, costPerKg);
        return (bulk, pack);
    }

    [Fact]
    public async Task Repacking_moves_stock_and_cost_to_the_pack_item_without_profit_or_loss()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var (bulk, pack) = await RiceAsync(app);                                 // 25 kg at 60.00 = 1,500.00
        var booksBefore = await InventoryBooksAsync(app);
        var profitBefore = (await app.Get<IProfitLossService>().BuildAsync(Helpers.Today.AddDays(-30), Helpers.Today)).Current.NetProfitPaise;

        var rp = app.Get<IRepackService>();
        var preview = await rp.PreviewAsync(new RepackCommand(Guid.NewGuid(), bulk, 25_000, pack, 100_000, null));
        Assert.Equal(150_000, preview.SourceValuePaise);
        Assert.Equal(1_500, preview.TargetUnitCostPaise);                          // 15.00 a pack

        var r = await rp.RepackAsync(new RepackCommand(Guid.NewGuid(), bulk, 25_000, pack, 100_000, "250 g packs"));
        Assert.False(r.AlreadyExisted);
        Assert.Equal(1_500, r.TargetUnitCostPaise);
        var b = await GetAsync(app, bulk); var p = await GetAsync(app, pack);
        Assert.Equal(0, b.QtyOnHandMilli);
        Assert.Equal(100_000, p.QtyOnHandMilli);
        Assert.Equal(1_500, p.AvgCostPaise);

        Assert.Equal(booksBefore, await InventoryBooksAsync(app));               // the inventory account did not move
        Assert.Equal(profitBefore, (await app.Get<IProfitLossService>().BuildAsync(Helpers.Today.AddDays(-30), Helpers.Today)).Current.NetProfitPaise);
        var checks = (await app.Get<IBalanceSheetService>().BuildAsync(Helpers.Today, null)).Checks;
        Assert.All(checks, c => Assert.True(c.Ok, c.Message));
        await Helpers.AssertBooksBalancedAsync(app);

        var list = await rp.ListAsync();
        Assert.Single(list);
        Assert.Equal("Rice (loose)", list[0].Source);
        Assert.Equal(100_000, list[0].TargetQtyMilli);
    }

    [Fact]
    public async Task A_part_repack_leaves_the_rest_of_the_bulk_stock_at_its_cost()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var (bulk, pack) = await RiceAsync(app);
        await app.Get<IRepackService>().RepackAsync(new RepackCommand(Guid.NewGuid(), bulk, 10_000, pack, 40_000, null));
        var b = await GetAsync(app, bulk);
        Assert.Equal(15_000, b.QtyOnHandMilli);
        Assert.Equal(6_000, b.AvgCostPaise);
        Assert.Equal(1_500, (await GetAsync(app, pack)).AvgCostPaise);
        Assert.Equal(await StockListValueAsync(app), await InventoryBooksAsync(app));
    }

    [Fact]
    public async Task Packs_that_do_not_divide_evenly_keep_the_books_exact_through_a_rounding_entry()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();                                       // 10 soaps at 60.00 in the seed
        var bulk = await ItemAsync(app, "BULK", "Bulk item", "pcs");
        var pack = await ItemAsync(app, "PACK3", "Pack", "pcs");
        await app.Get<IStockService>().ReceiveOpeningStockAsync(bulk, 1_000, 10_000);          // one unit at 100.00
        var booksBefore = await InventoryBooksAsync(app);

        var r = await app.Get<IRepackService>().RepackAsync(new RepackCommand(Guid.NewGuid(), bulk, 1_000, pack, 3_000, null));
        Assert.Equal(3_333, r.TargetUnitCostPaise);                                // 100.00 / 3 = 33.33
        Assert.Equal(booksBefore - 1, await InventoryBooksAsync(app));            // one paisa of rounding goes to cost variance
        var variance = (await app.Get<IReportService>().TrialBalanceAsync(Helpers.Today)).Rows.Where(x => x.Code == AccountCodes.InventoryCostVariance).Sum(x => x.DebitPaise - x.CreditPaise);
        Assert.Equal(1, variance);
        Assert.Equal(await StockListValueAsync(app), await InventoryBooksAsync(app));
        await Helpers.AssertBooksBalancedAsync(app);
        _ = soap;
    }

    [Fact]
    public async Task Repacking_back_again_returns_the_stock()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var (bulk, pack) = await RiceAsync(app);
        var rp = app.Get<IRepackService>();
        await rp.RepackAsync(new RepackCommand(Guid.NewGuid(), bulk, 25_000, pack, 100_000, null));
        await rp.RepackAsync(new RepackCommand(Guid.NewGuid(), pack, 100_000, bulk, 25_000, "Undo"));
        var b = await GetAsync(app, bulk);
        Assert.Equal(25_000, b.QtyOnHandMilli);
        Assert.Equal(6_000, b.AvgCostPaise);
        Assert.Equal(0, (await GetAsync(app, pack)).QtyOnHandMilli);
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task A_repack_then_sales_show_the_profit_on_the_packs()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var (bulk, pack) = await RiceAsync(app);
        await app.Get<IRepackService>().RepackAsync(new RepackCommand(Guid.NewGuid(), bulk, 25_000, pack, 100_000, null));
        await Helpers.SellAsync(app, pack, 10_000, 25_000);                         // ten packs at 25.00, cost 15.00 each
        var pnl = (await app.Get<IProfitLossService>().BuildAsync(Helpers.Today, Helpers.Today)).Current;
        Assert.Equal(15_000, (await GetAsync(app, pack)).AvgCostPaise * 10);
        Assert.Equal(25_000 - 15_000, pnl.GrossProfitPaise);
        Assert.Equal(90_000, (await GetAsync(app, pack)).QtyOnHandMilli);
    }

    [Fact]
    public async Task Repacking_into_an_item_that_was_already_sold_short_posts_the_cost_difference()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var (bulk, pack) = await RiceAsync(app);
        // two packs were sold before any existed (negative stock, booked at the last known cost of zero)
        await Helpers.SellAsync(app, pack, 2_000, 5_000);
        Assert.Equal(-2_000, (await GetAsync(app, pack)).QtyOnHandMilli);
        await app.Get<IRepackService>().RepackAsync(new RepackCommand(Guid.NewGuid(), bulk, 25_000, pack, 100_000, null));
        Assert.Equal(98_000, (await GetAsync(app, pack)).QtyOnHandMilli);
        var variance = (await app.Get<IReportService>().TrialBalanceAsync(Helpers.Today)).Rows.Where(x => x.Code == AccountCodes.InventoryCostVariance).Sum(x => x.DebitPaise - x.CreditPaise);
        Assert.Equal(3_000, variance);                                              // the two packs that were sold cost 15.00 each, not 0
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Bad_repacks_are_refused_with_plain_messages()
    {
        using var app = TestApp.Create();
        var soap = await app.SeedAsync();
        var (bulk, pack) = await RiceAsync(app);
        var rp = app.Get<IRepackService>();
        RepackCommand Cmd(long s, long sq, long t, long tq) => new(Guid.NewGuid(), s, sq, t, tq, null);

        await Assert.ThrowsAsync<ValidationException>(() => rp.RepackAsync(Cmd(bulk, 0, pack, 1_000)));
        await Assert.ThrowsAsync<ValidationException>(() => rp.RepackAsync(Cmd(bulk, 1_000, pack, 0)));
        await Assert.ThrowsAsync<ValidationException>(() => rp.RepackAsync(Cmd(bulk, -1_000, pack, 1_000)));
        await Assert.ThrowsAsync<ValidationException>(() => rp.RepackAsync(Cmd(bulk, 1_000, bulk, 1_000)));          // same item
        var tooMuch = await Assert.ThrowsAsync<ValidationException>(() => rp.RepackAsync(Cmd(bulk, 26_000, pack, 100_000)));
        Assert.Contains("in stock", tooMuch.Message);
        await Assert.ThrowsAsync<ValidationException>(() => rp.RepackAsync(Cmd(bulk, 1_000, 99999, 1_000)));         // no such item
        await Assert.ThrowsAsync<ValidationException>(() => rp.RepackAsync(Cmd(bulk, 1, pack, 1_000_000)));           // would cost nothing each
        await Assert.ThrowsAsync<ValidationException>(() => rp.RepackAsync(Cmd(bulk, 1_000, pack, 2_000_000_000_000)));
        await Assert.ThrowsAsync<ValidationException>(() => rp.RepackAsync(new RepackCommand(Guid.NewGuid(), bulk, 1_000, pack, 4_000, new string('x', 121))));

        // an item with no cost recorded cannot be repacked, because the packs would have no cost
        var free = await ItemAsync(app, "FREE", "Free sample", "kg");
        await app.Get<IStockService>().ReceiveOpeningStockAsync(free, 5_000, 0);
        var nocost = await Assert.ThrowsAsync<ValidationException>(() => rp.RepackAsync(Cmd(free, 1_000, pack, 4_000)));
        Assert.Contains("no cost", nocost.Message);

        // an inactive pack item is refused
        var dead = await ItemAsync(app, "DEAD", "Discontinued pack", active: false);
        await Assert.ThrowsAsync<ValidationException>(() => rp.RepackAsync(Cmd(bulk, 1_000, dead, 4_000)));

        Assert.Empty(await rp.ListAsync());
        Assert.Equal(25_000, (await GetAsync(app, bulk)).QtyOnHandMilli);
        _ = soap;
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task Repacking_is_admin_only_idempotent_audited_and_respects_the_locked_period()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.AddCashierAsync();
        var (bulk, pack) = await RiceAsync(app);
        var rp = app.Get<IRepackService>();
        var cmd = new RepackCommand(Guid.NewGuid(), bulk, 5_000, pack, 20_000, null);

        var a = await rp.RepackAsync(cmd);
        var b = await rp.RepackAsync(cmd);                                           // the same command again
        Assert.True(b.AlreadyExisted); Assert.Equal(a.Id, b.Id);
        Assert.Equal(20_000, (await GetAsync(app, pack)).QtyOnHandMilli);            // moved once only
        Assert.Single(await rp.ListAsync());

        await using (var db = app.Get<DbFactory>().Create())
            Assert.Contains("stock.repacked", db.AuditEvents.Select(e => e.Action).ToList());

        await app.Get<ISettingsService>().SetAsync(SettingKeys.LockedThrough, Helpers.Today.ToString("yyyy-MM-dd"));
        await Assert.ThrowsAsync<BusinessRuleException>(() => rp.RepackAsync(new RepackCommand(Guid.NewGuid(), bulk, 1_000, pack, 4_000, null)));
        await app.Get<ISettingsService>().SetAsync(SettingKeys.LockedThrough, Helpers.Today.AddDays(-5).ToString("yyyy-MM-dd"));

        await app.SignInAsCashierAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => rp.RepackAsync(new RepackCommand(Guid.NewGuid(), bulk, 1_000, pack, 4_000, null)));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => rp.PreviewAsync(new RepackCommand(Guid.NewGuid(), bulk, 1_000, pack, 4_000, null)));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => rp.ListAsync());
    }

    // ------------------------------------------------------------------ upgrade

    [Fact]
    public async Task A_database_from_the_previous_release_gets_the_barcode_and_repack_tables()
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
            cmd.CommandText = "DROP TABLE ProductBarcodes; DROP TABLE Repacks; DELETE FROM SchemaVersions WHERE Version = 7;";
            cmd.ExecuteNonQuery();
        }
        app1.Services.Dispose();

        using var app2 = TestApp.Create(dir);
        await app2.SignInAsAdminAsync();
        var soap = (await app2.Get<ICatalogService>().SearchAsync("SKU1")).Single().Id;
        var b = await app2.Get<IBarcodeService>().AddAsync(soap, ValidEan, 6_000, null);
        Assert.Equal(6_000, (await app2.Get<IBarcodeService>().ScanAsync(ValidEan))!.QtyMilli);
        Assert.True(b.Id > 0);
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
