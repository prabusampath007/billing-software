using System.Globalization;
using System.Text;
using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

/// <summary>
/// Brings in the customer's existing items, customers and suppliers from a spreadsheet. Every row goes through the same
/// rules as typing it in on the screen, and rows that are already there are skipped, so the import can be repeated safely.
/// </summary>
internal sealed class ImportService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), IImportService
{
    private const int MaxRows = 20_000;
    private const long MaxFileBytes = 20 * 1024 * 1024;
    private const int MaxListed = 2_000;

    // ---------------- reading the file ----------------
    private sealed record Table(string[] Headers, List<string[]> Rows);

    private static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static readonly Dictionary<ImportKind, Dictionary<string, string[]>> Columns = new()
    {
        [ImportKind.Items] = new()
        {
            ["name"] = new[] { "name", "itemname", "item", "product", "productname", "description" },
            ["tamil"] = new[] { "tamilname", "tamil", "nameintamil", "itemnametamil" },
            ["sku"] = new[] { "sku", "code", "itemcode", "productcode" },
            ["barcode"] = new[] { "barcode", "ean", "upc" },
            ["hsn"] = new[] { "hsn", "hsncode", "hsnsac" },
            ["gst"] = new[] { "gst", "gstrate", "gstpercent", "tax", "taxrate", "gst%" },
            ["unit"] = new[] { "unit", "uom" },
            ["price"] = new[] { "price", "sellingprice", "saleprice", "rate", "mrpprice" },
            ["mrp"] = new[] { "mrp" },
            ["inclusive"] = new[] { "pricesincludegst", "priceincludesgst", "inclusive", "gstinclusive", "includesgst" },
            ["stock"] = new[] { "stock", "openingstock", "qty", "quantity", "onhand" },
            ["cost"] = new[] { "cost", "openingcost", "costprice", "purchaseprice", "unitcost" },
        },
        [ImportKind.Customers] = new()
        {
            ["name"] = new[] { "name", "customer", "customername", "party" },
            ["phone"] = new[] { "phone", "mobile", "phonenumber", "contact" },
            ["gstin"] = new[] { "gstin", "gstno", "gstnumber" },
            ["state"] = new[] { "state", "statecode" },
            ["limit"] = new[] { "creditlimit", "limit" },
            ["due"] = new[] { "openingdue", "due", "outstanding", "balance", "owes", "amountdue" },
        },
        [ImportKind.Suppliers] = new()
        {
            ["name"] = new[] { "name", "supplier", "suppliername", "vendor", "party" },
            ["phone"] = new[] { "phone", "mobile", "phonenumber", "contact" },
            ["gstin"] = new[] { "gstin", "gstno", "gstnumber" },
            ["state"] = new[] { "state", "statecode" },
            ["due"] = new[] { "openingpayable", "payable", "due", "outstanding", "balance", "owedtosupplier", "amountdue" },
        },
    };

    private static Table ReadFile(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists) throw new ValidationException("That file was not found.");
        if (info.Length > MaxFileBytes) throw new ValidationException("The file is larger than 20 MB. Split it into smaller files.");
        var ext = info.Extension.ToLowerInvariant();
        var raw = ext switch
        {
            ".xlsx" => ReadXlsx(file),
            ".csv" or ".txt" => ReadCsv(file),
            ".xls" => throw new ValidationException("Old Excel (.xls) files are not supported. In Excel choose Save As, then \"Excel Workbook (.xlsx)\" or \"CSV\"."),
            _ => throw new ValidationException("Choose an Excel (.xlsx) or CSV file."),
        };
        var nonEmpty = raw.Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (nonEmpty.Count == 0) throw new ValidationException("The file is empty.");
        var headers = nonEmpty[0].Select(h => (h ?? "").Trim()).ToArray();
        var rows = nonEmpty.Skip(1).ToList();
        if (rows.Count > MaxRows) throw new ValidationException($"The file has more than {MaxRows:N0} rows. Split it into smaller files.");
        return new Table(headers, rows);
    }

    private static List<string[]> ReadXlsx(string file)
    {
        try
        {
            using var wb = new XLWorkbook(file);
            var ws = wb.Worksheets.First();
            var used = ws.RangeUsed();
            if (used == null) return new();
            var result = new List<string[]>();
            var width = used.ColumnCount();
            foreach (var row in used.Rows())
            {
                var cells = new string[width];
                for (var c = 0; c < width; c++)
                {
                    var cell = row.Cell(c + 1);
                    cells[c] = cell.DataType == XLDataType.Number ? cell.GetDouble().ToString("0.############", CultureInfo.InvariantCulture)
                        : cell.DataType == XLDataType.DateTime ? cell.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : cell.GetString();
                }
                result.Add(cells);
            }
            return result;
        }
        catch (Exception ex) when (ex is not ValidationException)       // the Excel library throws many kinds of error for a file that is not a real workbook
        {
            throw new ValidationException("The Excel file could not be read. Is it open in another program, or damaged?");
        }
    }

    /// <summary>A small CSV reader: quoted fields, doubled quotes, commas or semicolons, a byte-order mark.</summary>
    private static List<string[]> ReadCsv(string file)
    {
        string text;
        try { text = File.ReadAllText(file, Encoding.UTF8); }
        catch (IOException) { throw new ValidationException("The file could not be read. Is it open in another program?"); }
        text = text.TrimStart('﻿');
        var firstLine = text.Split('\n', 2)[0];
        var delimiter = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : (firstLine.Count(c => c == '\t') > firstLine.Count(c => c == ',') ? '\t' : ',');

        var rows = new List<string[]>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == delimiter) { fields.Add(sb.ToString()); sb.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(sb.ToString()); sb.Clear();
                rows.Add(fields.ToArray()); fields.Clear();
            }
            else sb.Append(c);
        }
        if (sb.Length > 0 || fields.Count > 0) { fields.Add(sb.ToString()); rows.Add(fields.ToArray()); }
        return rows;
    }

    /// <summary>Maps the file's headings to our columns. Returns the column index per key, and the headings that were not recognised.</summary>
    private static (Dictionary<string, int> Map, List<string> Ignored) MapHeaders(ImportKind kind, string[] headers)
    {
        var map = new Dictionary<string, int>();
        var ignored = new List<string>();
        for (var i = 0; i < headers.Length; i++)
        {
            var n = Norm(headers[i]);
            if (n.Length == 0) continue;
            var key = Columns[kind].FirstOrDefault(kv => kv.Value.Contains(n)).Key;
            if (key != null && !map.ContainsKey(key)) map[key] = i; else ignored.Add(headers[i]);
        }
        return (map, ignored);
    }

    private static string Get(string[] row, Dictionary<string, int> map, string key) =>
        map.TryGetValue(key, out var i) && i < row.Length ? (row[i] ?? "").Trim() : "";

    // ---------------- what the screen shows: the file's own columns ----------------
    private static readonly Dictionary<ImportKind, (string Key, string Title)[]> ShownColumns = new()
    {
        [ImportKind.Items] = new[] { ("name", "Name"), ("tamil", "Tamil name"), ("sku", "Code"), ("barcode", "Barcode"), ("hsn", "HSN"), ("gst", "GST %"), ("unit", "Unit"),
            ("price", "Price"), ("mrp", "MRP"), ("inclusive", "Price incl. GST"), ("stock", "Opening stock"), ("cost", "Opening cost") },
        [ImportKind.Customers] = new[] { ("name", "Name"), ("phone", "Phone"), ("gstin", "GSTIN"), ("state", "State"), ("limit", "Credit limit"), ("due", "Opening due") },
        [ImportKind.Suppliers] = new[] { ("name", "Name"), ("phone", "Phone"), ("gstin", "GSTIN"), ("state", "State"), ("due", "Opening payable") },
    };

    private static (List<string> Titles, List<string> Keys) ColumnsFor(ImportKind kind, Dictionary<string, int> map)
    {
        var shown = ShownColumns[kind].Where(c => map.ContainsKey(c.Key)).OrderBy(c => map[c.Key]).ToList();      // in the order of the file
        return (shown.Select(c => c.Title).ToList(), shown.Select(c => c.Key).ToList());
    }

    /// <summary>Gives each row the cells of its columns exactly as they are in the file, so the table looks like the spreadsheet.</summary>
    private static List<ImportRow> WithCells(List<ImportRow> rows, Table table, Dictionary<string, int> map, List<string> keys) =>
        rows.Select(r =>
        {
            var i = r.RowNumber - 2;
            var cells = i >= 0 && i < table.Rows.Count ? keys.Select(k => Get(table.Rows[i], map, k)).ToList() : new List<string>();
            return r with { Cells = cells };
        }).ToList();

    // ---------------- number parsing ----------------
    private static bool TryMoney(string text, out long paise, bool allowEmpty = true)
    {
        paise = 0;
        text = text.Replace("₹", "").Replace("Rs.", "", StringComparison.OrdinalIgnoreCase).Replace("Rs", "", StringComparison.OrdinalIgnoreCase).Replace(",", "").Trim();
        if (text.Length == 0) return allowEmpty;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) || d < 0 || d > 1_000_000_000m) return false;
        paise = Money.ToPaise(d);
        return true;
    }

    private static bool TryQty(string text, out long milli)
    {
        milli = 0;
        text = text.Replace(",", "").Trim();
        if (text.Length == 0) return true;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) || d < 0 || d > 1_000_000_000m) return false;
        milli = Qty.ToMilli(d);
        return true;
    }

    private static bool TryYesNo(string text, bool dflt, out bool value)
    {
        value = dflt;
        var t = text.Trim().ToLowerInvariant();
        if (t.Length == 0) return true;
        if (t is "y" or "yes" or "true" or "1") { value = true; return true; }
        if (t is "n" or "no" or "false" or "0") { value = false; return true; }
        return false;
    }

    // ---------------- parsed rows ----------------
    private sealed record ItemRec(int Row, string Name, string? Sku, string? Barcode, string? Hsn, int GstBp, string Unit, long Price, long? Mrp, bool Inclusive, long QtyMilli, long Cost, string? Tamil = null);
    private sealed record PartyRec(int Row, string Name, string? Phone, string? Gstin, string? State, long Limit, long Due);

    private sealed record Parsed<T>(List<T> Ok, List<ImportRow> Rows);

    private static string DisplayName(string[] row, Dictionary<string, int> map, int rowNo) { var n = Get(row, map, "name"); return n.Length > 0 ? n : $"(row {rowNo})"; }

    private static string? StateFrom(string stateText, string? gstin, out string? error)
    {
        error = null;
        if (stateText.Length == 0) return gstin?[..2];
        var digits = stateText.Trim();
        if (digits.Length == 1 && char.IsDigit(digits[0])) digits = "0" + digits;
        if (digits.Length == 2 && digits.All(char.IsDigit)) return digits;
        error = "The state must be its 2-digit GST code (for example 33 for Tamil Nadu), or left empty.";
        return null;
    }

    private async Task<(Table Table, Dictionary<string, int> Map, List<string> Ignored, string? Problem)> LoadAsync(ImportKind kind, string file)
    {
        Table table;
        try { table = await Task.Run(() => ReadFile(file)); }
        catch (ValidationException ex) { return (new Table(Array.Empty<string>(), new()), new(), new(), ex.Message); }
        var (map, ignored) = MapHeaders(kind, table.Headers);
        var required = kind == ImportKind.Items ? new[] { "name", "price" } : new[] { "name" };
        var missing = required.Where(k => !map.ContainsKey(k)).ToList();
        if (missing.Count > 0)
            return (table, map, ignored, "The file needs a column for: " + string.Join(", ", missing.Select(m => m == "price" ? "price" : "name")) + ". Use \"Save a blank template\" to see the headings.");
        return (table, map, ignored, null);
    }

    // ---------------- items ----------------
    private async Task<(List<ItemRec> Ok, List<ImportRow> Rows)> CheckItemsAsync(AppDbContext db, Table table, Dictionary<string, int> map)
    {
        var skus = (await db.Products.AsNoTracking().Select(p => p.Sku).ToListAsync()).Select(s => s.ToLowerInvariant()).ToHashSet();
        var codes = (await db.Products.AsNoTracking().Where(p => p.Barcode != null).Select(p => p.Barcode!).ToListAsync())
            .Concat(await db.ProductBarcodes.AsNoTracking().Select(b => b.Code).ToListAsync()).ToHashSet();
        var names = (await db.Products.AsNoTracking().Select(p => p.Name).ToListAsync()).Select(n => n.Trim().ToLowerInvariant()).ToHashSet();
        var seenSku = new HashSet<string>(); var seenBarcode = new HashSet<string>(); var seenName = new HashSet<string>();
        var ok = new List<ItemRec>(); var rows = new List<ImportRow>();
        var next = 1;
        string NewSku()
        {
            while (true)
            {
                var s = $"ITEM-{next++:D5}";
                if (!skus.Contains(s.ToLowerInvariant()) && seenSku.Add(s.ToLowerInvariant())) return s;
            }
        }

        for (var i = 0; i < table.Rows.Count; i++)
        {
            var r = table.Rows[i]; var no = i + 2;
            var name = Get(r, map, "name"); var show = DisplayName(r, map, no);
            void Fail(string why) => rows.Add(new ImportRow(no, show, ImportRowStatus.Error, why));
            if (name.Length == 0) { Fail("The item name is empty."); continue; }
            if (name.Length > 120) { Fail("The item name is too long (over 120 characters)."); continue; }
            if (!TryMoney(Get(r, map, "price"), out var price, allowEmpty: false)) { Fail("The price is missing or not a number."); continue; }
            long? mrp = null;
            if (Get(r, map, "mrp").Length > 0) { if (!TryMoney(Get(r, map, "mrp"), out var m)) { Fail("The MRP is not a number."); continue; } mrp = m; }
            var gstText = Get(r, map, "gst").Replace("%", "").Trim();
            var gstBp = 0;
            if (gstText.Length > 0)
            {
                if (!decimal.TryParse(gstText, NumberStyles.Number, CultureInfo.InvariantCulture, out var g) || g < 0 || g > 50) { Fail("The GST rate must be a number from 0 to 50."); continue; }
                gstBp = (int)Math.Round(g * 100);
            }
            else if (map.ContainsKey("gst")) { /* an empty cell means 0% */ }
            var unit = Get(r, map, "unit"); if (unit.Length == 0) unit = "pcs";
            if (!TryYesNo(Get(r, map, "inclusive"), true, out var inclusive)) { Fail("\"Price includes GST\" must be Yes or No."); continue; }
            if (!TryQty(Get(r, map, "stock"), out var qty)) { Fail("The opening stock is not a number."); continue; }
            if (!TryMoney(Get(r, map, "cost"), out var cost)) { Fail("The opening cost is not a number."); continue; }
            if (qty > 0 && Get(r, map, "cost").Length == 0) { Fail("Opening stock needs its cost per unit."); continue; }

            var sku = Get(r, map, "sku"); var barcode = Get(r, map, "barcode");
            if (barcode.Length > 0)
            {
                try { barcode = BarcodeService.CleanCode(barcode); }
                catch (ValidationException ex) { Fail(ex.Message); continue; }
            }
            if (sku.Length > 0 && (skus.Contains(sku.ToLowerInvariant()) || (barcode.Length > 0 && codes.Contains(barcode)) || (barcode.Length > 0 && skus.Contains(barcode.ToLowerInvariant()))))
            { rows.Add(new ImportRow(no, show, ImportRowStatus.Duplicate, "Already in the software (same code or barcode). Skipped.")); continue; }
            if (sku.Length == 0 && barcode.Length > 0 && codes.Contains(barcode))
            { rows.Add(new ImportRow(no, show, ImportRowStatus.Duplicate, "Already in the software (same barcode). Skipped.")); continue; }
            if (sku.Length > 0 && !seenSku.Add(sku.ToLowerInvariant())) { rows.Add(new ImportRow(no, show, ImportRowStatus.Duplicate, "The same code appears earlier in this file. Skipped.")); continue; }
            if (barcode.Length > 0 && !seenBarcode.Add(barcode)) { rows.Add(new ImportRow(no, show, ImportRowStatus.Duplicate, "The same barcode appears earlier in this file. Skipped.")); continue; }

            // no code in the file: the name is what identifies it, so importing the same file twice does not make a second copy
            if (sku.Length == 0)
            {
                if (names.Contains(name.ToLowerInvariant())) { rows.Add(new ImportRow(no, show, ImportRowStatus.Duplicate, "An item with this name is already in the software. Skipped.")); continue; }
                if (!seenName.Add(name.ToLowerInvariant())) { rows.Add(new ImportRow(no, show, ImportRowStatus.Duplicate, "The same name appears earlier in this file. Skipped.")); continue; }
            }
            var finalSku = sku.Length > 0 ? sku : NewSku();
            var tamilName = Get(r, map, "tamil");
            if (tamilName.Length > 120) { Fail("The Tamil name is too long (over 120 characters)."); continue; }
            ok.Add(new ItemRec(no, name, finalSku, barcode.Length > 0 ? barcode : null, Get(r, map, "hsn").Length > 0 ? Get(r, map, "hsn") : null, gstBp, unit, price, mrp, inclusive, qty, cost, tamilName.Length > 0 ? tamilName : null));
            rows.Add(new ImportRow(no, show, ImportRowStatus.Ready, $"Code {finalSku}, price {Money.Format(price)}, GST {gstBp / 100m:0.##}%" + (qty > 0 ? $", opening stock {Qty.Format(qty)} {unit}" : "")));
        }
        return (ok, rows);
    }

    // ---------------- customers and suppliers ----------------
    private async Task<(List<PartyRec> Ok, List<ImportRow> Rows, bool HasDues)> CheckPartiesAsync(AppDbContext db, ImportKind kind, Table table, Dictionary<string, int> map)
    {
        var existing = kind == ImportKind.Customers
            ? (await db.Parties.AsNoTracking().Select(p => new { p.Name, p.Phone, p.Gstin }).ToListAsync()).Select(p => (p.Name, p.Phone, p.Gstin)).ToList()
            : (await db.Suppliers.AsNoTracking().Select(p => new { p.Name, p.Phone, p.Gstin }).ToListAsync()).Select(p => (p.Name, p.Phone, p.Gstin)).ToList();
        string Key(string name, string? phone) => name.Trim().ToLowerInvariant() + "|" + (phone ?? "").Trim();
        var known = existing.Select(e => Key(e.Name, e.Phone)).ToHashSet();
        var knownGstin = existing.Where(e => !string.IsNullOrWhiteSpace(e.Gstin)).Select(e => e.Gstin!.ToUpperInvariant()).ToHashSet();
        var seen = new HashSet<string>(); var ok = new List<PartyRec>(); var rows = new List<ImportRow>();
        var hasDues = false;

        for (var i = 0; i < table.Rows.Count; i++)
        {
            var r = table.Rows[i]; var no = i + 2;
            var name = Get(r, map, "name"); var show = DisplayName(r, map, no);
            void Fail(string why) => rows.Add(new ImportRow(no, show, ImportRowStatus.Error, why));
            if (name.Length == 0) { Fail("The name is empty."); continue; }
            if (name.Length > 120) { Fail("The name is too long (over 120 characters)."); continue; }
            var gstin = Get(r, map, "gstin").ToUpperInvariant();
            if (gstin.Length > 0 && !GstValidation.IsValidGstin(gstin)) { Fail("The GSTIN format is not valid."); continue; }
            var state = StateFrom(Get(r, map, "state"), gstin.Length > 0 ? gstin : null, out var stateError);
            if (stateError != null) { Fail(stateError); continue; }
            if (gstin.Length > 0 && state != null && !gstin.StartsWith(state, StringComparison.Ordinal)) { Fail("The first two digits of the GSTIN must match the state."); continue; }
            if (!TryMoney(Get(r, map, "due"), out var due)) { Fail("The opening amount is not a number."); continue; }
            long limit = 0;
            if (kind == ImportKind.Customers && !TryMoney(Get(r, map, "limit"), out limit)) { Fail("The credit limit is not a number."); continue; }
            var phone = Get(r, map, "phone");

            var key = Key(name, phone);
            if (known.Contains(key) || (gstin.Length > 0 && knownGstin.Contains(gstin)))
            { rows.Add(new ImportRow(no, show, ImportRowStatus.Duplicate, "Already in the software (same name and phone, or same GSTIN). Skipped.")); continue; }
            if (!seen.Add(key)) { rows.Add(new ImportRow(no, show, ImportRowStatus.Duplicate, "The same name and phone appear earlier in this file. Skipped.")); continue; }

            if (due > 0) hasDues = true;
            ok.Add(new PartyRec(no, name, phone.Length > 0 ? phone : null, gstin.Length > 0 ? gstin : null, state, limit, due));
            rows.Add(new ImportRow(no, show, ImportRowStatus.Ready,
                (due > 0 ? $"{(kind == ImportKind.Customers ? "Owes" : "You owe")} {Money.Format(due)}" : "No opening amount") + (limit > 0 ? $", credit limit {Money.Format(limit)}" : "")));
        }
        return (ok, rows, hasDues);
    }

    // ---------------- preview ----------------
    public async Task<ImportPreview> PreviewAsync(ImportKind kind, string file)
    {
        RequireAdmin();
        if (!Enum.IsDefined(kind)) throw new ValidationException("Choose what to import.");
        var (table, map, ignored, problem) = await LoadAsync(kind, file);
        var name = Path.GetFileName(file);
        if (problem != null) return new ImportPreview(kind, name, problem, 0, 0, 0, 0, ignored, false, Array.Empty<ImportRow>());

        await using var db = Factory.Create();
        List<ImportRow> rows; bool needsDate = false;
        if (kind == ImportKind.Items) rows = (await CheckItemsAsync(db, table, map)).Rows;
        else { var p = await CheckPartiesAsync(db, kind, table, map); rows = p.Rows; needsDate = p.HasDues; }
        rows = rows.OrderBy(r => r.RowNumber).ToList();
        var (titles, keys) = ColumnsFor(kind, map);
        rows = WithCells(rows, table, map, keys);
        return new ImportPreview(kind, name, null, rows.Count, rows.Count(r => r.Status == ImportRowStatus.Ready), rows.Count(r => r.Status == ImportRowStatus.Duplicate),
            rows.Count(r => r.Status == ImportRowStatus.Error), ignored, needsDate, rows.Take(MaxListed).ToList(), titles);
    }

    // ---------------- import ----------------
    public async Task<ImportResult> ImportAsync(ImportKind kind, string file, DateOnly? asAt)
    {
        var admin = RequireAdmin();
        if (!Enum.IsDefined(kind)) throw new ValidationException("Choose what to import.");
        var (table, map, _, problem) = await LoadAsync(kind, file);
        if (problem != null) throw new ValidationException(problem);

        var catalog = new CatalogService(Factory, Current);
        var stock = new StockService(Factory, Current);
        var parties = new PartyService(Factory, Current);
        var suppliers = new SupplierService(Factory, Current);
        var opening = new OpeningBalanceService(Factory, Current);

        List<ImportRow> rows; int imported = 0;
        await using (var db = Factory.Create())
        {
            if (kind == ImportKind.Items)
            {
                var (ok, checkedRows) = await CheckItemsAsync(db, table, map);
                rows = checkedRows;
                foreach (var it in ok)
                {
                    try
                    {
                        var saved = await catalog.SaveAsync(new ProductDto(0, it.Sku!, it.Barcode, it.Name, it.Hsn, it.GstBp, it.Unit, it.Price, it.Mrp, it.Inclusive, 0, 0, true, it.Tamil));
                        if (it.QtyMilli > 0) await stock.ReceiveOpeningStockAsync(saved.Id, it.QtyMilli, it.Cost);
                        Mark(rows, it.Row, ImportRowStatus.Imported, "Imported"); imported++;
                    }
                    catch (BusinessRuleException ex) { Mark(rows, it.Row, ImportRowStatus.Error, ex.Message); }
                }
            }
            else
            {
                var (ok, checkedRows, hasDues) = await CheckPartiesAsync(db, kind, table, map);
                rows = checkedRows;
                if (hasDues && asAt == null) throw new ValidationException("The file has opening amounts. Choose the date they are as at.");
                if (asAt is { } d && d > DateOnly.FromDateTime(DateTime.Today)) throw new ValidationException("The date cannot be in the future.");
                foreach (var p in ok)
                {
                    try
                    {
                        long id;
                        if (kind == ImportKind.Customers) id = (await parties.SaveAsync(new PartyDto(0, p.Name, p.Phone, p.Gstin, p.State, 0, p.Limit))).Id;
                        else id = (await suppliers.SaveAsync(new SupplierDto(0, p.Name, p.Phone, p.Gstin, p.State, 0))).Id;
                        if (p.Due > 0)
                            await opening.AddAsync(new AddOpeningCommand(Guid.NewGuid(), kind == ImportKind.Customers ? OpeningKind.CustomerDue : OpeningKind.SupplierDue,
                                asAt!.Value, kind == ImportKind.Customers ? id : null, kind == ImportKind.Suppliers ? id : null, "", p.Due, null, "Imported"));
                        Mark(rows, p.Row, ImportRowStatus.Imported, "Imported"); imported++;
                    }
                    catch (BusinessRuleException ex) { Mark(rows, p.Row, ImportRowStatus.Error, ex.Message); }
                }
            }
            await AuditWriter.AppendAsync(db, admin, "import.done", "Import", null,
                $"{kind}: {imported} imported, {rows.Count(r => r.Status == ImportRowStatus.Duplicate)} duplicates skipped, {rows.Count(r => r.Status == ImportRowStatus.Error)} rows with problems, file {Path.GetFileName(file)}");
        }
        rows = rows.OrderBy(r => r.RowNumber).ToList();
        var (resultTitles, resultKeys) = ColumnsFor(kind, map);
        rows = WithCells(rows, table, map, resultKeys);
        return new ImportResult(imported, rows.Count(r => r.Status == ImportRowStatus.Duplicate), rows.Count(r => r.Status == ImportRowStatus.Error), rows.Take(MaxListed).ToList(), resultTitles);
    }

    private static void Mark(List<ImportRow> rows, int rowNo, ImportRowStatus status, string detail)
    {
        var i = rows.FindIndex(r => r.RowNumber == rowNo);
        if (i >= 0) rows[i] = rows[i] with { Status = status, Detail = detail };
    }

    // ---------------- template ----------------
    public Task WriteTemplateAsync(ImportKind kind, string path)
    {
        RequireAdmin();
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(kind.ToString());
        string[] headers; string[] example;
        switch (kind)
        {
            case ImportKind.Items:
                headers = new[] { "Name", "Tamil name", "Code", "Barcode", "HSN", "GST %", "Unit", "Price", "MRP", "Price includes GST", "Opening stock", "Opening cost" };
                example = new[] { "Lux Soap 100g", "லக்ஸ் சோப்பு 100கி", "SOAP-100", "4006381333931", "3401", "18", "pcs", "45.00", "45.00", "Yes", "50", "28.00" }; break;
            case ImportKind.Customers:
                headers = new[] { "Name", "Phone", "GSTIN", "State code", "Credit limit", "Opening due" };
                example = new[] { "Ravi Traders", "9000000001", "", "33", "50000", "12500" }; break;
            case ImportKind.Suppliers:
                headers = new[] { "Name", "Phone", "GSTIN", "State code", "Opening payable" };
                example = new[] { "Metro Wholesale", "9000000002", "33AAAAA0000A1Z5", "33", "40000" }; break;
            default: throw new ValidationException("Choose what to import.");
        }
        for (var i = 0; i < headers.Length; i++)
        {
            var h = ws.Cell(1, i + 1); h.Value = headers[i]; h.Style.Font.Bold = true; h.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F7");
        }
        ws.Columns().AdjustToContents();
        // Only the first sheet is read, so the notes and the example live on a second one and can never be imported by mistake.
        var help = wb.Worksheets.Add("How to fill this in");
        help.Cell(1, 1).Value = "Type one row per " + (kind == ImportKind.Items ? "item" : kind == ImportKind.Customers ? "customer" : "supplier") + " on the first sheet, under the headings. Do not change the headings.";
        help.Cell(2, 1).Value = "Only the first sheet is read. Rows that are already in the software are skipped, so the same file can be imported again safely.";
        help.Cell(4, 1).Value = "Example row:";
        for (var i = 0; i < headers.Length; i++) { help.Cell(5, i + 1).Value = headers[i]; help.Cell(5, i + 1).Style.Font.Bold = true; help.Cell(6, i + 1).Value = example[i]; }
        help.Columns().AdjustToContents();
        wb.SaveAs(path);
        return Task.CompletedTask;
    }
}
