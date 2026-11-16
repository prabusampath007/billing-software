using System.Collections.ObjectModel;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed class BillRow(SaleListItem s)
{
    public SaleListItem Item { get; } = s;
    public string InvoiceNumber => s.InvoiceNumber;
    public string TimeText => s.SaleDate.ToString("HH:mm");
    public string Customer => string.IsNullOrWhiteSpace(s.CustomerName) ? "Walk-in" : s.CustomerName!;
    public string Cashier => s.CashierName;
    public string TotalText => Money.FormatPlain(s.TotalPaise);
    public string StatusText => s.Status == SaleStatus.Voided ? "Voided" : "Paid";
    public bool IsVoided => s.Status == SaleStatus.Voided;
}

public sealed partial class BillsViewModel : ObservableObject
{
    private readonly ISalesService _sales;
    private readonly ISalesReturnService _returns;
    private readonly ISettingsService _settings;
    private readonly IUiHost _ui;

    public BillsViewModel(ISalesService sales, ISalesReturnService returns, ISettingsService settings, IUiHost ui)
    { _sales = sales; _returns = returns; _settings = settings; _ui = ui; }

    public ObservableCollection<BillRow> Items { get; } = new();
    public ObservableCollection<ReturnRow> ReturnRows { get; } = new();
    [ObservableProperty] private ReturnRow? _selectedReturn;

    [ObservableProperty] private DateTime _date = DateTime.Today;
    [ObservableProperty] private BillRow? _selected;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;

    partial void OnDateChanged(DateTime value) => _ = LoadAsync();
    partial void OnSelectedChanged(BillRow? value)
    {
        ViewCommand.NotifyCanExecuteChanged();
        VoidCommand.NotifyCanExecuteChanged();
        ReturnItemsCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedReturnChanged(ReturnRow? value) => ViewReturnCommand.NotifyCanExecuteChanged();

    public async Task LoadAsync()
    {
        try
        {
            var rows = await _sales.ListAsync(DateOnly.FromDateTime(Date));
            ReturnRows.Clear();
            foreach (var r in await _returns.ListAsync(DateOnly.FromDateTime(Date))) ReturnRows.Add(new ReturnRow(r));
            Items.Clear();
            foreach (var r in rows) Items.Add(new BillRow(r));
            var live = rows.Where(r => r.Status == SaleStatus.Posted).ToList();
            Summary = rows.Count == 0 ? "No bills on this day."
                : $"{live.Count} bill{(live.Count == 1 ? "" : "s")}, total {Money.Format(live.Sum(r => r.TotalPaise))}"
                  + (rows.Count != live.Count ? $"  ({rows.Count - live.Count} voided)" : "");
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand] private void Today() => Date = DateTime.Today;
    [RelayCommand] private void Previous() => Date = Date.AddDays(-1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ViewAsync()
    {
        if (Selected == null) return;
        try
        {
            var detail = await _sales.GetForReprintAsync(Selected.Item.Id);
            var company = await _settings.GetCompanyAsync();
            if (detail != null) _ui.ShowReceipt(detail, company, duplicate: true);
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    /// <summary>The full tax invoice on A4 pages (the copy is marked DUPLICATE and counted as a reprint, like the receipt).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PrintA4Async()
    {
        if (Selected == null) return;
        try
        {
            var detail = await _sales.GetForReprintAsync(Selected.Item.Id);
            var company = await _settings.GetCompanyAsync();
            if (detail != null) _ui.ShowDocument($"Invoice {detail.InvoiceNumber}", () => InvoiceBuilder.BuildA4(detail, company, duplicate: true));
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand(CanExecute = nameof(CanVoid))]
    private async Task VoidAsync()
    {
        if (Selected == null) return;
        var reason = _ui.Prompt($"Why is bill {Selected.InvoiceNumber} being voided?\nStock will be returned and the sale reversed.", "Void bill");
        if (string.IsNullOrWhiteSpace(reason)) return;
        try
        {
            long? approver = null;
            while (true)
            {
                try { await _sales.VoidSaleAsync(Selected.Item.Id, reason, approver); break; }
                catch (ApprovalRequiredException ex)
                {
                    approver = await _ui.RequestApprovalAsync(ex.Message);
                    if (approver == null) { Set("Not approved. The bill was not voided.", true); return; }
                }
            }
            Set($"Bill {Selected.InvoiceNumber} voided.", false);
            await LoadAsync();
        }
        catch (BusinessRuleException ex) { Set(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand(CanExecute = nameof(CanVoid))]
    private async Task ReturnItemsAsync()
    {
        if (Selected == null) return;
        var id = Selected.Item.Id;
        var number = Selected.InvoiceNumber;
        try
        {
            var lines = await _returns.GetReturnableAsync(id);
            if (lines.All(l => l.RemainingMilli <= 0)) { _ui.Info("Everything on this bill has already been returned."); return; }
            var request = new ReturnDialogRequest(
                "Return items from a bill", $"Bill {number} - {Selected.Customer}", lines, AllowRestock: true, CreditModeLabel: "Reduce what the customer owes",
                (inputs, mode, reason) => _returns.PreviewAsync(new SaleReturnCommand(Guid.NewGuid(), id, inputs, mode, reason)),
                (inputs, mode, reason, cmdId, approver) => _returns.ReturnAsync(new SaleReturnCommand(cmdId, id, inputs, mode, reason, approver)));
            var result = await _ui.ShowReturnDialogAsync(request);
            if (result == null) return;
            var detail = await _returns.GetAsync(result.ReturnId);
            var company = await _settings.GetCompanyAsync();
            Set($"Credit note {result.ReturnNumber} created. For an exchange, make a new bill for the new items.", false);
            await LoadAsync();
            if (detail != null) _ui.ShowDocument($"Credit note {detail.ReturnNumber}", () => OtherDocuments.BuildReturn(detail, company));
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
            var detail = await _returns.GetAsync(SelectedReturn.Item.Id);
            var company = await _settings.GetCompanyAsync();
            if (detail != null) _ui.ShowDocument($"Credit note {detail.ReturnNumber}", () => OtherDocuments.BuildReturn(detail, company));
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private bool HasReturnSelection() => SelectedReturn != null;
    private bool HasSelection() => Selected != null;
    private bool CanVoid() => Selected is { IsVoided: false };
    private void Set(string m, bool e) { Status = m; StatusIsError = e; }
}
