using BillingSoftware.Domain;

namespace BillingSoftware.Application;

// ---------- Sales returns (credit notes) ----------
public sealed record ReturnableLineDto(long LineId, string Name, string Unit, long OriginalQtyMilli, long ReturnedQtyMilli, long UnitAmountPaise)
{
    public long RemainingMilli => OriginalQtyMilli - ReturnedQtyMilli;
}

public sealed record ReturnLineInput(long LineId, long QtyMilli, bool Restock = true);

public sealed record SaleReturnCommand(
    Guid CommandId, long SaleId, IReadOnlyList<ReturnLineInput> Lines, PaymentMode RefundMode, string Reason, long? ApprovedByUserId = null);

public sealed record ReturnPreview(long TaxablePaise, long TaxPaise, long RoundOffPaise, long TotalPaise);

public sealed record ReturnResult(long ReturnId, string ReturnNumber, long TotalPaise, bool AlreadyExisted);

public sealed record ReturnLineDto(string Name, string? HsnCode, string Unit, long QtyMilli, long TaxablePaise, long TaxPaise, long TotalPaise);

public sealed record ReturnDetailDto(
    long Id, string ReturnNumber, DateTime Date, string OriginalNumber, string PartyName, string Kind,
    long TaxablePaise, long CgstPaise, long SgstPaise, long IgstPaise, long RoundOffPaise, long TotalPaise,
    PaymentMode RefundMode, string Reason, IReadOnlyList<ReturnLineDto> Lines);

public sealed record ReturnListItem(long Id, string ReturnNumber, DateTime Date, string OriginalNumber, long TotalPaise);

public interface ISalesReturnService
{
    Task<IReadOnlyList<ReturnableLineDto>> GetReturnableAsync(long saleId);
    Task<ReturnPreview> PreviewAsync(SaleReturnCommand command);
    Task<ReturnResult> ReturnAsync(SaleReturnCommand command);
    Task<ReturnDetailDto?> GetAsync(long returnId);
    Task<IReadOnlyList<ReturnListItem>> ListAsync(DateOnly date);
}

// ---------- Suppliers and purchases ----------
public sealed record SupplierDto(long Id, string Name, string? Phone, string? Gstin, string? StateCode, long OutstandingPaise);

public sealed record PurchaseLineInput(
    long ProductId, long QtyMilli, long UnitCostPaise, int? GstRateBp = null, long DiscountPaise = 0, bool ItcEligible = true);

public sealed record CompletePurchaseCommand(
    Guid CommandId, long SupplierId, string SupplierInvoiceNo, DateTime? SupplierInvoiceDate,
    IReadOnlyList<PurchaseLineInput> Lines, IReadOnlyList<PaymentInput> Payments);

public sealed record PurchaseResult(long PurchaseId, string PurchaseNumber, long TotalPaise, bool AlreadyExisted);

public sealed record PurchaseLineDto(string Name, string? HsnCode, string Unit, long QtyMilli, long UnitCostPaise, long DiscountPaise,
    int GstRateBp, long TaxablePaise, long TaxPaise, long TotalPaise, bool ItcEligible);

public sealed record PurchaseDetailDto(
    long Id, string PurchaseNumber, DateTime Date, string SupplierName, string SupplierInvoiceNo, DateTime? SupplierInvoiceDate,
    bool IsInterState, long TaxablePaise, long CgstPaise, long SgstPaise, long IgstPaise, long RoundOffPaise, long TotalPaise,
    IReadOnlyList<PurchaseLineDto> Lines, IReadOnlyList<PaymentInput> Payments);

public sealed record PurchaseListItem(long Id, string PurchaseNumber, DateTime Date, string SupplierName, string SupplierInvoiceNo, long TotalPaise);

public sealed record PurchaseReturnCommand(Guid CommandId, long PurchaseId, IReadOnlyList<ReturnLineInput> Lines, PaymentMode RefundMode, string Reason);

public interface ISupplierService
{
    Task<IReadOnlyList<SupplierDto>> SearchAsync(string query, int take = 30);
    Task<SupplierDto> SaveAsync(SupplierDto supplier);
    Task PayAsync(long supplierId, long amountPaise, PaymentMode mode);
}

public interface IPurchaseService
{
    Task<PurchaseResult> CompleteAsync(CompletePurchaseCommand command);
    Task<PurchaseDetailDto?> GetAsync(long purchaseId);
    Task<IReadOnlyList<PurchaseListItem>> ListAsync(DateOnly from, DateOnly to);
    Task<IReadOnlyList<ReturnableLineDto>> GetReturnableAsync(long purchaseId);
    Task<ReturnPreview> PreviewReturnAsync(PurchaseReturnCommand command);
    Task<ReturnResult> ReturnAsync(PurchaseReturnCommand command);
    Task<ReturnDetailDto?> GetReturnAsync(long returnId);
    Task<IReadOnlyList<ReturnListItem>> ListReturnsAsync(DateOnly from, DateOnly to);
}
