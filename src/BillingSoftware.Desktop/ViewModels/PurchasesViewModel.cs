using System.Collections.ObjectModel;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed class PurchaseRow(PurchaseListItem p)
{
    public PurchaseListItem Item { get; } = p;
    public string Number => p.PurchaseNumber;
    public string DateText => p.Date.ToString("dd-MMM-yy");
    public string Supplier => p.SupplierName;
    public string InvoiceNo => p.SupplierInvoiceNo;
    public string TotalText => Money.FormatPlain(p.TotalPaise);
}

public sealed class ReturnRow(ReturnListItem r)
{
    public ReturnListItem Item { get; } = r;
    public string Number => r.ReturnNumber;
    public string DateText => r.Date.ToString("dd-MMM-yy");
    public string Against => r.OriginalNumber;
    public string TotalText => Money.FormatPlain(r.TotalPaise);
}

public sealed partial class PurchasesViewModel : ObservableObject
{
    private readonly IPurchaseService _purchases;
    private readonly ISettingsService _settings;
    private readonly IUiHost _ui;
    private bool _loading;

    public PurchasesViewModel(IPurchaseService purchases, ISettingsService settings, IUiHost ui) { _purchases = purchases; _settings = settings; _ui = ui; }

    public ObservableCollection<PurchaseRow> Items { get; } = new();
    public ObservableCollection<ReturnRow> Returns { get; } = new();

    [ObservableProperty] private DateTime _from = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private DateTime _to = DateTime.Today;
    [ObservableProperty] private PurchaseRow? _selected;
    [ObservableProperty] private ReturnRow? _selectedReturn;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;

    partial void OnFromChanged(DateTime value) { if (!_loading) _ = LoadAsync(); }
    partial void OnToChanged(DateTime value) { if (!_loading) _ = LoadAsync(); }
    partial void OnSelectedChanged(PurchaseRow? value) { ViewCommand.NotifyCanExecuteChanged(); ReturnToSupplierCommand.NotifyCanExecuteChanged(); }
    partial void OnSelectedReturnChanged(ReturnRow? value) => ViewReturnCommand.NotifyCanExecuteChanged();

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var a = DateOnly.FromDateTime(From); var b = DateOnly.FromDateTime(To);
            var rows = await _purchases.ListAsync(a, b);
            Items.Clear();
            foreach (var r in rows) Items.Add(new PurchaseRow(r));
            Returns.Clear();
            foreach (var r in await _purchases.ListReturnsAsync(a, b)) Returns.Add(new ReturnRow(r));
            Summary = rows.Count == 0 ? "No purchases in this period."
                : $"{rows.Count} purchase{(rows.Count == 1 ? "" : "s")}, total {Money.Format(rows.Sum(r => r.TotalPaise))}";
        }
        catch (Exception ex) { _ui.ShowError(ex); }
        finally { _loading = false; }
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        if (_ui.ShowPurchaseEntry()) { Set("Purchase saved.", false); await LoadAsync(); }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ViewAsync()
    {
        if (Selected == null) return;
        try
        {
            var detail = await _purchases.GetAsync(Selected.Item.Id);
            var company = await _settings.GetCompanyAsync();
            if (detail != null) _ui.ShowDocument($"Purchase {detail.PurchaseNumber}", () => OtherDocuments.BuildPurchase(detail, company));
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ReturnToSupplierAsync()
    {
        if (Selected == null) return;
        var id = Selected.Item.Id;
        var number = Selected.Number;
        try
        {
            var lines = await _purchases.GetReturnableAsync(id);
            if (lines.All(l => l.RemainingMilli <= 0)) { _ui.Info("Everything on this purchase has already been sent back."); return; }
            var request = new ReturnDialogRequest(
                "Send goods back to the supplier", $"Purchase {number} from {Selected.Supplier}", lines, AllowRestock: false, CreditModeLabel: "Reduce what I owe the supplier",
                (inputs, mode, reason) => _purchases.PreviewReturnAsync(new PurchaseReturnCommand(Guid.NewGuid(), id, inputs, mode, reason)),
                (inputs, mode, reason, cmdId, _) => _purchases.ReturnAsync(new PurchaseReturnCommand(cmdId, id, inputs, mode, reason)));
            var result = await _ui.ShowReturnDialogAsync(request);
            if (result == null) return;
            var detail = await _purchases.GetReturnAsync(result.ReturnId);
            var company = await _settings.GetCompanyAsync();
            Set($"Debit note {result.ReturnNumber} created. Stock has been reduced.", false);
            await LoadAsync();
            if (detail != null) _ui.ShowDocument($"Debit note {detail.ReturnNumber}", () => OtherDocuments.BuildReturn(detail, company));
        }
        catch (BusinessRuleException ex) { Set(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand(CanExecute = nameof(HasReturnSelection))]
    private async Task ViewReturnAsync()
    {
        if (SelectedReturn == null) return;
        try
        {
            var detail = await _purchases.GetReturnAsync(SelectedReturn.Item.Id);
            var company = await _settings.GetCompanyAsync();
            if (detail != null) _ui.ShowDocument($"Debit note {detail.ReturnNumber}", () => OtherDocuments.BuildReturn(detail, company));
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private bool HasSelection() => Selected != null;
    private bool HasReturnSelection() => SelectedReturn != null;
    private void Set(string m, bool e) { Status = m; StatusIsError = e; }
}
