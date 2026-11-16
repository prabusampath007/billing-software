using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

internal sealed class BarcodeService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), IBarcodeService
{
    private const int MaxPerItem = 20;
    private const long MaxPackMilli = 1_000_000_000;      // one million units in one scan is a typing mistake

    private ProductDto Map(Product p) => new(p.Id, p.Sku, p.Barcode, p.Name, p.HsnCode, p.GstRateBp, p.Unit,
        p.SellingPricePaise, p.MrpPaise, p.PriceInclusive,
        Current.IsAdmin ? p.AvgCostPaise : 0,                      // cashiers never see purchase cost
        p.QtyOnHandMilli, p.IsActive);

    public async Task<ScanResult?> ScanAsync(string code)
    {
        RequireUser();
        code = (code ?? "").Trim();
        if (code.Length == 0) return null;
        await using var db = Factory.Create();

        var own = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.IsActive && (x.Barcode == code || x.Sku == code));
        if (own != null) return new ScanResult(Map(own), Qty.Unit, false);

        var pack = await db.ProductBarcodes.AsNoTracking().FirstOrDefaultAsync(b => b.Code == code);
        if (pack == null) return null;
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pack.ProductId && x.IsActive);
        return product == null ? null : new ScanResult(Map(product), pack.PackQtyMilli, true);
    }

    public async Task<IReadOnlyList<ProductBarcodeDto>> ListAsync(long productId)
    {
        RequireAdmin();
        await using var db = Factory.Create();
        return await db.ProductBarcodes.AsNoTracking().Where(b => b.ProductId == productId).OrderBy(b => b.Id)
            .Select(b => new ProductBarcodeDto(b.Id, b.ProductId, b.Code, b.PackQtyMilli, b.Note)).ToListAsync();
    }

    /// <summary>Checks a typed code: no spaces, a sensible length, and a correct check digit if it is 13 digits (a typing mistake otherwise).</summary>
    internal static string CleanCode(string? raw)
    {
        var code = (raw ?? "").Trim();
        if (code.Length < 3 || code.Length > 40) throw new ValidationException("A barcode must be 3 to 40 characters.");
        if (code.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) throw new ValidationException("A barcode cannot contain spaces.");
        if (Ean13.IsDigits(code, 13) && !Ean13.IsValid(code))
            throw new ValidationException("This looks like a 13-digit EAN barcode but its last digit does not match. Check it for a typing mistake.");
        return code;
    }

    public async Task<ProductBarcodeDto> AddAsync(long productId, string code, long packQtyMilli, string? note)
    {
        var admin = RequireAdmin();
        code = CleanCode(code);
        if (packQtyMilli <= 0) throw new ValidationException("The quantity one scan adds must be more than zero.");
        if (packQtyMilli > MaxPackMilli) throw new ValidationException("That quantity is too large. Please check it.");
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if ((note?.Length ?? 0) > 80) throw new ValidationException("The note is too long.");

        await using var db = Factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync();
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId) ?? throw new ValidationException("Item not found.");
        if (await db.ProductBarcodes.CountAsync(b => b.ProductId == productId) >= MaxPerItem)
            throw new BusinessRuleException($"An item can have at most {MaxPerItem} extra barcodes.");
        if (await BarcodeInUseAsync(db, code, ignoreProductId: null))
            throw new ValidationException("Another item (or pack) already uses this barcode.");

        var b = new ProductBarcode { ProductId = productId, Code = code, PackQtyMilli = packQtyMilli, Note = note, CreatedAt = DateTime.Now };
        db.ProductBarcodes.Add(b);
        await db.SaveChangesAsync();
        await AuditWriter.AppendAsync(db, admin, "barcode.added", "Product", productId, $"{product.Sku} extra barcode {code} = {packQtyMilli} milli");
        await tx.CommitAsync();
        return new ProductBarcodeDto(b.Id, b.ProductId, b.Code, b.PackQtyMilli, b.Note);
    }

    public async Task RemoveAsync(long barcodeId)
    {
        var admin = RequireAdmin();
        await using var db = Factory.Create();
        var b = await db.ProductBarcodes.FirstOrDefaultAsync(x => x.Id == barcodeId) ?? throw new ValidationException("Barcode not found.");
        db.ProductBarcodes.Remove(b);
        await db.SaveChangesAsync();
        await AuditWriter.AppendAsync(db, admin, "barcode.removed", "Product", b.ProductId, $"extra barcode {b.Code} removed");
    }

    public async Task<string> GenerateAsync()
    {
        RequireAdmin();
        await using var db = Factory.Create();
        var prefix = Ean13.InternalPrefix;
        var own = await db.Products.AsNoTracking().Where(p => p.Barcode != null && p.Barcode.StartsWith(prefix)).Select(p => p.Barcode!).ToListAsync();
        var extra = await db.ProductBarcodes.AsNoTracking().Where(b => b.Code.StartsWith(prefix)).Select(b => b.Code).ToListAsync();
        var max = own.Concat(extra).Select(Ean13.SequenceOf).Where(s => s.HasValue).Select(s => s!.Value).DefaultIfEmpty(0).Max();
        return Ean13.FromSequence(max + 1);
    }

    /// <summary>A code is taken if any item uses it as its barcode or its code (SKU), or any pack uses it.</summary>
    internal static async Task<bool> BarcodeInUseAsync(AppDbContext db, string code, long? ignoreProductId) =>
        await db.Products.AnyAsync(p => (p.Barcode == code || p.Sku == code) && p.Id != ignoreProductId)
        || await db.ProductBarcodes.AnyAsync(b => b.Code == code && b.ProductId != ignoreProductId);
}

internal sealed class RepackService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), IRepackService
{
    private const long MaxQtyMilli = 1_000_000_000_000;

    private sealed record Calc(Product Source, Product Target, long SourceValue, long TargetCost, long TargetValue);

    private static async Task<Calc> CalcAsync(AppDbContext db, RepackCommand cmd)
    {
        if (cmd.SourceQtyMilli <= 0 || cmd.TargetQtyMilli <= 0) throw new ValidationException("Enter how much stock is used and how many packs are made.");
        if (cmd.SourceQtyMilli > MaxQtyMilli || cmd.TargetQtyMilli > MaxQtyMilli) throw new ValidationException("That quantity is too large. Please check it.");
        if (cmd.SourceProductId == cmd.TargetProductId) throw new ValidationException("Choose a different item for the packs. Repacking turns one item into another.");
        if ((cmd.Note?.Length ?? 0) > 120) throw new ValidationException("The note is too long.");

        var source = await db.Products.FirstOrDefaultAsync(p => p.Id == cmd.SourceProductId && p.IsActive) ?? throw new ValidationException("Choose the bulk item.");
        var target = await db.Products.FirstOrDefaultAsync(p => p.Id == cmd.TargetProductId && p.IsActive) ?? throw new ValidationException("Choose the pack item.");
        if (cmd.SourceQtyMilli > source.QtyOnHandMilli)
            throw new ValidationException($"Only {Qty.Format(Math.Max(0, source.QtyOnHandMilli))} {source.Unit} of {source.Name} is in stock.");
        if (source.AvgCostPaise <= 0)
            throw new ValidationException($"{source.Name} has no cost recorded, so the cost of the packs cannot be worked out. Enter its stock with a cost first.");

        var value = Costing.Value(cmd.SourceQtyMilli, source.AvgCostPaise);
        var unitCost = Money.RoundHalfUp((decimal)value * Qty.Unit / cmd.TargetQtyMilli);
        if (unitCost <= 0) throw new ValidationException("That many packs from so little stock would cost nothing each. Check the quantities.");
        return new Calc(source, target, value, unitCost, Costing.Value(cmd.TargetQtyMilli, unitCost));
    }

    public async Task<RepackPreview> PreviewAsync(RepackCommand cmd)
    {
        RequireAdmin();
        await using var db = Factory.Create();
        var c = await CalcAsync(db, cmd);
        return new RepackPreview(c.Source.Name, c.Target.Name, c.Source.QtyOnHandMilli, c.Source.AvgCostPaise, c.SourceValue, c.TargetCost, c.TargetValue);
    }

    public async Task<RepackResult> RepackAsync(RepackCommand cmd)
    {
        var admin = RequireAdmin();
        await using var db = Factory.Create();
        var existing = await db.Repacks.AsNoTracking().FirstOrDefaultAsync(r => r.CommandId == cmd.CommandId);
        if (existing != null) return new RepackResult(existing.Id, existing.TargetUnitCostPaise, existing.ValuePaise, true);

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var now = DateTime.Now;
            await Posting.EnsurePeriodOpenAsync(db, now);
            var c = await CalcAsync(db, cmd);
            var s = c.Source; var t = c.Target;

            var variance = Costing.NegativeStockVariance(t.QtyOnHandMilli, t.AvgCostPaise, cmd.TargetQtyMilli, c.TargetCost);
            var sourceCost = s.AvgCostPaise;
            (s.QtyOnHandMilli, s.AvgCostPaise) = Costing.Remove(s.QtyOnHandMilli, s.AvgCostPaise, cmd.SourceQtyMilli, sourceCost);
            (t.QtyOnHandMilli, t.AvgCostPaise) = Costing.Receive(t.QtyOnHandMilli, t.AvgCostPaise, cmd.TargetQtyMilli, c.TargetCost);

            var note = string.IsNullOrWhiteSpace(cmd.Note) ? null : cmd.Note.Trim();
            var r = new Repack
            {
                CommandId = cmd.CommandId, Date = now, SourceProductId = s.Id, TargetProductId = t.Id, SourceQtyMilli = cmd.SourceQtyMilli,
                TargetQtyMilli = cmd.TargetQtyMilli, SourceUnitCostPaise = sourceCost, TargetUnitCostPaise = c.TargetCost, ValuePaise = c.SourceValue,
                Note = note, UserId = admin.Id,
            };
            db.Repacks.Add(r);
            await db.SaveChangesAsync();
            db.StockMovements.Add(new StockMovement { ProductId = s.Id, Date = now, QtyDeltaMilli = -cmd.SourceQtyMilli, UnitCostPaise = sourceCost, Type = StockMovementType.RepackOut, RefType = "Repack", RefId = r.Id, Note = note ?? $"Repacked into {t.Name}" });
            db.StockMovements.Add(new StockMovement { ProductId = t.Id, Date = now, QtyDeltaMilli = cmd.TargetQtyMilli, UnitCostPaise = c.TargetCost, Type = StockMovementType.RepackIn, RefType = "Repack", RefId = r.Id, Note = note ?? $"Repacked from {s.Name}" });
            await db.SaveChangesAsync();

            // The stock value moves from one item to the other, so the inventory account does not change except for whole-paise rounding.
            var rounding = c.SourceValue - c.TargetValue;
            if (rounding != 0)
                await Posting.PostAsync(db, now, $"Repack rounding: {s.Name} to {t.Name}", "RepackRounding", r.Id, admin.Id, PostingBuilder.ForCostVariance(rounding));
            if (variance != 0)
                await Posting.PostAsync(db, now, $"Cost variance on negative stock: {t.Name}", "StockVariance", r.Id, admin.Id, PostingBuilder.ForCostVariance(variance));
            await AuditWriter.AppendAsync(db, admin, "stock.repacked", "Repack", r.Id,
                $"{cmd.SourceQtyMilli} milli of {s.Sku} into {cmd.TargetQtyMilli} milli of {t.Sku} at cost {c.TargetCost} (value {c.SourceValue})");
            await tx.CommitAsync();
            return new RepackResult(r.Id, c.TargetCost, c.SourceValue, false);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync();
            await using var db2 = Factory.Create();
            var again = await db2.Repacks.AsNoTracking().FirstOrDefaultAsync(x => x.CommandId == cmd.CommandId);
            if (again != null) return new RepackResult(again.Id, again.TargetUnitCostPaise, again.ValuePaise, true);
            throw;
        }
    }

    public async Task<IReadOnlyList<RepackListItem>> ListAsync(int take = 50)
    {
        RequireAdmin();
        await using var db = Factory.Create();
        return await (from r in db.Repacks.AsNoTracking()
                      join s in db.Products on r.SourceProductId equals s.Id
                      join t in db.Products on r.TargetProductId equals t.Id
                      orderby r.Id descending
                      select new RepackListItem(r.Id, r.Date, s.Name, r.SourceQtyMilli, t.Name, r.TargetQtyMilli, r.TargetUnitCostPaise, r.ValuePaise, r.Note))
            .Take(Math.Clamp(take, 1, 200)).ToListAsync();
    }
}
