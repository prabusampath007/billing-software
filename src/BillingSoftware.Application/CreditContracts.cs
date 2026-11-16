namespace BillingSoftware.Application;

/// <summary>One line of a customer's account. Debit = the customer owes more; credit = they owe less. Balance is what they owe after this line.</summary>
public sealed record LedgerEntry(DateTime Date, string Kind, string Reference, string Description, long DebitPaise, long CreditPaise, long BalancePaise);

public sealed record CustomerStatement(
    string CompanyName, long PartyId, string Name, string? Phone, string? Gstin, DateOnly From, DateOnly To,
    long BroughtForwardPaise, IReadOnlyList<LedgerEntry> Entries, long TotalDebitPaise, long TotalCreditPaise, long ClosingPaise, long CreditLimitPaise);

/// <summary>What one customer owes, split by how old the unpaid amounts are (payments are taken against the oldest amounts first).</summary>
public sealed record AgeingRow(
    long PartyId, string Name, string? Phone, long OutstandingPaise,
    long CurrentPaise, long Days31To60Paise, long Days61To90Paise, long Over90Paise,
    DateTime? OldestUnpaid, int OldestDays, long CreditLimitPaise)
{
    public bool OverLimit => CreditLimitPaise > 0 && OutstandingPaise > CreditLimitPaise;
}

public sealed record AgeingReport(
    string CompanyName, DateOnly AsAt, IReadOnlyList<AgeingRow> Rows,
    long TotalPaise, long CurrentPaise, long Days31To60Paise, long Days61To90Paise, long Over90Paise);

/// <summary>An unpaid bill (or opening balance) for a reminder: what is still unpaid on it and how many days old it is.</summary>
public sealed record UnpaidItem(string Reference, DateTime Date, long UnpaidPaise, int Days);

public sealed record CustomerReminder(
    string CompanyName, string? CompanyPhone, string CustomerName, DateOnly AsAt, long OutstandingPaise, IReadOnlyList<UnpaidItem> Items);

public interface ICreditService
{
    /// <summary>Everyone who owes money, with the amounts split by age. The total equals the customers' outstanding balances and the receivables in the books.</summary>
    Task<AgeingReport> GetAgeingAsync(DateOnly asAt);
    /// <summary>A customer's account for the dates given: balance brought forward, every bill, receipt and credit note, and the closing balance.</summary>
    Task<CustomerStatement> GetStatementAsync(long partyId, DateOnly from, DateOnly to);
    /// <summary>The unpaid bills of one customer, for a payment reminder.</summary>
    Task<CustomerReminder> GetReminderAsync(long partyId);
}
