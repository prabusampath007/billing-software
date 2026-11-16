using System.Collections.ObjectModel;
using System.Globalization;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed record PayOption(PaymentMode Mode, string Label);

public sealed class ExpenseRow(ExpenseListItem e)
{
    public ExpenseListItem Item { get; } = e;
    public string Number => e.Number;
    public string DateText => e.Date.ToString("dd-MMM-yy");
    public string Category => e.Category;
    public string Text => string.IsNullOrWhiteSpace(e.PayTo) ? e.Description : string.IsNullOrWhiteSpace(e.Description) ? e.PayTo : $"{e.PayTo}: {e.Description}";
    public string AmountText => Money.FormatPlain(e.AmountPaise);
    public string PaidByText => e.Status == ExpenseStatus.Voided ? "Cancelled"
        : e.PaymentMode switch { PaymentMode.Cash => "Cash", PaymentMode.Upi => "UPI", PaymentMode.Card => "Card", PaymentMode.NonCash => "Depreciation", _ => e.OutstandingPaise > 0 ? "Not paid" : "Paid later" };
    public string OutstandingText => e.OutstandingPaise > 0 ? Money.FormatPlain(e.OutstandingPaise) : "";
    public string CreditText => e.ItcPaise > 0 ? Money.FormatPlain(e.ItcPaise) : (e.GstPaise > 0 ? "not claimed" : "");
    public bool IsVoided => e.Status == ExpenseStatus.Voided;
}

public sealed partial class ExpensesViewModel : ObservableObject
{
    private readonly IExpenseService _expenses;
    private readonly IUiHost _ui;
    private Guid _commandId = Guid.NewGuid();
    private bool _loading;

    public ExpensesViewModel(IExpenseService expenses, IUiHost ui)
    {
        _expenses = expenses; _ui = ui;
        PayOptions = new[]
        {
            new PayOption(PaymentMode.Cash, "Cash (from the drawer)"), new PayOption(PaymentMode.Upi, "UPI"),
            new PayOption(PaymentMode.Card, "Card"), new PayOption(PaymentMode.Credit, "Not paid yet"),
            new PayOption(PaymentMode.NonCash, "No money involved (depreciation)"),
        };
        SettleOptions = PayOptions.Take(3).ToList();
        _payOption = PayOptions[0];
        _settleOption = SettleOptions[0];
        var today = DateTime.Today;
        _from = new DateTime(today.Year, today.Month, 1);
        _to = today;
        _date = today;
        _filterCategory = AllCategories;
    }

    public static readonly ExpenseCategoryDto AllCategories = new(0, "All categories", "", true, true);

    public IReadOnlyList<PayOption> PayOptions { get; }
    public IReadOnlyList<PayOption> SettleOptions { get; }
    public ObservableCollection<ExpenseCategoryDto> Categories { get; } = new();
    public ObservableCollection<ExpenseCategoryDto> FilterCategories { get; } = new();
    public ObservableCollection<ExpenseRow> Items { get; } = new();
    public ObservableCollection<ExpenseCategoryTotal> ByCategory { get; } = new();

    // form
    [ObservableProperty] private DateTime _date;
    [ObservableProperty] private ExpenseCategoryDto? _category;
    [ObservableProperty] private string _payTo = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _reference = "";
    [ObservableProperty] private string _amountText = "";
    [ObservableProperty] private string _gstText = "";
    [ObservableProperty] private string _supplierGstin = "";
    [ObservableProperty] private bool _claimCredit;
    [ObservableProperty] private PayOption _payOption;
    [ObservableProperty] private string _newCategoryName = "";
    // list
    [ObservableProperty] private DateTime _from;
    [ObservableProperty] private DateTime _to;
    [ObservableProperty] private ExpenseCategoryDto _filterCategory;
    [ObservableProperty] private bool _unpaidOnly;
    [ObservableProperty] private ExpenseRow? _selected;
    [ObservableProperty] private string _totalText = "";
    // selected expense
    [ObservableProperty] private string _settleAmountText = "";
    [ObservableProperty] private PayOption _settleOption;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _isBusy;

    /// <summary>Depreciation has no GST, so the GST fields are hidden for it.</summary>
    public bool ShowGst => PayOption.Mode != PaymentMode.NonCash;
    public string GstSummaryText
    {
        get
        {
            if (!TryMoney(AmountText, out var amount) || !TryMoney(GstText, out var gst) || gst >= amount) return "";
            return ClaimCredit
                ? $"Expense {Money.Format(amount - gst)} plus GST {Money.Format(gst)}, which is claimed as input credit."
                : $"The GST of {Money.Format(gst)} is not claimed, so the whole {Money.Format(amount)} is the expense.";
        }
    }

    partial void OnAmountTextChanged(string value) => OnPropertyChanged(nameof(GstSummaryText));
    partial void OnGstTextChanged(string value) => OnPropertyChanged(nameof(GstSummaryText));
    partial void OnClaimCreditChanged(bool value) => OnPropertyChanged(nameof(GstSummaryText));

    public bool HasSelection => Selected != null;
    public bool CanSettle => Selected is { Item.OutstandingPaise: > 0 };
    public string SelectionText => Selected == null ? "Select an expense in the list to pay it or cancel it."
        : $"{Selected.Number}  {Selected.Category}  {Selected.AmountText}" + (Selected.Item.OutstandingPaise > 0 ? $"   (still to pay {Selected.OutstandingText})" : "");

    partial void OnSelectedChanged(ExpenseRow? value)
    {
        OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(CanSettle)); OnPropertyChanged(nameof(SelectionText));
        PayCommand.NotifyCanExecuteChanged(); CancelExpenseCommand.NotifyCanExecuteChanged();
    }
    // Depreciation is the only expense where no money moves, and it is the only category that can use that choice.
    private const string DepreciationCode = "6180";
    partial void OnCategoryChanged(ExpenseCategoryDto? value)
    {
        if (value == null) return;
        var noMoney = PayOptions.First(o => o.Mode == PaymentMode.NonCash);
        if (value.AccountCode == DepreciationCode) PayOption = noMoney;
        else if (PayOption.Mode == PaymentMode.NonCash) PayOption = PayOptions[0];
    }
    partial void OnPayOptionChanged(PayOption value)
    {
        OnPropertyChanged(nameof(ShowGst));
        if (value.Mode != PaymentMode.NonCash || Category?.AccountCode == DepreciationCode) return;
        var dep = Categories.FirstOrDefault(c => c.AccountCode == DepreciationCode);
        if (dep != null) Category = dep;
    }
    partial void OnFromChanged(DateTime value) { if (!_loading) _ = LoadAsync(); }
    partial void OnToChanged(DateTime value) { if (!_loading) _ = LoadAsync(); }
    partial void OnFilterCategoryChanged(ExpenseCategoryDto value) { if (!_loading) _ = LoadAsync(); }
    partial void OnUnpaidOnlyChanged(bool value) { if (!_loading) _ = LoadAsync(); }

    private static bool TryMoney(string? text, out long paise)
    {
        paise = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var d) || d <= 0 || d > 1_000_000_000m) return false;
        paise = Money.ToPaise(d);
        return true;
    }

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var cats = await _expenses.ListCategoriesAsync();
            var keep = Category?.Id;
            Categories.Clear(); FilterCategories.Clear(); FilterCategories.Add(AllCategories);
            foreach (var c in cats) { Categories.Add(c); FilterCategories.Add(c); }
            Category = Categories.FirstOrDefault(c => c.Id == keep) ?? Categories.FirstOrDefault();
            if (!FilterCategories.Contains(FilterCategory)) FilterCategory = FilterCategories.FirstOrDefault(c => c.Id == FilterCategory.Id) ?? AllCategories;

            var a = DateOnly.FromDateTime(From); var b = DateOnly.FromDateTime(To);
            var rows = await _expenses.ListAsync(a, b, FilterCategory.Id == 0 ? null : FilterCategory.Id, UnpaidOnly);
            Items.Clear();
            foreach (var r in rows) Items.Add(new ExpenseRow(r));
            var summary = await _expenses.SummaryAsync(a, b);
            ByCategory.Clear();
            foreach (var c in summary.ByCategory) ByCategory.Add(c);
            TotalText = summary.Count == 0 ? "No expenses in this period." : $"{summary.Count} expense{(summary.Count == 1 ? "" : "s")}, total {Money.Format(summary.TotalPaise)}";
        }
        catch (Exception ex) { _ui.ShowError(ex); }
        finally { _loading = false; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Category == null) { Set("Choose a category.", true); return; }
        if (!TryMoney(AmountText, out var amount)) { Set("Enter the amount as a number above zero.", true); return; }
        long gst = 0;
        if (ShowGst && !string.IsNullOrWhiteSpace(GstText))
        {
            if (!TryMoney(GstText, out gst)) { Set("Enter the GST as a number above zero, or leave it empty.", true); return; }
        }
        IsBusy = true;
        try
        {
            var result = await _expenses.RecordAsync(new RecordExpenseCommand(_commandId, DateOnly.FromDateTime(Date), Category.Id, PayTo, Description,
                string.IsNullOrWhiteSpace(Reference) ? null : Reference, amount, PayOption.Mode,
                gst, ShowGst && gst > 0 && ClaimCredit, string.IsNullOrWhiteSpace(SupplierGstin) ? null : SupplierGstin));
            _commandId = Guid.NewGuid();
            PayTo = Description = Reference = AmountText = GstText = SupplierGstin = ""; ClaimCredit = false;
            Set($"Saved {result.Number}.", false);
            await LoadAsync();
        }
        catch (BusinessRuleException ex) { Set(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); Set("It was not saved. You can try again.", true); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AddCategoryAsync()
    {
        try
        {
            var c = await _expenses.AddCategoryAsync(NewCategoryName);
            NewCategoryName = "";
            await LoadAsync();
            Category = Categories.FirstOrDefault(x => x.Id == c.Id);
            Set($"Added the category {c.Name}.", false);
        }
        catch (BusinessRuleException ex) { Set(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private bool CanPay() => CanSettle;

    [RelayCommand(CanExecute = nameof(CanPay))]
    private async Task PayAsync()
    {
        if (Selected == null) return;
        if (!TryMoney(SettleAmountText, out var amount)) { Set("Enter the amount paid as a number above zero.", true); return; }
        try
        {
            await _expenses.PayAsync(Selected.Item.Id, amount, SettleOption.Mode);
            SettleAmountText = "";
            Set("Payment recorded.", false);
            await LoadAsync();
        }
        catch (BusinessRuleException ex) { Set(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private bool CanCancel() => Selected is { IsVoided: false };

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelExpenseAsync()
    {
        if (Selected == null) return;
        var reason = _ui.Prompt($"Why is expense {Selected.Number} being cancelled?\nThe entry will be reversed.", "Cancel expense");
        if (string.IsNullOrWhiteSpace(reason)) return;
        try
        {
            await _expenses.VoidAsync(Selected.Item.Id, reason);
            Set("The expense was cancelled.", false);
            await LoadAsync();
        }
        catch (BusinessRuleException ex) { Set(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private void Set(string m, bool e) { Status = m; StatusIsError = e; }
}
