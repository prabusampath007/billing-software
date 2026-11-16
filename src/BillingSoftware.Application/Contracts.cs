using BillingSoftware.Domain;

namespace BillingSoftware.Application;

// ---------- Exceptions ----------
public class BusinessRuleException(string message) : Exception(message);
public sealed class ValidationException(string message) : BusinessRuleException(message);
public sealed class PermissionDeniedException(string message = "You do not have permission to do this.") : BusinessRuleException(message);

public enum ApprovalReason
{
    DiscountAboveLimit = 1,
    PriceOverride = 2,
    NegativeStock = 3,
    VoidSale = 4,
    SaleReturn = 5,
    CreditLimit = 6,
}

/// <summary>Thrown when a Cashier attempts a restricted action; the UI shows the Admin approval prompt and retries.</summary>
public sealed class ApprovalRequiredException(ApprovalReason reason, string message) : BusinessRuleException(message)
{
    public ApprovalReason Reason { get; } = reason;
}

// ---------- Session ----------
public sealed record SessionUser(long Id, string Username, string DisplayName, UserRole Role);

public interface ICurrentUser
{
    SessionUser? User { get; }
    bool IsAdmin { get; }
    void SignIn(SessionUser user);
    void SignOut();
}

public sealed class CurrentUser : ICurrentUser
{
    public SessionUser? User { get; private set; }
    public bool IsAdmin => User?.Role == UserRole.Admin;
    public void SignIn(SessionUser user) => User = user;
    public void SignOut() => User = null;
}

// ---------- DTOs ----------
public sealed record UserDto(long Id, string Username, string DisplayName, UserRole Role, bool IsActive);

public sealed record ProductDto(
    long Id, string Sku, string? Barcode, string Name, string? HsnCode, int GstRateBp, string Unit,
    long SellingPricePaise, long? MrpPaise, bool PriceInclusive, long AvgCostPaise, long QtyOnHandMilli, bool IsActive, string? TamilName = null)
{
    /// <summary>The selling price as shown in lists.</summary>
    public string PriceText => BillingSoftware.Domain.Money.FormatPlain(SellingPricePaise);
}

public sealed record PartyDto(long Id, string Name, string? Phone, string? Gstin, string? StateCode, long OutstandingPaise, long CreditLimitPaise = 0)
{
    /// <summary>True when a limit is set and what the customer owes is over it.</summary>
    public bool IsOverLimit => CreditLimitPaise > 0 && OutstandingPaise > CreditLimitPaise;
}

public sealed record CompanyDto(string Name, string? Gstin, string StateCode, bool IsComposition, string? Address, string? Phone, bool GstEnabled = true)
{
    /// <summary>Whether bills show and collect GST: GST is on and the business is not on the composition scheme.</summary>
    public bool CollectsTax => GstEnabled && !IsComposition;
}

public sealed record SaleLineInput(long ProductId, long QtyMilli, long DiscountPaise = 0, long? UnitPriceOverridePaise = null);
public sealed record PaymentInput(PaymentMode Mode, long AmountPaise);

public sealed record CompleteSaleCommand(
    Guid CommandId,
    IReadOnlyList<SaleLineInput> Lines,
    IReadOnlyList<PaymentInput> Payments,
    long? PartyId = null,
    string? CustomerName = null,
    string? CustomerGstin = null,
    string? BuyerStateCode = null,
    long? ApprovedByUserId = null,
    string? ApprovalNote = null);

public sealed record SaleResult(long SaleId, string InvoiceNumber, long TotalPaise, long RoundOffPaise, bool AlreadyExisted);

public sealed record SaleLineDto(string ProductName, string? HsnCode, string Unit, long QtyMilli, long UnitPricePaise,
    long DiscountPaise, int GstRateBp, long TaxablePaise, long TaxPaise, long LineTotalPaise, string? TamilName = null);

public sealed record SaleDetailDto(
    long Id, string InvoiceNumber, DateTime SaleDate, string CashierName, string? CustomerName, string? CustomerGstin,
    bool IsInterState, long TaxablePaise, long CgstPaise, long SgstPaise, long IgstPaise, long DiscountPaise,
    long RoundOffPaise, long TotalPaise, SaleStatus Status, int ReprintCount,
    IReadOnlyList<SaleLineDto> Lines, IReadOnlyList<PaymentInput> Payments);

public sealed record SaleListItem(long Id, string InvoiceNumber, DateTime SaleDate, string CashierName, string? CustomerName,
    long TotalPaise, SaleStatus Status);

public sealed record DailySummary(
    DateOnly Date, int BillCount, int VoidedCount, long TaxablePaise, long CgstPaise, long SgstPaise, long IgstPaise,
    long DiscountPaise, long RoundOffPaise, long TotalPaise,
    IReadOnlyList<(PaymentMode Mode, long AmountPaise)> ByPaymentMode,
    IReadOnlyList<(string Cashier, int Bills, long TotalPaise)> ByCashier,
    int ReturnCount = 0, long ReturnTotalPaise = 0, int PurchaseCount = 0, long PurchaseTotalPaise = 0);

public sealed record TrialBalanceRow(string Code, string Name, long DebitPaise, long CreditPaise);
public sealed record TrialBalance(DateOnly AsOf, IReadOnlyList<TrialBalanceRow> Rows, long TotalDebitPaise, long TotalCreditPaise)
{
    public bool IsBalanced => TotalDebitPaise == TotalCreditPaise;
}

public sealed record StockRow(long ProductId, string Sku, string Name, string Unit, long QtyMilli, long AvgCostPaise, long ValuePaise);

public sealed record BackupResult(bool Success, string? Path, string? Checksum, string Message);
public sealed record BackupStatus(string? Folder, DateTime? LastBackupUtc, bool FolderOnSystemDisk, bool Overdue, string Summary);

// ---------- Services ----------
public interface IAuthService
{
    Task<bool> IsFirstRunAsync();
    Task<SessionUser> CreateInitialAdminAsync(string username, string displayName, string password);
    Task<SessionUser> LoginAsync(string username, string password);
    /// <summary>Verifies an Admin password for an on-the-spot approval and returns the approving Admin's id.</summary>
    Task<long> VerifyAdminApprovalAsync(string adminPassword);
    Task<IReadOnlyList<UserDto>> ListUsersAsync();
    Task<UserDto> CreateCashierAsync(string username, string displayName, string password);
    Task ResetPasswordAsync(long userId, string newPassword);
    Task SetActiveAsync(long userId, bool active);
}

public interface ICatalogService
{
    Task<IReadOnlyList<ProductDto>> SearchAsync(string query, int take = 30);
    Task<ProductDto?> GetByBarcodeAsync(string barcode);
    /// <summary>Looks up a scanned code: the item's own barcode or code (one unit), or an extra barcode (its pack quantity).</summary>
    Task<ScanResult?> ScanAsync(string code);
    Task<ProductDto?> GetAsync(long id);
    Task<IReadOnlyList<ProductDto>> ListAsync(int skip, int take, bool includeInactive = false);
    Task<ProductDto> SaveAsync(ProductDto product);
}

public interface IStockService
{
    Task ReceiveOpeningStockAsync(long productId, long qtyMilli, long unitCostPaise);
    Task AdjustAsync(long productId, long deltaMilli, string reason);
}

public interface IPartyService
{
    Task<IReadOnlyList<PartyDto>> SearchAsync(string query, int take = 20);
    Task<PartyDto> SaveAsync(PartyDto party);
    Task ReceivePaymentAsync(long partyId, long amountPaise, PaymentMode mode);
}

public interface ISalesService
{
    Task<SaleResult> CompleteSaleAsync(CompleteSaleCommand command);
    Task VoidSaleAsync(long saleId, string reason, long? approvedByUserId);
    Task<SaleDetailDto?> GetAsync(long saleId);
    /// <summary>Returns the sale for reprinting and records the reprint (the copy is marked DUPLICATE).</summary>
    Task<SaleDetailDto?> GetForReprintAsync(long saleId);
    Task<IReadOnlyList<SaleListItem>> ListAsync(DateOnly date);
}

public interface IReportService
{
    Task<DailySummary> DailySummaryAsync(DateOnly date);
    Task<TrialBalance> TrialBalanceAsync(DateOnly asOf);
    Task<IReadOnlyList<StockRow>> StockValuationAsync();
}

public interface ISettingsService
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task<CompanyDto?> GetCompanyAsync();
    Task SaveCompanyAsync(CompanyDto company);
}

public interface IBackupService
{
    Task<BackupResult> BackupNowAsync(string? folder = null);
    Task<BackupStatus> GetStatusAsync();
}

public interface IAuditService
{
    Task<(bool Valid, long? FirstBrokenId)> VerifyChainAsync();
}

public sealed record StartupResult(bool Ok, string? Error, string? RecoveryKey, bool NeedsRecoveryKey = false);
