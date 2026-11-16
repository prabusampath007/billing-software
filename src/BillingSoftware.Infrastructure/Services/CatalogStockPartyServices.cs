using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

internal sealed class CatalogService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), ICatalogService
{
    public async Task<IReadOnlyList<ProductDto>> SearchAsync(string query, int take = 30)
    {
        RequireUser();
        query = (query ?? "").Trim();
        take = Math.Clamp(take, 1, 200);
        await using var db = Factory.Create();
        IQueryable<Product> q = db.Products.AsNoTracking().Where(p => p.IsActive);
        if (query.Length > 0)
        {
            // Exact barcode or SKU hits come first; then name contains / SKU starts with.
            var like = Like.Contains(query);
            var starts = Like.StartsWith(query);
            q = q.Where(p => p.Barcode == query || p.Sku == query
                             || db.ProductBarcodes.Any(b => b.ProductId == p.Id && b.Code == query)
                             || EF.Functions.Like(p.Name, like, Like.Escape) || EF.Functions.Like(p.Sku, starts, Like.Escape)
                             || (p.TamilName != null && EF.Functions.Like(p.TamilName, like, Like.Escape)));
        }
        var rows = await q.OrderBy(p => p.Barcode == query ? 0 : p.Sku == query ? 1 : 2).ThenBy(p => p.Name).Take(take).ToListAsync();
        return rows.Select(Map).ToList();
    }

    public async Task<ProductDto?> GetByBarcodeAsync(string barcode)
    {
        RequireUser();
        barcode = (barcode ?? "").Trim();
        if (barcode.Length == 0) return null;
        await using var db = Factory.Create();
        var p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.IsActive && (x.Barcode == barcode || x.Sku == barcode));
        return p == null ? null : Map(p);
    }

    public Task<ScanResult?> ScanAsync(string code) => new BarcodeService(Factory, Current).ScanAsync(code);

    public async Task<ProductDto?> GetAsync(long id)
    {
        RequireUser();
        await using var db = Factory.Create();
        var p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return p == null ? null : Map(p);
    }

    public async Task<IReadOnlyList<ProductDto>> ListAsync(int skip, int take, bool includeInactive = false)
    {
        RequireUser();
        await using var db = Factory.Create();
        var q = db.Products.AsNoTracking().AsQueryable();
        if (!includeInactive) q = q.Where(p => p.IsActive);
        var rows = await q.OrderBy(p => p.Name).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 500)).ToListAsync();
        return rows.Select(Map).ToList();
    }

    public async Task<ProductDto> SaveAsync(ProductDto dto)
    {
        var admin = RequireAdmin();
        var sku = (dto.Sku ?? "").Trim();
        var name = (dto.Name ?? "").Trim();
        var barcode = string.IsNullOrWhiteSpace(dto.Barcode) ? null : dto.Barcode.Trim();
        if (name.Length == 0) throw new ValidationException("Item name is required.");
        if (string.IsNullOrWhiteSpace(dto.Unit)) throw new ValidationException("Unit is required (for example pcs, kg).");
        if (dto.SellingPricePaise < 0) throw new ValidationException("Selling price cannot be negative.");
        if (dto.GstRateBp < 0 || dto.GstRateBp > 5000) throw new ValidationException("GST rate must be between 0% and 50%.");
        if (dto.MrpPaise is < 0) throw new ValidationException("MRP cannot be negative.");
        var tamil = string.IsNullOrWhiteSpace(dto.TamilName) ? null : dto.TamilName.Trim();
        if ((tamil?.Length ?? 0) > 120) throw new ValidationException("The Tamil name is too long (over 120 characters).");

        await using var db = Factory.Create();
        // The item code is optional: a new item without one gets the next free code, and an existing item keeps the code it has.
        if (sku.Length == 0)
            sku = dto.Id == 0 ? await SkuGenerator.NextAsync(db)
                : await db.Products.AsNoTracking().Where(p => p.Id == dto.Id).Select(p => p.Sku).FirstOrDefaultAsync() ?? throw new ValidationException("Item not found.");
        // A code is checked when it is new or changed; an item saved earlier with an odd code can still be edited (price, name) without retyping it.
        var storedBarcode = dto.Id == 0 ? null : await db.Products.AsNoTracking().Where(p => p.Id == dto.Id).Select(p => p.Barcode).FirstOrDefaultAsync();
        if (barcode != null && barcode != storedBarcode) barcode = BarcodeService.CleanCode(barcode);
        if (await db.Products.AnyAsync(p => p.Sku == sku && p.Id != dto.Id)) throw new ValidationException("Another item already uses this code (SKU).");
        if (barcode != null && await db.Products.AnyAsync(p => p.Barcode == barcode && p.Id != dto.Id)) throw new ValidationException("Another item already uses this barcode.");
        if (barcode != null && await db.ProductBarcodes.AnyAsync(b => b.Code == barcode && b.ProductId != dto.Id)) throw new ValidationException("A pack of another item already uses this barcode.");
        if (await db.ProductBarcodes.AnyAsync(b => b.Code == sku && b.ProductId != dto.Id)) throw new ValidationException("A pack of another item already uses this code (SKU) as its barcode.");

        Product entity;
        string action;
        if (dto.Id == 0) { entity = new Product(); db.Products.Add(entity); action = "product.created"; }
        else
        {
            entity = await db.Products.FindAsync(dto.Id) ?? throw new ValidationException("Item not found.");
            action = "product.updated";
        }

        var before = dto.Id == 0 ? "" : $"price {entity.SellingPricePaise}, gst {entity.GstRateBp}, active {entity.IsActive}; ";
        entity.Sku = sku; entity.Name = name; entity.TamilName = tamil; entity.Barcode = barcode; entity.HsnCode = dto.HsnCode?.Trim();
        entity.GstRateBp = dto.GstRateBp; entity.Unit = dto.Unit.Trim(); entity.SellingPricePaise = dto.SellingPricePaise;
        entity.MrpPaise = dto.MrpPaise; entity.PriceInclusive = dto.PriceInclusive; entity.IsActive = dto.IsActive;
        // Quantity and average cost are never edited here: they change only through stock movements.

        await db.SaveChangesAsync();
        await AuditWriter.AppendAsync(db, admin, action, "Product", entity.Id,
            $"{before}now: {entity.Sku} '{entity.Name}' price {entity.SellingPricePaise}, gst {entity.GstRateBp}, active {entity.IsActive}");
        return Map(entity);
    }

    private ProductDto Map(Product p) => new(p.Id, p.Sku, p.Barcode, p.Name, p.HsnCode, p.GstRateBp, p.Unit,
        p.SellingPricePaise, p.MrpPaise, p.PriceInclusive,
        Current.IsAdmin ? p.AvgCostPaise : 0,                      // cashiers never see purchase cost
        p.QtyOnHandMilli, p.IsActive, p.TamilName);
}

internal sealed class StockService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), IStockService
{
    public async Task ReceiveOpeningStockAsync(long productId, long qtyMilli, long unitCostPaise)
    {
        var admin = RequireAdmin();
        if (qtyMilli <= 0) throw new ValidationException("Quantity must be greater than zero.");
        if (unitCostPaise < 0) throw new ValidationException("Cost cannot be negative.");

        await using var db = Factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync();
        var now = DateTime.Now;
        await Posting.EnsurePeriodOpenAsync(db, now);
        var p = await db.Products.FindAsync(productId) ?? throw new ValidationException("Item not found.");

        var variance = Costing.NegativeStockVariance(p.QtyOnHandMilli, p.AvgCostPaise, qtyMilli, unitCostPaise);
        (p.QtyOnHandMilli, p.AvgCostPaise) = Costing.Receive(p.QtyOnHandMilli, p.AvgCostPaise, qtyMilli, unitCostPaise);
        var value = Costing.Value(qtyMilli, unitCostPaise);
        var move = new StockMovement { ProductId = p.Id, Date = now, QtyDeltaMilli = qtyMilli, UnitCostPaise = unitCostPaise, Type = StockMovementType.Opening, RefType = "Opening", Note = "Opening stock" };
        db.StockMovements.Add(move);
        await db.SaveChangesAsync();

        // Stock with no cost (a free sample) has no value to book; a zero-value entry is not a valid journal.
        if (value > 0)
            await Posting.PostAsync(db, now, $"Opening stock: {p.Name}", "StockIn", move.Id, admin.Id, PostingBuilder.ForOpeningStock(value));
        if (variance != 0)
            await Posting.PostAsync(db, now, $"Cost variance on negative stock: {p.Name}", "StockVariance", move.Id, admin.Id, PostingBuilder.ForCostVariance(variance));
        await AuditWriter.AppendAsync(db, admin, "stock.opening", "Product", p.Id, $"qty {qtyMilli} at cost {unitCostPaise}, variance {variance}");
        await tx.CommitAsync();
    }

    public async Task AdjustAsync(long productId, long deltaMilli, string reason)
    {
        var admin = RequireAdmin();
        if (deltaMilli == 0) throw new ValidationException("Adjustment quantity cannot be zero.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3) throw new ValidationException("A reason is required for a stock adjustment.");

        await using var db = Factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync();
        var now = DateTime.Now;
        await Posting.EnsurePeriodOpenAsync(db, now);
        var p = await db.Products.FindAsync(productId) ?? throw new ValidationException("Item not found.");
        if (deltaMilli < 0 && p.QtyOnHandMilli + deltaMilli < 0)
            throw new ValidationException("The adjustment is more than the stock on hand.");

        p.QtyOnHandMilli += deltaMilli;
        var value = Costing.Value(Math.Abs(deltaMilli), p.AvgCostPaise);
        var move = new StockMovement
        {
            ProductId = p.Id, Date = now, QtyDeltaMilli = deltaMilli, UnitCostPaise = p.AvgCostPaise,
            Type = deltaMilli < 0 ? StockMovementType.Loss : StockMovementType.Gain, RefType = "Adjustment", Note = reason.Trim(),
        };
        db.StockMovements.Add(move);
        await db.SaveChangesAsync();
        await Posting.PostAsync(db, now, $"Stock adjustment: {p.Name} ({reason.Trim()})", "StockAdjust", move.Id, admin.Id,
            PostingBuilder.ForStockLoss(deltaMilli < 0 ? value : -value));
        await AuditWriter.AppendAsync(db, admin, "stock.adjusted", "Product", p.Id, $"delta {deltaMilli}; reason: {reason.Trim()}");
        await tx.CommitAsync();
    }
}

internal sealed class PartyService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), IPartyService
{
    public async Task<IReadOnlyList<PartyDto>> SearchAsync(string query, int take = 20)
    {
        RequireUser();
        query = (query ?? "").Trim();
        await using var db = Factory.Create();
        var q = db.Parties.AsNoTracking().Where(p => p.IsActive);
        if (query.Length > 0)
        {
            var like = Like.Contains(query);
            q = q.Where(p => EF.Functions.Like(p.Name, like, Like.Escape) || (p.Phone != null && EF.Functions.Like(p.Phone, like, Like.Escape)));
        }
        var rows = await q.OrderBy(p => p.Name).Take(Math.Clamp(take, 1, 100)).ToListAsync();
        var result = new List<PartyDto>();
        foreach (var p in rows) result.Add(new PartyDto(p.Id, p.Name, p.Phone, p.Gstin, p.StateCode, await OutstandingAsync(db, p.Id), p.CreditLimitPaise));
        return result;
    }

    public async Task<PartyDto> SaveAsync(PartyDto dto)
    {
        var user = RequireUser();
        var name = (dto.Name ?? "").Trim();
        if (name.Length == 0) throw new ValidationException("Customer name is required.");
        if (dto.Id != 0 && !user.Role.Equals(UserRole.Admin)) throw new PermissionDeniedException("Only the Admin can edit an existing customer.");
        if (!string.IsNullOrWhiteSpace(dto.Gstin) && !GstValidation.IsValidGstin(dto.Gstin)) throw new ValidationException("The GSTIN format is not valid.");
        if (dto.CreditLimitPaise < 0) throw new ValidationException("The credit limit cannot be negative.");
        if (dto.CreditLimitPaise > 100_000_000_00) throw new ValidationException("That credit limit is too large. Please check it.");
        if (dto.CreditLimitPaise != 0 && !user.Role.Equals(UserRole.Admin)) throw new PermissionDeniedException("Only the Admin can set a credit limit.");

        await using var db = Factory.Create();
        Party entity;
        if (dto.Id == 0) { entity = new Party(); db.Parties.Add(entity); }
        else entity = await db.Parties.FindAsync(dto.Id) ?? throw new ValidationException("Customer not found.");
        var gstin = string.IsNullOrWhiteSpace(dto.Gstin) ? null : dto.Gstin.Trim().ToUpperInvariant();
        var state = string.IsNullOrWhiteSpace(dto.StateCode) ? gstin?[..2] : dto.StateCode.Trim();
        if (gstin != null && state != null && !gstin.StartsWith(state, StringComparison.Ordinal))
            throw new ValidationException("The first two digits of the GSTIN must match the customer's state.");
        var limitChanged = dto.Id != 0 && entity.CreditLimitPaise != dto.CreditLimitPaise;
        entity.Name = name; entity.Phone = dto.Phone?.Trim(); entity.Gstin = gstin; entity.StateCode = state; entity.CreditLimitPaise = dto.CreditLimitPaise;
        await db.SaveChangesAsync();
        await AuditWriter.AppendAsync(db, user, dto.Id == 0 ? "party.created" : "party.updated", "Party", entity.Id,
            $"Customer #{entity.Id}" + (dto.Id == 0 && entity.CreditLimitPaise > 0 || limitChanged ? $"; credit limit {entity.CreditLimitPaise} paise" : ""));
        return new PartyDto(entity.Id, entity.Name, entity.Phone, entity.Gstin, entity.StateCode, await OutstandingAsync(db, entity.Id), entity.CreditLimitPaise);
    }

    public async Task ReceivePaymentAsync(long partyId, long amountPaise, PaymentMode mode)
    {
        var user = RequireUser();
        if (amountPaise <= 0) throw new ValidationException("Amount must be greater than zero.");
        PaymentModeRules.EnsureMoney(mode);

        await using var db = Factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync();
        var now = DateTime.Now;
        await Posting.EnsurePeriodOpenAsync(db, now);
        var party = await db.Parties.FindAsync(partyId) ?? throw new ValidationException("Customer not found.");
        var outstanding = await OutstandingAsync(db, partyId);
        if (amountPaise > outstanding)
            throw new ValidationException($"Amount is more than the outstanding balance ({Money.Format(outstanding)}).");

        var receipt = new Receipt { PartyId = partyId, Date = now, Mode = mode, AmountPaise = amountPaise, ReceivedByUserId = user.Id };
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync();
        await Posting.PostAsync(db, now, $"Receipt from {party.Name}", "Receipt", receipt.Id, user.Id, PostingBuilder.ForReceipt(mode, amountPaise));
        await AuditWriter.AppendAsync(db, user, "receipt.created", "Receipt", receipt.Id, $"party {partyId}, {amountPaise} paise, {mode}");
        await tx.CommitAsync();
    }

    internal static async Task<long> OutstandingAsync(AppDbContext db, long partyId)
    {
        var credit = await (from s in db.Sales
                            where s.PartyId == partyId && s.Status == SaleStatus.Posted
                            from p in s.Payments
                            where p.Mode == PaymentMode.Credit
                            select (long?)p.AmountPaise).SumAsync() ?? 0;
        var paid = await db.Receipts.Where(r => r.PartyId == partyId).SumAsync(r => (long?)r.AmountPaise) ?? 0;
        var credited = await db.SaleReturns.Where(r => r.PartyId == partyId && r.RefundMode == PaymentMode.Credit).SumAsync(r => (long?)r.TotalPaise) ?? 0;
        return credit - paid - credited + await OpeningDues.CustomerAsync(db, partyId);
    }
}

/// <summary>Case-insensitive LIKE patterns with wildcard characters escaped (search text is never treated as a pattern).</summary>
internal static class Like
{
    public const string Escape = "\\";
    private static string Esc(string t) => t.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    public static string Contains(string t) => "%" + Esc(t) + "%";
    public static string StartsWith(string t) => Esc(t) + "%";
}

public static class GstValidation
{
    private static readonly System.Text.RegularExpressions.Regex Pattern =
        new(@"^\d{2}[A-Z]{5}\d{4}[A-Z][A-Z\d]Z[A-Z\d]$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsValidGstin(string? gstin) => !string.IsNullOrWhiteSpace(gstin) && Pattern.IsMatch(gstin.Trim().ToUpperInvariant());
}

/// <summary>Makes an item code when the owner leaves it empty: ITEM-00001, ITEM-00002 ... never one that is already taken.</summary>
internal static class SkuGenerator
{
    public static async Task<string> NextAsync(AppDbContext db, ISet<string>? alsoTaken = null)
    {
        var existing = await db.Products.AsNoTracking().Where(p => p.Sku.StartsWith("ITEM-")).Select(p => p.Sku).ToListAsync();
        var n = existing.Select(s => int.TryParse(s.AsSpan(5), out var v) ? v : 0).DefaultIfEmpty(0).Max() + 1;
        while (true)
        {
            var candidate = $"ITEM-{n:D5}";
            if ((alsoTaken == null || !alsoTaken.Contains(candidate.ToLowerInvariant())) && !await BarcodeService.BarcodeInUseAsync(db, candidate, null)) return candidate;
            n++;
        }
    }
}
