using BillingSoftware.Domain;

namespace BillingSoftware.Application;

/// <summary>
/// An extra barcode for an item. Scanning it adds <see cref="PackQtyMilli"/> of the item to the bill (for example a carton of 12,
/// or a 250 g pack of an item sold by the kilo). The price is the item's price for that quantity.
/// </summary>
public sealed record ProductBarcodeDto(long Id, long ProductId, string Code, long PackQtyMilli, string? Note);

/// <summary>What a scan found: the item and how much of it the scanned code stands for (one unit for the item's own barcode or code).</summary>
public sealed record ScanResult(ProductDto Product, long QtyMilli, bool IsPack);

public interface IBarcodeService
{
    /// <summary>Looks up a scanned or typed code: the item's own barcode, its code (SKU), or an extra barcode.</summary>
    Task<ScanResult?> ScanAsync(string code);
    Task<IReadOnlyList<ProductBarcodeDto>> ListAsync(long productId);
    Task<ProductBarcodeDto> AddAsync(long productId, string code, long packQtyMilli, string? note);
    Task RemoveAsync(long barcodeId);
    /// <summary>The next unused shop-made EAN-13 code (it is reserved only when saved on an item).</summary>
    Task<string> GenerateAsync();
}

/// <summary>
/// Turns bulk stock into smaller packs that are sold as their own item (own barcode, price and GST), for example 25 kg of rice
/// into 100 packs of 250 g. Stock moves from one item to the other at cost, so no profit or loss is made.
/// </summary>
public sealed record RepackCommand(Guid CommandId, long SourceProductId, long SourceQtyMilli, long TargetProductId, long TargetQtyMilli, string? Note);

public sealed record RepackPreview(string SourceName, string TargetName, long SourceOnHandMilli, long SourceUnitCostPaise, long SourceValuePaise,
    long TargetUnitCostPaise, long TargetValuePaise);

public sealed record RepackResult(long Id, long TargetUnitCostPaise, long ValuePaise, bool AlreadyExisted);

public sealed record RepackListItem(long Id, DateTime Date, string Source, long SourceQtyMilli, string Target, long TargetQtyMilli,
    long TargetUnitCostPaise, long ValuePaise, string? Note);

public interface IRepackService
{
    Task<RepackPreview> PreviewAsync(RepackCommand command);
    Task<RepackResult> RepackAsync(RepackCommand command);
    Task<IReadOnlyList<RepackListItem>> ListAsync(int take = 50);
}
