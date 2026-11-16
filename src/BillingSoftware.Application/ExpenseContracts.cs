using BillingSoftware.Domain;

namespace BillingSoftware.Application;

public sealed record ExpenseCategoryDto(long Id, string Name, string AccountCode, bool IsActive, bool IsDefault);

public sealed record RecordExpenseCommand(
    Guid CommandId, DateOnly Date, long CategoryId, string PayTo, string Description, string? Reference, long AmountPaise, PaymentMode PaymentMode,
    long GstPaise = 0, bool ClaimCredit = false, string? SupplierGstin = null);

public sealed record ExpenseResult(long Id, string Number, bool AlreadyExisted);

public sealed record ExpenseListItem(
    long Id, string Number, DateOnly Date, string Category, string PayTo, string Description, long AmountPaise,
    PaymentMode PaymentMode, ExpenseStatus Status, long OutstandingPaise, long GstPaise = 0, long ItcPaise = 0);

public sealed record ExpenseCategoryTotal(string Category, int Count, long TotalPaise);

public sealed record ExpenseSummary(IReadOnlyList<ExpenseCategoryTotal> ByCategory, long TotalPaise, int Count);

public interface IExpenseService
{
    Task<IReadOnlyList<ExpenseCategoryDto>> ListCategoriesAsync(bool includeInactive = false);
    Task<ExpenseCategoryDto> AddCategoryAsync(string name);
    Task SetCategoryActiveAsync(long id, bool active);
    Task<ExpenseResult> RecordAsync(RecordExpenseCommand command);
    /// <summary>Pays (part of) an expense that was entered as not paid yet.</summary>
    Task PayAsync(long expenseId, long amountPaise, PaymentMode mode);
    /// <summary>Cancels a mistaken expense (an expense that has had payments cannot be cancelled).</summary>
    Task VoidAsync(long expenseId, string reason);
    Task<IReadOnlyList<ExpenseListItem>> ListAsync(DateOnly from, DateOnly to, long? categoryId = null, bool unpaidOnly = false);
    Task<ExpenseSummary> SummaryAsync(DateOnly from, DateOnly to);
}
