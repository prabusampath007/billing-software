using System.Collections.ObjectModel;
using System.Globalization;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed class CustomerRow(PartyDto p)
{
    public PartyDto Party { get; } = p;
    public string Name => p.Name;
    public string Phone => p.Phone ?? "";
    public string OwesText => p.OutstandingPaise == 0 ? "-" : Money.FormatPlain(p.OutstandingPaise);
}

public sealed class PaymentModeItem(PaymentMode mode, string label) { public PaymentMode Mode { get; } = mode; public string Label { get; } = label; }

public sealed partial class CustomersViewModel : ObservableObject
{
    private readonly IPartyService _parties;
    private readonly ICurrentUser _current;
    private readonly IUiHost _ui;
    private long _editingId;

    public CustomersViewModel(IPartyService parties, ICurrentUser current, IUiHost ui) { _parties = parties; _current = current; _ui = ui; }

    /// <summary>Only the Admin sets credit limits.</summary>
    public bool CanSetLimit => _current.User?.Role == UserRole.Admin;

    public ObservableCollection<CustomerRow> Items { get; } = new();
    public IReadOnlyList<StateItem> States => StateCodes.All;
    public IReadOnlyList<PaymentModeItem> Modes { get; } = new[]
    {
        new PaymentModeItem(PaymentMode.Cash, "Cash"), new PaymentModeItem(PaymentMode.Upi, "UPI"), new PaymentModeItem(PaymentMode.Card, "Card"),
    };

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private CustomerRow? _selected;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _gstin = "";
    [ObservableProperty] private StateItem? _state;
    [ObservableProperty] private string _owesText = "";
    [ObservableProperty] private string _creditLimitText = "";
    [ObservableProperty] private string _receiveAmount = "";
    [ObservableProperty] private PaymentModeItem? _receiveMode;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;

    public string FormTitle => _editingId == 0 ? "New customer" : "Customer";
    public bool IsExisting => _editingId != 0;

    public async Task LoadAsync()
    {
        ReceiveMode ??= Modes[0];
        try
        {
            Items.Clear();
            foreach (var p in await _parties.SearchAsync(Query, 100)) Items.Add(new CustomerRow(p));
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand] private Task SearchAsync() => LoadAsync();

    partial void OnSelectedChanged(CustomerRow? value)
    {
        if (value == null) return;
        var p = value.Party;
        _editingId = p.Id; Name = p.Name; Phone = p.Phone ?? ""; Gstin = p.Gstin ?? "";
        State = StateCodes.All.FirstOrDefault(s => s.Code == p.StateCode);
        CreditLimitText = p.CreditLimitPaise == 0 ? "" : Money.ToRupees(p.CreditLimitPaise).ToString("0.00", CultureInfo.CurrentCulture);
        OwesText = (p.OutstandingPaise > 0 ? $"Owes {Money.Format(p.OutstandingPaise)}" : "Nothing owed")
                   + (p.CreditLimitPaise > 0 ? $"   (credit limit {Money.Format(p.CreditLimitPaise)}{(p.IsOverLimit ? ", over the limit" : "")})" : "");
        ReceiveAmount = "";
        OnPropertyChanged(nameof(FormTitle)); OnPropertyChanged(nameof(IsExisting));
        Set("", false);
    }

    [RelayCommand]
    private void New()
    {
        _editingId = 0; Selected = null; Name = Phone = Gstin = OwesText = ReceiveAmount = CreditLimitText = ""; State = null;
        OnPropertyChanged(nameof(FormTitle)); OnPropertyChanged(nameof(IsExisting));
        Set("", false);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        long limit = 0;
        if (!string.IsNullOrWhiteSpace(CreditLimitText))
        {
            if (!decimal.TryParse(CreditLimitText.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var l) || l < 0 || l > 1_000_000_000m)
            { Set("Enter the credit limit as a number, or leave it empty for no limit.", true); return; }
            limit = Money.ToPaise(l);
        }
        try
        {
            var saved = await _parties.SaveAsync(new PartyDto(_editingId, Name, Phone, string.IsNullOrWhiteSpace(Gstin) ? null : Gstin, State?.Code, 0, limit));
            await LoadAsync();
            Selected = Items.FirstOrDefault(i => i.Party.Id == saved.Id);
            Set($"Saved {saved.Name}.", false);
        }
        catch (BusinessRuleException ex) { Set(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand]
    private async Task ReceiveAsync()
    {
        if (_editingId == 0) return;
        if (!decimal.TryParse(ReceiveAmount.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var amount) || amount <= 0)
        { Set("Enter the amount received as a number.", true); return; }
        try
        {
            await _parties.ReceivePaymentAsync(_editingId, Money.ToPaise(amount), ReceiveMode?.Mode ?? PaymentMode.Cash);
            var id = _editingId;
            await LoadAsync();
            Selected = Items.FirstOrDefault(i => i.Party.Id == id);
            Set("Payment received.", false);
        }
        catch (BusinessRuleException ex) { Set(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private void Set(string m, bool e) { Status = m; StatusIsError = e; }
}
