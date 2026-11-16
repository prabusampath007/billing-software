namespace BillingSoftware.Application;

public sealed record HeldLine(long ProductId, long QtyMilli, long DiscountPaise);

/// <summary>A bill put aside while the customer fetches something. It is not a sale: no stock moves and nothing is posted until it is completed.</summary>
public sealed record HoldBillCommand(string? Label, long? PartyId, IReadOnlyList<HeldLine> Lines);

public sealed record HeldBillSummary(long Id, DateTime CreatedAt, string UserName, string Label, string? CustomerName, int ItemCount);

public sealed record HeldBillDto(long Id, string Label, long? PartyId, string? CustomerName, IReadOnlyList<HeldLine> Lines);

public interface IHoldService
{
    Task<long> HoldAsync(HoldBillCommand command);
    /// <summary>Held bills, oldest first. Bills held for more than three days are removed (their prices would be out of date).</summary>
    Task<IReadOnlyList<HeldBillSummary>> ListAsync();
    /// <summary>Takes a held bill back to the counter: returns it and removes it from the held list.</summary>
    Task<HeldBillDto?> RecallAsync(long id);
    Task DiscardAsync(long id);
}
