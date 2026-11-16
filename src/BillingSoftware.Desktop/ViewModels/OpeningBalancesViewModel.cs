using System.Collections.ObjectModel;
using System.Globalization;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed record OpeningKindItem(OpeningKind Kind, string Label, string Hint);

public sealed class OpeningRow(OpeningItem i)
{
    public OpeningItem Item { get; } = i;
    public long Id => i.Id;
    public string DateText => i.Date.ToString("dd-MMM-yy");
    public string KindText => i.KindText;
    public string Name => i.Name + (i.IsVoided ? "  (cancelled)" : "");
    public string AmountText => Money.FormatPlain(i.AmountPaise);
    public string Detail => i.CostPaise is { } c && c != i.AmountPaise ? $"cost {Money.FormatPlain(c)}" : i.Note ?? "";
    public bool IsVoided => i.IsVoided;
}

public sealed partial class OpeningBalancesViewModel : ObservableObject
{
    private readonly IOpeningBalanceService _opening;
    private readonly IPartyService _parties;
    private readonly ISupplierService _suppliers;
    private readonly IUiHost _ui;
    private Guid _commandId = Guid.NewGuid();

    public OpeningBalancesViewModel(IOpeningBalanceService opening, IPartyService parties, ISupplierService suppliers, IUiHost ui)
    {
        _opening = opening; _parties = parties; _suppliers = suppliers; _ui = ui;
        Kinds = new[]
        {
            new OpeningKindItem(OpeningKind.Bank, "Bank / UPI balance", "The money in your bank account or UPI wallet on the date below."),
            new OpeningKindItem(OpeningKind.Cash, "Cash in hand", "The cash in the drawer when you start. Enter this before your first day-end close; the day end then starts from it."),
            new OpeningKindItem(OpeningKind.CustomerDue, "Customer owes you", "What a customer already owes you from before. They can pay it down from the Customers screen."),
            new OpeningKindItem(OpeningKind.SupplierDue, "You owe a supplier", "What you already owe a supplier from before. Pay it from the Suppliers screen."),
            new OpeningKindItem(OpeningKind.Loan, "Loan taken", "A loan you still owe. Repay it from the Day end screen (\"Loan repaid in cash\")."),
            new OpeningKindItem(OpeningKind.FixedAsset, "Furniture / equipment", "Shelves, counters, fridge, computer. Enter its value today; depreciation is recorded as an expense."),
        };
        _kind = Kinds[0];
        _date = DateTime.Today;
    }

    public IReadOnlyList<OpeningKindItem> Kinds { get; }
    public ObservableCollection<PartyDto> Customers { get; } = new();
    public ObservableCollection<SupplierDto> SupplierList { get; } = new();
    public ObservableCollection<OpeningRow> Items { get; } = new();
    public ObservableCollection<KeyValueRow> Summary { get; } = new();

    [ObservableProperty] private OpeningKindItem _kind;
    [ObservableProperty] private DateTime _date;
    [ObservableProperty] private PartyDto? _customer;
    [ObservableProperty] private SupplierDto? _supplier;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _amountText = "";
    [ObservableProperty] private string _costText = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private OpeningRow? _selected;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _dayClosed;
    [ObservableProperty] private string _headline = "";

    public bool ShowCustomer => Kind.Kind == OpeningKind.CustomerDue;
    public bool ShowSupplier => Kind.Kind == OpeningKind.SupplierDue;
    public bool ShowName => Kind.Kind is OpeningKind.Bank or OpeningKind.Loan or OpeningKind.FixedAsset;
    public bool ShowCost => Kind.Kind == OpeningKind.FixedAsset;
    public string NameLabel => Kind.Kind switch { OpeningKind.Bank => "Bank or account name", OpeningKind.Loan => "Loan from", _ => "What it is" };
    public string AmountLabel => Kind.Kind == OpeningKind.FixedAsset ? "Value today (₹) *" : "Amount (₹) *";
    public string DateLabel => "As at (date of this balance)";
    public bool CashBlocked => Kind.Kind == OpeningKind.Cash && DayClosed;
    public string CashBlockedText => "The drawer has already been counted at a day-end close, so cash cannot be added here now. Use \"Money put in\" on the Day end screen.";
    public bool CanCancelEntry => Selected is { IsVoided: false };
    public bool HasSelection => Selected != null;
    public string SelectionText => Selected == null ? "Select an entry in the list to cancel it."
        : $"{Selected.KindText}: {Selected.Name}  {Selected.AmountText}";
    public string Guidance =>
        "Enter what the business already had on the day you started using this software. Each entry is recorded once; "
        + "the other side goes to owner's funds. Stock is entered on the Items screen (\"Add stock\") and is shown here for information.";

    partial void OnKindChanged(OpeningKindItem value)
    {
        foreach (var n in new[] { nameof(ShowCustomer), nameof(ShowSupplier), nameof(ShowName), nameof(ShowCost), nameof(NameLabel), nameof(AmountLabel), nameof(CashBlocked) })
            OnPropertyChanged(n);
        SaveCommand.NotifyCanExecuteChanged();
    }
    partial void OnDayClosedChanged(bool value) { OnPropertyChanged(nameof(CashBlocked)); SaveCommand.NotifyCanExecuteChanged(); }
    partial void OnSelectedChanged(OpeningRow? value)
    {
        OnPropertyChanged(nameof(CanCancelEntry)); OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(SelectionText));
        CancelEntryCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsBusyChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

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
        try
        {
            var keepCustomer = Customer?.Id; var keepSupplier = Supplier?.Id;
            var s = await _opening.GetSummaryAsync();
            Customers.Clear(); foreach (var c in await _parties.SearchAsync("", 500)) Customers.Add(c);
            SupplierList.Clear(); foreach (var c in await _suppliers.SearchAsync("", 500)) SupplierList.Add(c);
            Customer = Customers.FirstOrDefault(c => c.Id == keepCustomer); Supplier = SupplierList.FirstOrDefault(c => c.Id == keepSupplier);

            Items.Clear(); foreach (var i in s.Items) Items.Add(new OpeningRow(i));
            Summary.Clear();
            void Row(string label, long v) { if (v != 0) Summary.Add(new KeyValueRow(label, Money.FormatPlain(v))); }
            Row("Cash in hand", s.CashPaise); Row("Bank / UPI balance", s.BankPaise); Row("Customers owe you", s.CustomerDuesPaise);
            Row("Furniture and equipment", s.FixedAssetsPaise); Row("Stock (entered on Items)", s.OpeningStockPaise);
            Summary.Add(new KeyValueRow("Total assets", Money.FormatPlain(s.TotalAssetsPaise)));
            Row("You owe suppliers", s.SupplierDuesPaise); Row("Loans", s.LoansPaise);
            Summary.Add(new KeyValueRow("Total liabilities", Money.FormatPlain(s.TotalLiabilitiesPaise)));
            Summary.Add(new KeyValueRow("Owner's funds at the start", Money.FormatPlain(s.OwnerFundsPaise)));
            Headline = s.Items.All(i => i.IsVoided)
                ? "Nothing entered yet."
                : $"You started with {Money.Format(s.TotalAssetsPaise)} of assets and {Money.Format(s.TotalLiabilitiesPaise)} owed, so owner's funds were {Money.Format(s.OwnerFundsPaise)}.";
            DayClosed = s.DayClosed;
        }
        catch (PermissionDeniedException) { Headline = ""; Set("Only the Admin can enter opening balances.", true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private bool CanSave() => !IsBusy && !CashBlocked;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!TryMoney(AmountText, out var amount)) { Set("Enter the amount as a number above zero.", true); return; }
        long? cost = null;
        if (ShowCost && !string.IsNullOrWhiteSpace(CostText))
        {
            if (!TryMoney(CostText, out var c)) { Set("Enter the original cost as a number above zero, or leave it empty.", true); return; }
            cost = c;
        }
        if (ShowCustomer && Customer == null) { Set("Choose the customer.", true); return; }
        if (ShowSupplier && Supplier == null) { Set("Choose the supplier.", true); return; }
        IsBusy = true;
        try
        {
            await _opening.AddAsync(new AddOpeningCommand(_commandId, Kind.Kind, DateOnly.FromDateTime(Date), ShowCustomer ? Customer?.Id : null,
                ShowSupplier ? Supplier?.Id : null, Name, amount, cost, string.IsNullOrWhiteSpace(Note) ? null : Note));
            _commandId = Guid.NewGuid();
            Name = AmountText = CostText = Note = "";
            Set("Saved.", false);
            await LoadAsync();
        }
        catch (BusinessRuleException ex) { Set(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); Set("It was not saved. You can try again.", true); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanCancelEntry))]
    private async Task CancelEntryAsync()
    {
        if (Selected == null) return;
        var reason = _ui.Prompt($"Why is this entry being cancelled?\n{Selected.KindText}: {Selected.Name} {Selected.AmountText}\nThe entry will be reversed.", "Cancel opening balance");
        if (string.IsNullOrWhiteSpace(reason)) return;
        try
        {
            await _opening.VoidAsync(Selected.Id, reason);
            Set("The entry was cancelled.", false);
            await LoadAsync();
        }
        catch (BusinessRuleException ex) { Set(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private void Set(string m, bool e) { Status = m; StatusIsError = e; }
}
