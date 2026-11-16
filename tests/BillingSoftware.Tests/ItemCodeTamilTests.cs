using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BillingSoftware.Tests;

public class ItemCodeTamilTests : IDisposable
{
    private readonly List<string> _files = new();
    public void Dispose() { SqliteConnection.ClearAllPools(); foreach (var f in _files) { try { File.Delete(f); } catch (IOException) { } } }

    private const string Soap = "லக்ஸ் சோப்பு 100கி";

    private static ProductDto Item(string name, string sku = "", string? barcode = null, string? tamil = null, long id = 0) =>
        new(id, sku, barcode, name, null, 1800, "pcs", 10_000, null, true, 0, 0, true, tamil);

    // ------------------------------------------------------------------ optional item code

    [Fact]
    public async Task An_item_without_a_code_gets_the_next_free_one()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();                                                           // SKU1 exists
        var cat = app.Get<ICatalogService>();
        var a = await cat.SaveAsync(Item("Pen"));
        var b = await cat.SaveAsync(Item("Pencil"));
        Assert.Equal("ITEM-00001", a.Sku);
        Assert.Equal("ITEM-00002", b.Sku);
        Assert.Equal(3, (await cat.ListAsync(0, 10, true)).Count);
    }

    [Fact]
    public async Task A_made_code_never_clashes_with_one_somebody_typed_or_a_barcode()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var cat = app.Get<ICatalogService>();
        await cat.SaveAsync(Item("Typed", sku: "ITEM-00005"));
        Assert.Equal("ITEM-00006", (await cat.SaveAsync(Item("Next"))).Sku);                    // continues after the highest, not into a gap
        await cat.SaveAsync(Item("Odd barcode", sku: "ZZZ", barcode: "ITEM-00007"));            // a barcode that looks like a code
        Assert.Equal("ITEM-00008", (await cat.SaveAsync(Item("After"))).Sku);
    }

    [Fact]
    public async Task Saving_an_existing_item_with_the_code_left_empty_keeps_its_code()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var cat = app.Get<ICatalogService>();
        var saved = await cat.SaveAsync(Item("Pen"));
        var again = await cat.SaveAsync(Item("Pen renamed", sku: "", id: saved.Id));
        Assert.Equal(saved.Sku, again.Sku);
        Assert.Equal("Pen renamed", again.Name);
        await Assert.ThrowsAsync<ValidationException>(() => cat.SaveAsync(Item("Ghost", sku: "", id: 99999)));
    }

    [Fact]
    public async Task A_code_that_is_typed_must_still_be_unique_and_can_be_changed()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var cat = app.Get<ICatalogService>();
        await Assert.ThrowsAsync<ValidationException>(() => cat.SaveAsync(Item("Dup", sku: "SKU1")));
        var pen = await cat.SaveAsync(Item("Pen"));
        var changed = await cat.SaveAsync(Item("Pen", sku: "PEN-1", id: pen.Id));
        Assert.Equal("PEN-1", changed.Sku);
    }

    [Fact]
    public async Task A_sale_can_be_made_for_an_item_whose_code_was_made_for_it()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var pen = await app.Get<ICatalogService>().SaveAsync(Item("Pen"));
        await app.Get<IStockService>().ReceiveOpeningStockAsync(pen.Id, 10_000, 5_000);
        var scan = await app.Get<IBarcodeService>().ScanAsync(pen.Sku);                            // the made code works like any code
        Assert.Equal(pen.Id, scan!.Product.Id);
        await Helpers.SellAsync(app, pen.Id, 1000, 10_000);
        await Helpers.AssertBooksBalancedAsync(app);
    }

    // ------------------------------------------------------------------ Tamil name

    [Fact]
    public async Task A_Tamil_name_is_saved_trimmed_and_returned_and_blank_means_none()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var cat = app.Get<ICatalogService>();
        var saved = await cat.SaveAsync(Item("Lux Soap", tamil: "  " + Soap + "  "));
        Assert.Equal(Soap, saved.TamilName);
        Assert.Equal(Soap, (await cat.GetAsync(saved.Id))!.TamilName);
        Assert.Null((await cat.SaveAsync(Item("Lux Soap", id: saved.Id, tamil: "   "))).TamilName);
        await Assert.ThrowsAsync<ValidationException>(() => cat.SaveAsync(Item("Long", tamil: new string('த', 121))));
    }

    [Fact]
    public async Task An_item_can_be_found_by_typing_its_Tamil_name()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var cat = app.Get<ICatalogService>();
        var soap = await cat.SaveAsync(Item("Lux Soap", tamil: Soap));
        await cat.SaveAsync(Item("Salt", tamil: "உப்பு"));
        Assert.Equal(soap.Id, (await cat.SearchAsync("சோப்பு")).Single().Id);
        Assert.Equal(soap.Id, (await cat.SearchAsync("Lux")).Single().Id);
        Assert.Empty(await cat.SearchAsync("தண்ணீர்"));
    }

    [Fact]
    public async Task A_bill_keeps_the_Tamil_name_it_had_on_the_day()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var cat = app.Get<ICatalogService>();
        var soap = await cat.SaveAsync(Item("Lux Soap", tamil: Soap));
        await app.Get<IStockService>().ReceiveOpeningStockAsync(soap.Id, 10_000, 5_000);
        var plain = await cat.SaveAsync(Item("Plain pen"));
        await app.Get<IStockService>().ReceiveOpeningStockAsync(plain.Id, 10_000, 5_000);

        var s1 = await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(soap.Id, 1000, 10_000));
        var s2 = await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(plain.Id, 1000, 10_000));
        await cat.SaveAsync(Item("Lux Soap", id: soap.Id, tamil: "வேறு பெயர்"));                      // renamed afterwards

        Assert.Equal(Soap, (await app.Get<ISalesService>().GetAsync(s1.SaleId))!.Lines.Single().TamilName);
        Assert.Null((await app.Get<ISalesService>().GetAsync(s2.SaleId))!.Lines.Single().TamilName);
        Assert.Equal(Soap, (await app.Get<ISalesService>().GetForReprintAsync(s1.SaleId))!.Lines.Single().TamilName);
    }

    [Fact]
    public async Task The_import_reads_a_Tamil_name_column_and_does_not_need_a_code()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var path = Path.Combine(Path.GetTempPath(), $"tamil-{Guid.NewGuid():N}.csv");
        _files.Add(path);
        File.WriteAllText(path, "Name,Tamil name,Price\nLux Soap," + Soap + ",45\nPlain pen,,10\n", new System.Text.UTF8Encoding(true));
        var r = await app.Get<IImportService>().ImportAsync(ImportKind.Items, path, null);
        Assert.Equal(2, r.Imported);
        Assert.Equal(Soap, (await app.Get<ICatalogService>().SearchAsync("Lux")).Single().TamilName);
        Assert.Null((await app.Get<ICatalogService>().SearchAsync("Plain pen")).Single().TamilName);

        var template = Path.Combine(Path.GetTempPath(), $"tpl-{Guid.NewGuid():N}.xlsx");
        _files.Add(template);
        await app.Get<IImportService>().WriteTemplateAsync(ImportKind.Items, template);
        Assert.Empty((await app.Get<IImportService>().PreviewAsync(ImportKind.Items, template)).IgnoredColumns);
    }

    // ------------------------------------------------------------------ upgrade

    [Fact]
    public async Task A_database_from_the_previous_release_gets_the_Tamil_columns()
    {
        var app1 = TestApp.Create();
        var soap = await app1.SeedAsync();
        await app1.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(soap, 1000, 11_800));
        var dir = app1.Dir;
        var factory = app1.Get<DbFactory>();
        var dbPath = app1.Paths.DbPath;
        SqliteConnection.ClearAllPools();
        using (var raw = factory.OpenRaw(dbPath))
        using (var cmd = raw.CreateCommand())
        {
            cmd.CommandText = "ALTER TABLE Products DROP COLUMN TamilName; ALTER TABLE SaleLines DROP COLUMN TamilName; DELETE FROM SchemaVersions WHERE Version = 12;";
            cmd.ExecuteNonQuery();
        }
        app1.Services.Dispose();

        using var app2 = TestApp.Create(dir);
        await app2.SignInAsAdminAsync();
        var item = (await app2.Get<ICatalogService>().SearchAsync("SKU1")).Single();
        Assert.Null(item.TamilName);
        var saved = await app2.Get<ICatalogService>().SaveAsync(item with { TamilName = Soap });
        Assert.Equal(Soap, saved.TamilName);
        var bill = (await app2.Get<ISalesService>().ListAsync(Helpers.Today)).First();
        Assert.Null((await app2.Get<ISalesService>().GetAsync(bill.Id))!.Lines.Single().TamilName);
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
