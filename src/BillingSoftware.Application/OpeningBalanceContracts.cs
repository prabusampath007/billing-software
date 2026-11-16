using BillingSoftware.Domain;

namespace BillingSoftware.Application;

/// <summary>
/// One balance the business already had when it started using the software.
/// For a customer or supplier due, <see cref="PartyId"/> / <see cref="SupplierId"/> says whose it is. For a fixed asset,
/// <see cref="AmountPaise"/> is its value today (after depreciation so far) and <see cref="CostPaise"/> what it originally cost.
/// </summary>
public sealed record AddOpeningCommand(
    Guid CommandId, OpeningKind Kind, DateOnly Date, long? PartyId, long? SupplierId, string Name, long AmountPaise, long? CostPaise, string? Note);

public sealed record OpeningItem(
    long Id, OpeningKind Kind, DateOnly Date, string Name, long AmountPaise, long? CostPaise, string? Note, bool IsVoided, string? VoidReason)
{
    public static string KindLabel(OpeningKind k) => k switch
    {
        OpeningKind.Cash => "Cash in hand",
        OpeningKind.Bank => "Bank / UPI balance",
        OpeningKind.CustomerDue => "Customer owes you",
        OpeningKind.SupplierDue => "You owe a supplier",
        OpeningKind.Loan => "Loan taken",
        OpeningKind.FixedAsset => "Furniture / equipment",
        _ => k.ToString(),
    };
    public string KindText => KindLabel(Kind);
}

public sealed record OpeningSummary(
    IReadOnlyList<OpeningItem> Items,
    long CashPaise, long BankPaise, long CustomerDuesPaise, long FixedAssetsPaise,
    long SupplierDuesPaise, long LoansPaise,
    long OpeningStockPaise,
    long TotalAssetsPaise, long TotalLiabilitiesPaise,
    long OwnerFundsPaise,
    bool DayClosed)
{
    /// <summary>What the owner started with: assets (including stock) less liabilities.</summary>
    public long NetPaise => TotalAssetsPaise - TotalLiabilitiesPaise;
}

public interface IOpeningBalanceService
{
    Task<OpeningSummary> GetSummaryAsync();
    Task<long> AddAsync(AddOpeningCommand command);
    /// <summary>Cancels an entry made by mistake. Refused if the balance has since been used (paid, repaid, depreciated or counted at day end).</summary>
    Task VoidAsync(long id, string reason);
}
