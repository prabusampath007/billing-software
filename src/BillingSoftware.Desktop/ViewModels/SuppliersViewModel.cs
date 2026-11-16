using System.Collections.ObjectModel;
using System.Globalization;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed class SupplierRow(SupplierDto s)
{
    public SupplierDto Supplier { get; } = s;
    public string Name => Supplier.Name;
    public string Phone => Supplier.Phone ?? "";
    public string OwedText => Supplier.OutstandingPaise == 0 ? "-" : Money.FormatPlain(Supplier.OutstandingPaise);
}

public sealed partial class SuppliersViewModel : ObservableObject
{
    private readonly ISupplierService _suppliers;
    private readonly IUiHost _ui;
    private long _editingId;

    public SuppliersViewModel(ISupplierService suppliers, IUiHost ui) { _suppliers = suppliers; _ui = ui; }

    public ObservableCollection<SupplierRow> Items { get; } = new();
    public IReadOnlyList<StateItem> States => StateCodes.All;
    public IReadOnlyList<PaymentModeItem> Modes { get; } = new[]
    {
        new PaymentModeItem(PaymentMode.Cash, "Cash"), new PaymentModeItem(PaymentMode.Upi, "UPI"), new PaymentModeItem(PaymentMode.Card, "Card"),
    };

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private SupplierRow? _selected;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _gstin = "";
    [ObservableProperty] private StateItem? _state;
    [ObservableProperty] private string _owedText = "";
    [ObservableProperty] private string _payAmount = "";
    [ObservableProperty] private PaymentModeItem? _payMode;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;

    public string FormTitle => _editingId == 0 ? "New supplier" : "Supplier";
    public bool IsExisting => _editingId != 0;

    public async Task LoadAsync()
    {
        PayMode ??= Modes[0];
        try
        {
            Items.Clear();
            foreach (var s in await _suppliers.SearchAsync(Query, 100)) Items.Add(new SupplierRow(s));
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand] private Task SearchAsync() => LoadAsync();

    partial void OnSelectedChanged(SupplierRow? value)
    {
        if (value == null) return;
        var s = value.Supplier;
        _editingId = s.Id; Name = s.Name; Phone = s.Phone ?? ""; Gstin = s.Gstin ?? "";
        State = StateCodes.All.FirstOrDefault(x => x.Code == s.StateCode);
        OwedText = s.OutstandingPaise > 0 ? $"You owe {Money.Format(s.OutstandingPaise)}" : "Nothing owed";
        PayAmount = "";
        OnPropertyChanged(nameof(FormTitle)); OnPropertyChanged(nameof(IsExisting));
        Set("", false);
    }

    [RelayCommand]
    private void New()
    {
        _editingId = 0; Selected = null; Name = Phone = Gstin = OwedText = PayAmount = ""; State = null;
        OnPropertyChanged(nameof(FormTitle)); OnPropertyChanged(nameof(IsExisting));
        Set("", false);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var saved = await _suppliers.SaveAsync(new SupplierDto(_editingId, Name, Phone, string.IsNullOrWhiteSpace(Gstin) ? null : Gstin, State?.Code, 0));
            await LoadAsync();
            Selected = Items.FirstOrDefault(i => i.Supplier.Id == saved.Id);
            Set($"Saved {saved.Name}.", false);
        }
        catch (BusinessRuleException ex) { Set(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand]
    private async Task PayAsync()
    {
        if (_editingId == 0) return;
        if (!decimal.TryParse(PayAmount.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var amount) || amount <= 0)
        { Set("Enter the amount paid as a number.", true); return; }
        try
        {
            await _suppliers.PayAsync(_editingId, Money.ToPaise(amount), PayMode?.Mode ?? PaymentMode.Cash);
            var id = _editingId;
            await LoadAsync();
            Selected = Items.FirstOrDefault(i => i.Supplier.Id == id);
            Set("Payment recorded.", false);
        }
        catch (BusinessRuleException ex) { Set(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private void Set(string m, bool e) { Status = m; StatusIsError = e; }
}
