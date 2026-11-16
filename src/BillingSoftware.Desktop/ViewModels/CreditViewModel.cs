using System.Collections.ObjectModel;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Export;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed class AgeingRowItem(AgeingRow r)
{
    public AgeingRow Row { get; } = r;
    public string Name => r.Name;
    public string Phone => r.Phone ?? "";
    public string OwesText => Money.FormatPlain(r.OutstandingPaise);
    private static string Cell(long p) => p == 0 ? "" : Money.FormatPlain(p);
    public string CurrentText => Cell(r.CurrentPaise);
    public string Days31Text => Cell(r.Days31To60Paise);
    public string Days61Text => Cell(r.Days61To90Paise);
    public string Over90Text => Cell(r.Over90Paise);
    public string OldestText => r.OldestUnpaid.HasValue ? $"{r.OldestDays} days" : "";
    public string LimitText => r.CreditLimitPaise == 0 ? "" : Money.FormatPlain(r.CreditLimitPaise) + (r.OverLimit ? "  OVER" : "");
    public bool IsOver => r.OverLimit;
    public bool IsOld => r.Over90Paise > 0;
}

public sealed class LedgerRowItem(LedgerEntry e)
{
    public string DateText => e.Date.ToString("dd-MMM-yy");
    public string Kind => e.Kind;
    public string Reference => e.Reference;
    public string Description => e.Description;
    public string DebitText => e.DebitPaise == 0 ? "" : Money.FormatPlain(e.DebitPaise);
    public string CreditText => e.CreditPaise == 0 ? "" : Money.FormatPlain(e.CreditPaise);
    public string BalanceText => Money.FormatPlain(e.BalancePaise);
}

public sealed partial class CreditViewModel : ObservableObject
{
    private readonly ICreditService _credit;
    private readonly IUiHost _ui;
    private AgeingReport? _ageing;
    private CustomerStatement? _statement;
    private int _generation;

    public CreditViewModel(ICreditService credit, IUiHost ui)
    {
        _credit = credit; _ui = ui;
        var today = DateTime.Today;
        _to = today; _from = today.AddDays(-90);
    }

    public ObservableCollection<AgeingRowItem> Rows { get; } = new();
    public ObservableCollection<LedgerRowItem> Entries { get; } = new();

    [ObservableProperty] private AgeingRowItem? _selected;
    [ObservableProperty] private DateTime _from;
    [ObservableProperty] private DateTime _to;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _customerText = "Select a customer in the list to see their account.";
    [ObservableProperty] private string _broughtForwardText = "";
    [ObservableProperty] private string _closingText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _isBusy;

    public bool HasSelection => Selected != null;
    public bool HasAgeing => _ageing != null && _ageing.Rows.Count > 0;
    public bool HasStatement => _statement != null;

    partial void OnSelectedChanged(AgeingRowItem? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        PrintStatementCommand.NotifyCanExecuteChanged(); PrintReminderCommand.NotifyCanExecuteChanged(); ExportStatementCommand.NotifyCanExecuteChanged();
        _ = LoadStatementAsync();
    }
    partial void OnFromChanged(DateTime value) { if (Selected != null) _ = LoadStatementAsync(); }
    partial void OnToChanged(DateTime value) { if (Selected != null) _ = LoadStatementAsync(); }

    [RelayCommand]
    public async Task PrepareAsync()
    {
        IsBusy = true;
        try
        {
            var keep = Selected?.Row.PartyId;
            var r = await _credit.GetAgeingAsync(DateOnly.FromDateTime(DateTime.Today));
            _ageing = r;
            Rows.Clear();
            foreach (var x in r.Rows) Rows.Add(new AgeingRowItem(x));
            Selected = Rows.FirstOrDefault(x => x.Row.PartyId == keep);
            SummaryText = r.Rows.Count == 0
                ? "Nobody owes you anything."
                : $"{r.Rows.Count} customer{(r.Rows.Count == 1 ? "" : "s")} owe {Money.Format(r.TotalPaise)}:  0-30 days {Money.Format(r.CurrentPaise)},  31-60 days {Money.Format(r.Days31To60Paise)},  "
                  + $"61-90 days {Money.Format(r.Days61To90Paise)},  over 90 days {Money.Format(r.Over90Paise)}.";
            OnPropertyChanged(nameof(HasAgeing));
            ExportAgeingCommand.NotifyCanExecuteChanged();
            Set("", false);
        }
        catch (PermissionDeniedException) { Rows.Clear(); _ageing = null; SummaryText = ""; Set("Only the Admin can see customer accounts.", true); }
        catch (Exception ex) { _ui.ShowError(ex); }
        finally { IsBusy = false; }
    }

    private async Task LoadStatementAsync()
    {
        var generation = ++_generation;
        var item = Selected;
        if (item == null)
        {
            _statement = null; Entries.Clear(); CustomerText = "Select a customer in the list to see their account."; BroughtForwardText = ClosingText = "";
            OnPropertyChanged(nameof(HasStatement)); return;
        }
        try
        {
            var st = await _credit.GetStatementAsync(item.Row.PartyId, DateOnly.FromDateTime(From), DateOnly.FromDateTime(To));
            if (generation != _generation) return;
            _statement = st;
            Entries.Clear();
            foreach (var e in st.Entries) Entries.Add(new LedgerRowItem(e));
            CustomerText = $"{st.Name}   owes {Money.Format(item.Row.OutstandingPaise)}"
                           + (st.CreditLimitPaise > 0 ? $"   (credit limit {Money.Format(st.CreditLimitPaise)}{(item.Row.OverLimit ? ", over the limit" : "")})" : "   (no credit limit)");
            BroughtForwardText = $"Balance brought forward: {Money.Format(st.BroughtForwardPaise)}";
            ClosingText = $"Closing balance on {st.To:dd-MMM-yyyy}: {Money.Format(st.ClosingPaise)}";
            OnPropertyChanged(nameof(HasStatement));
            PrintStatementCommand.NotifyCanExecuteChanged(); ExportStatementCommand.NotifyCanExecuteChanged();
        }
        catch (BusinessRuleException ex) { if (generation == _generation) { _statement = null; Entries.Clear(); Set(ex.Message, true); OnPropertyChanged(nameof(HasStatement)); } }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private bool CanStatement() => _statement != null;
    private bool CanReminder() => Selected != null;
    private bool CanExportAgeing() => HasAgeing;

    [RelayCommand(CanExecute = nameof(CanStatement))]
    private void PrintStatement()
    {
        if (_statement == null) return;
        var st = _statement;
        _ui.ShowDocument($"Statement - {st.Name}", () => OtherDocuments.BuildStatement(st));
    }

    [RelayCommand(CanExecute = nameof(CanReminder))]
    private async Task PrintReminderAsync()
    {
        if (Selected == null) return;
        try
        {
            var rem = await _credit.GetReminderAsync(Selected.Row.PartyId);
            _ui.ShowDocument($"Reminder - {rem.CustomerName}", () => OtherDocuments.BuildReminder(rem));
            Set("", false);
        }
        catch (BusinessRuleException ex) { Set(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand(CanExecute = nameof(CanExportAgeing))]
    private void ExportAgeing()
    {
        if (_ageing == null) return;
        var path = _ui.PickSaveFile($"Who-owes-you-{DateTime.Today:yyyy-MM-dd}.xlsx", "Excel workbook (*.xlsx)|*.xlsx");
        if (path == null) return;
        try { CreditExcelExporter.WriteAgeing(_ageing, path); Set("Saved the list to Excel.", false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Set("The file could not be saved. Is it open in another program? Choose another name or place.", true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand(CanExecute = nameof(CanStatement))]
    private void ExportStatement()
    {
        if (_statement == null) return;
        var path = _ui.PickSaveFile($"Statement-{_statement.Name}-{DateTime.Today:yyyy-MM-dd}.xlsx", "Excel workbook (*.xlsx)|*.xlsx");
        if (path == null) return;
        try { CreditExcelExporter.WriteStatement(_statement, path); Set("Saved the statement to Excel.", false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Set("The file could not be saved. Is it open in another program? Choose another name or place.", true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private void Set(string m, bool e) { Status = m; StatusIsError = e; }
}
