using BillingSoftware.Domain;

namespace BillingSoftware.Application;

public sealed record NamedAmount(string Label, long AmountPaise);

public sealed record CashierLine(string Cashier, int Bills, long TotalPaise);

/// <summary>
/// The Z-report: everything since the previous close. Cash lines are signed (money in is positive, money out negative)
/// and add up to the expected cash in the drawer.
/// </summary>
public sealed record DayCloseReport(
    DateTime PeriodStart, DateTime PeriodEnd, string ClosedBy,
    int BillsMade, int BillsVoided, int ReturnCount,
    long GrossSalesPaise, long VoidedPaise, long ReturnsPaise, long NetSalesPaise,
    long DiscountPaise, long CgstPaise, long SgstPaise, long IgstPaise,
    IReadOnlyList<NamedAmount> SalesByMode, IReadOnlyList<NamedAmount> RefundsByMode, IReadOnlyList<CashierLine> ByCashier,
    IReadOnlyList<NamedAmount> CashLines, long OpeningCashPaise, long ExpectedCashPaise, long CountedCashPaise, long VariancePaise, string? Note);

public sealed record DayClosePreview(DayCloseReport Report, bool IsFirstClose, DateTime? LastClosedAt, long? LastCountedPaise, bool OpeningCashRecorded = false);

public sealed record DayCloseCommand(Guid CommandId, long CountedCashPaise, long OpeningFloatPaise = 0, string? Note = null);

public sealed record DayCloseResult(long Id, long ExpectedPaise, long CountedPaise, long VariancePaise, bool AlreadyExisted);

public sealed record DayCloseListItem(long Id, DateTime PeriodEnd, string ClosedBy, long CountedPaise, long VariancePaise);

public sealed record CashMovementDto(long Id, DateTime Date, CashMovementType Type, long AmountPaise, string Reason, string UserName)
{
    public bool IsMoneyIn => Type is CashMovementType.OwnerPayIn or CashMovementType.BankWithdrawal or CashMovementType.LoanReceived;
}

public interface IDayCloseService
{
    /// <summary>What the drawer should hold right now and the report as it would be if closed now. Nothing is saved. The opening cash only applies to the very first close.</summary>
    Task<DayClosePreview> PreviewAsync(long openingFloatPaise = 0);
    Task<DayCloseResult> CloseAsync(DayCloseCommand command);
    Task<DayCloseReport?> GetAsync(long id);
    Task<IReadOnlyList<DayCloseListItem>> ListAsync(int take = 30);
    Task<CashMovementDto> RecordMovementAsync(CashMovementType type, long amountPaise, string reason);
    /// <summary>Petty cash movements since the last close.</summary>
    Task<IReadOnlyList<CashMovementDto>> ListMovementsAsync();
}
