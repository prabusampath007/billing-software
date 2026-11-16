using System.Text;
using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using ClosedXML.Excel;
using Xunit;

namespace BillingSoftware.Tests;

public class ImportTests : IDisposable
{
    private readonly List<string> _files = new();

    private string File_(string name, string content, Encoding? enc = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"import-{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, content, enc ?? new UTF8Encoding(false));
        _files.Add(path);
        return path;
    }

    public void Dispose() { foreach (var f in _files) { try { File.Delete(f); } catch (IOException) { } } }

    private static DateOnly Today => Helpers.Today;

    private const string ItemsCsv =
        "Name,Code,Barcode,HSN,GST %,Unit,Price,MRP,Price includes GST,Opening stock,Opening cost\n" +
        "Lux Soap,SOAP1,4006381333931,3401,18,pcs,45.00,45.00,Yes,50,28.00\n" +
        "Rice 1kg,,,1006,5%,kg,\"1,200.50\",,No,10,800\n" +
        "Bad price,B1,,,18,pcs,abc,,,,\n" +
        "Stock without cost,S1,,,18,pcs,10,,,5,\n" +
        "Same code as existing,SKU1,,,18,pcs,10,,,,\n" +
        "Repeated code,SOAP1,,,18,pcs,10,,,,\n" +
        "Bad EAN,E1,4006381333932,,18,pcs,10,,,,\n" +
        ",N1,,,18,pcs,10,,,,\n";

    // ------------------------------------------------------------------ items

    [Fact]
    public async Task An_items_file_is_checked_row_by_row_and_nothing_is_saved_by_the_preview()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();                                                    // SKU1 "Test Soap" already exists
        var imp = app.Get<IImportService>();
        var p = await imp.PreviewAsync(ImportKind.Items, File_("items.csv", ItemsCsv));

        Assert.Null(p.FileProblem);
        Assert.Equal(8, p.TotalRows);
        Assert.Equal(2, p.ReadyCount);
        Assert.Equal(2, p.DuplicateCount);
        Assert.Equal(4, p.ErrorCount);
        Assert.Equal(ImportRowStatus.Ready, p.Rows.Single(r => r.RowNumber == 2).Status);
        Assert.Equal(ImportRowStatus.Error, p.Rows.Single(r => r.RowNumber == 4).Status);       // price abc
        Assert.Contains("price", p.Rows.Single(r => r.RowNumber == 4).Detail);
        Assert.Contains("cost", p.Rows.Single(r => r.RowNumber == 5).Detail);
        Assert.Equal(ImportRowStatus.Duplicate, p.Rows.Single(r => r.RowNumber == 6).Status);   // SKU1 exists
        Assert.Equal(ImportRowStatus.Duplicate, p.Rows.Single(r => r.RowNumber == 7).Status);   // repeated in the file
        Assert.Contains("typing mistake", p.Rows.Single(r => r.RowNumber == 8).Detail);
        Assert.Contains("name", p.Rows.Single(r => r.RowNumber == 9).Detail);
        Assert.Single(await app.Get<ICatalogService>().ListAsync(0, 100, true));                // only the seeded item
    }

    [Fact]
    public async Task Importing_items_creates_them_with_their_opening_stock_and_keeps_the_books_balanced()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var inventoryBefore = await Helpers.NetAsync(app, AccountCodes.Inventory);
        var r = await app.Get<IImportService>().ImportAsync(ImportKind.Items, File_("items.csv", ItemsCsv), null);

        Assert.Equal(2, r.Imported);
        Assert.Equal(2, r.Duplicates);
        Assert.Equal(4, r.Errors);
        var all = await app.Get<ICatalogService>().ListAsync(0, 100, true);
        Assert.Equal(3, all.Count);

        var lux = all.Single(p => p.Sku == "SOAP1");
        Assert.Equal("4006381333931", lux.Barcode);
        Assert.Equal(4_500, lux.SellingPricePaise);
        Assert.Equal(1_800, lux.GstRateBp);
        Assert.True(lux.PriceInclusive);
        Assert.Equal(50_000, lux.QtyOnHandMilli);
        Assert.Equal(2_800, lux.AvgCostPaise);

        var rice = all.Single(p => p.Name == "Rice 1kg");
        Assert.StartsWith("ITEM-", rice.Sku);                                    // no code in the file: one was made
        Assert.Equal(120_050, rice.SellingPricePaise);
        Assert.Equal(500, rice.GstRateBp);
        Assert.False(rice.PriceInclusive);
        Assert.Equal("kg", rice.Unit);
        Assert.Equal(10_000, rice.QtyOnHandMilli);

        Assert.Equal(inventoryBefore + 50 * 2_800 + 10 * 80_000, await Helpers.NetAsync(app, AccountCodes.Inventory));
        await Helpers.AssertBooksBalancedAsync(app);
        Assert.All((await app.Get<IBalanceSheetService>().BuildAsync(Today, null)).Checks, c => Assert.True(c.Ok, c.Message));
    }

    [Fact]
    public async Task Importing_the_same_file_again_changes_nothing()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var file = File_("items.csv", ItemsCsv);
        await app.Get<IImportService>().ImportAsync(ImportKind.Items, file, null);
        var inventory = await Helpers.NetAsync(app, AccountCodes.Inventory);
        var again = await app.Get<IImportService>().ImportAsync(ImportKind.Items, file, null);
        Assert.Equal(0, again.Imported);
        Assert.Equal(4, again.Duplicates);                                       // the two imported rows and the two earlier duplicates
        Assert.Equal(3, (await app.Get<ICatalogService>().ListAsync(0, 100, true)).Count);
        Assert.Equal(inventory, await Helpers.NetAsync(app, AccountCodes.Inventory));
    }

    [Fact]
    public async Task Headings_are_matched_loosely_and_unknown_columns_are_reported()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var csv = "Item Name;Selling Price;Colour;Tax\nBlue pen;12,50;blue;12\n".Replace("12,50", "12.50");
        var p = await app.Get<IImportService>().PreviewAsync(ImportKind.Items, File_("loose.csv", csv));
        Assert.Equal(1, p.ReadyCount);
        Assert.Contains("Colour", p.IgnoredColumns);
        var r = await app.Get<IImportService>().ImportAsync(ImportKind.Items, File_("loose.csv", csv), null);
        Assert.Equal(1, r.Imported);
        var pen = (await app.Get<ICatalogService>().SearchAsync("Blue pen")).Single();
        Assert.Equal(1_250, pen.SellingPricePaise);
        Assert.Equal(1_200, pen.GstRateBp);
    }

    [Fact]
    public async Task A_file_with_a_byte_order_mark_quoted_commas_and_windows_line_ends_is_read()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var csv = "Name,Price\r\n\"Tea, loose, 250g\",\"1,050.00\"\r\n\"Say \"\"hello\"\" mug\",99\r\n";
        var path = File_("bom.csv", csv, new UTF8Encoding(true));
        var r = await app.Get<IImportService>().ImportAsync(ImportKind.Items, path, null);
        Assert.Equal(2, r.Imported);
        Assert.Equal(105_000, (await app.Get<ICatalogService>().SearchAsync("Tea, loose")).Single().SellingPricePaise);
        Assert.Single(await app.Get<ICatalogService>().SearchAsync("Say \"hello\" mug"));
    }

    [Fact]
    public async Task An_Excel_file_is_read_with_numbers_and_text()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var path = Path.Combine(Path.GetTempPath(), $"import-{Guid.NewGuid():N}.xlsx");
        _files.Add(path);
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("Items");
            ws.Cell(1, 1).Value = "Name"; ws.Cell(1, 2).Value = "Price"; ws.Cell(1, 3).Value = "GST"; ws.Cell(1, 4).Value = "Opening stock"; ws.Cell(1, 5).Value = "Opening cost";
            ws.Cell(2, 1).Value = "Notebook"; ws.Cell(2, 2).Value = 85.5; ws.Cell(2, 3).Value = 12; ws.Cell(2, 4).Value = 20; ws.Cell(2, 5).Value = 50;
            wb.SaveAs(path);
        }
        var r = await app.Get<IImportService>().ImportAsync(ImportKind.Items, path, null);
        Assert.Equal(1, r.Imported);
        var nb = (await app.Get<ICatalogService>().SearchAsync("Notebook")).Single();
        Assert.Equal(8_550, nb.SellingPricePaise);
        Assert.Equal(20_000, nb.QtyOnHandMilli);
        Assert.Equal(5_000, nb.AvgCostPaise);
    }

    // ------------------------------------------------------------------ customers and suppliers

    [Fact]
    public async Task Customers_come_in_with_credit_limits_and_opening_dues_that_tie_to_the_books()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var csv = "Name,Phone,GSTIN,State code,Credit limit,Opening due\n" +
                  "Ravi Traders,9000000001,,33,50000,12500\n" +
                  "Kumar Stores,9000000003,33AAAAA1111A1Z5,,,0\n" +
                  "Bad GSTIN,,12345,,,\n" +
                  "State mismatch,,29AAAAA2222A1Z5,33,,\n" +
                  "Bad due,,,,,abc\n";
        var path = File_("customers.csv", csv);
        var imp = app.Get<IImportService>();
        var p = await imp.PreviewAsync(ImportKind.Customers, path);
        Assert.Equal(2, p.ReadyCount);
        Assert.Equal(3, p.ErrorCount);
        Assert.True(p.NeedsAsAtDate);

        await Assert.ThrowsAsync<ValidationException>(() => imp.ImportAsync(ImportKind.Customers, path, null));                 // dues need a date
        await Assert.ThrowsAsync<ValidationException>(() => imp.ImportAsync(ImportKind.Customers, path, Today.AddDays(2)));
        var r = await imp.ImportAsync(ImportKind.Customers, path, Today.AddDays(-1));
        Assert.Equal(2, r.Imported);

        var ravi = (await app.Get<IPartyService>().SearchAsync("Ravi")).Single();
        Assert.Equal(1_250_000, ravi.OutstandingPaise);
        Assert.Equal(5_000_000, ravi.CreditLimitPaise);
        var kumar = (await app.Get<IPartyService>().SearchAsync("Kumar")).Single();
        Assert.Equal("33", kumar.StateCode);                                      // taken from the GSTIN
        Assert.Equal(0, kumar.OutstandingPaise);
        Assert.Equal(1_250_000, await Helpers.NetAsync(app, AccountCodes.Receivables));
        Assert.All((await app.Get<IBalanceSheetService>().BuildAsync(Today, null)).Checks, c => Assert.True(c.Ok, c.Message));
        await Helpers.AssertBooksBalancedAsync(app);

        var again = await imp.ImportAsync(ImportKind.Customers, path, Today.AddDays(-1));
        Assert.Equal(0, again.Imported);
        Assert.Equal(1_250_000, (await app.Get<IPartyService>().SearchAsync("Ravi")).Single().OutstandingPaise);   // the due was not added twice
    }

    [Fact]
    public async Task Suppliers_come_in_with_what_is_owed_to_them()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var csv = "Supplier,Mobile,GST No,State,Payable\nMetro Wholesale,9000000002,33AAAAA0000A1Z5,33,40000\nSouth Traders,,,,\n";
        var r = await app.Get<IImportService>().ImportAsync(ImportKind.Suppliers, File_("sup.csv", csv), Today.AddDays(-3));
        Assert.Equal(2, r.Imported);
        var metro = (await app.Get<ISupplierService>().SearchAsync("Metro")).Single();
        Assert.Equal(4_000_000, metro.OutstandingPaise);
        Assert.Equal(-4_000_000, await Helpers.NetAsync(app, AccountCodes.Payables));
        await app.Get<ISupplierService>().PayAsync(metro.Id, 1_000_000, PaymentMode.Cash);          // and it can be paid down
        Assert.Equal(3_000_000, (await app.Get<ISupplierService>().SearchAsync("Metro")).Single().OutstandingPaise);
        await Helpers.AssertBooksBalancedAsync(app);
    }

    [Fact]
    public async Task A_customer_already_there_is_skipped_by_name_and_phone_or_by_GSTIN()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<IPartyService>().SaveAsync(new PartyDto(0, "Ravi Traders", "9000000001", null, "33", 0));
        await app.Get<IPartyService>().SaveAsync(new PartyDto(0, "Kumar Stores", null, "33AAAAA1111A1Z5", "33", 0));
        var csv = "Name,Phone,GSTIN\nravi traders,9000000001,\nKumar Stores Ltd,,33AAAAA1111A1Z5\nRavi Traders,9000000099,\n";
        var p = await app.Get<IImportService>().PreviewAsync(ImportKind.Customers, File_("c.csv", csv));
        Assert.Equal(2, p.DuplicateCount);
        Assert.Equal(1, p.ReadyCount);                                            // same name but a different phone is a different customer
        Assert.False(p.NeedsAsAtDate);
    }

    // ------------------------------------------------------------------ file problems, template, permissions

    [Fact]
    public async Task Bad_files_are_explained_not_crashed_on()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var imp = app.Get<IImportService>();
        Assert.Contains("price", (await imp.PreviewAsync(ImportKind.Items, File_("nopr.csv", "Name,Colour\nPen,blue\n"))).FileProblem);
        Assert.Contains("name, price", (await imp.PreviewAsync(ImportKind.Items, File_("nothing.csv", "Colour\nblue\n"))).FileProblem);
        Assert.Contains("name", (await imp.PreviewAsync(ImportKind.Customers, File_("noname.csv", "Phone\n9000\n"))).FileProblem);
        Assert.Contains("empty", (await imp.PreviewAsync(ImportKind.Items, File_("empty.csv", ""))).FileProblem);
        Assert.Contains("not found", (await imp.PreviewAsync(ImportKind.Items, Path.Combine(Path.GetTempPath(), "nothing-" + Guid.NewGuid().ToString("N") + ".csv"))).FileProblem);
        Assert.Contains(".xlsx", (await imp.PreviewAsync(ImportKind.Items, File_("old.xls", "x"))).FileProblem);
        Assert.Contains("Excel (.xlsx) or CSV", (await imp.PreviewAsync(ImportKind.Items, File_("data.pdf", "x"))).FileProblem);
        var badXlsx = File_("broken.xlsx", "this is not a zip file");
        Assert.Contains("could not be read", (await imp.PreviewAsync(ImportKind.Items, badXlsx)).FileProblem);
        await Assert.ThrowsAsync<ValidationException>(() => imp.ImportAsync(ImportKind.Items, File_("nopr2.csv", "Name\nPen\n"), null));
        await Assert.ThrowsAsync<ValidationException>(() => imp.PreviewAsync((ImportKind)9, File_("x.csv", "Name,Price\nA,1\n")));
        Assert.Single(await app.Get<ICatalogService>().ListAsync(0, 100, true));
    }

    [Fact]
    public async Task The_blank_template_has_the_right_headings_and_can_be_read_back()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var imp = app.Get<IImportService>();
        foreach (var kind in new[] { ImportKind.Items, ImportKind.Customers, ImportKind.Suppliers })
        {
            var path = Path.Combine(Path.GetTempPath(), $"template-{Guid.NewGuid():N}.xlsx");
            _files.Add(path);
            await imp.WriteTemplateAsync(kind, path);
            var p = await imp.PreviewAsync(kind, path);
            Assert.Null(p.FileProblem);
            Assert.Empty(p.IgnoredColumns);
            Assert.Equal(0, p.TotalRows);                                          // headings only: nothing can be imported by mistake
        }
    }

    [Fact]
    public async Task Only_the_Admin_can_import_and_every_import_is_logged()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var imp = app.Get<IImportService>();
        await imp.ImportAsync(ImportKind.Items, File_("small.csv", "Name,Price\nPen,10\n"), null);
        await using (var db = app.Get<DbFactory>().Create())
            Assert.Contains(db.AuditEvents.Select(e => e.Action).ToList(), a => a == "import.done");
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => imp.PreviewAsync(ImportKind.Items, File_("c.csv", "Name,Price\nPen2,10\n")));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => imp.ImportAsync(ImportKind.Items, File_("c2.csv", "Name,Price\nPen3,10\n"), null));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => imp.WriteTemplateAsync(ImportKind.Items, Path.Combine(Path.GetTempPath(), "t.xlsx")));
    }
}
