using System.Collections.ObjectModel;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Export;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed record BsRowDisplay(string Label, string Current, string Compare, string Change, PnlRowKind Kind);

public sealed record CheckRow(string Name, string Books, string Records, string Result, string Message, bool Ok);

public sealed partial class BalanceSheetViewModel : ObservableObject
{
    private readonly IBalanceSheetService _bs;
    private readonly IUiHost _ui;
    private bool _syncing;

    public BalanceSheetViewModel(IBalanceSheetService bs, IUiHost ui)
    {
        _bs = bs; _ui = ui;
        _asAt = DateTime.Today;
        _compareAt = DateTime.Today.AddMonths(-1);
    }

    public ObservableCollection<BsRowDisplay> Rows { get; } = new();
    public ObservableCollection<CheckRow> Checks { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();

    [ObservableProperty] private DateTime _asAt;
    [ObservableProperty] private DateTime _compareAt;
    [ObservableProperty] private BalanceSheetReport? _report;
    [ObservableProperty] private string _status = "Choose a date and press Prepare.";
    [ObservableProperty] private bool _isBusy;

    public bool HasReport => Report != null;
    public bool Balanced => Report?.Current.DifferencePaise == 0;
    public bool HasProblems => Report != null && (!Balanced || Warnings.Count > 0 || Checks.Any(c => !c.Ok));
    public string CurrentHeader => $"As at {AsAt:dd-MMM-yy}";
    public string CompareHeader => $"As at {CompareAt:dd-MMM-yy}";
    public string Headline => Report == null ? "" : Balanced
        ? $"Assets {Money.Format(Report.Current.TotalAssetsPaise)} = liabilities {Money.Format(Report.Current.TotalLiabilitiesPaise)} + owner's funds {Money.Format(Report.Current.TotalOwnersFundsPaise)}"
        : $"The balance sheet does not balance (difference {Money.Format(Report.Current.DifferencePaise)}). Do not rely on it.";
    public string AttentionText => Report == null ? "" : !HasProblems ? "Everything checks out." : $"{Warnings.Count + Checks.Count(c => !c.Ok)} thing(s) to look at: see the Checks tab.";

    partial void OnAsAtChanged(DateTime value)
    {
        if (_syncing) return;
        _syncing = true; CompareAt = value.AddMonths(-1); _syncing = false;
    }

    partial void OnReportChanged(BalanceSheetReport? value)
    {
        OnPropertyChanged(nameof(HasReport)); OnPropertyChanged(nameof(Balanced)); OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(HasProblems)); OnPropertyChanged(nameof(AttentionText)); OnPropertyChanged(nameof(CurrentHeader)); OnPropertyChanged(nameof(CompareHeader));
        ExportCommand.NotifyCanExecuteChanged(); PrintCommand.NotifyCanExecuteChanged();
    }

    private static string M(long p) => Money.FormatPlain(p);

    [RelayCommand]
    public async Task PrepareAsync()
    {
        IsBusy = true;
        try
        {
            var r = await _bs.BuildAsync(DateOnly.FromDateTime(AsAt), DateOnly.FromDateTime(CompareAt));
            Rows.Clear();
            foreach (var x in BalanceSheetStatement.Rows(r))
                Rows.Add(new BsRowDisplay(x.Label, x.Kind == PnlRowKind.Heading ? "" : M(x.Current), x.Kind == PnlRowKind.Heading ? "" : M(x.Previous),
                    x.Kind == PnlRowKind.Heading ? "" : M(x.Current - x.Previous), x.Kind));
            Checks.Clear();
            foreach (var c in r.Checks) Checks.Add(new CheckRow(c.Name, M(c.BooksPaise), M(c.RecordsPaise), c.Ok ? "OK" : "CHECK", c.Message, c.Ok));
            Warnings.Clear();
            foreach (var w in r.Warnings) Warnings.Add(w);
            Notes.Clear();
            foreach (var n in r.Notes) Notes.Add(n);
            Report = r;
            Status = "Read from the books. For you and your accountant to review.";
        }
        catch (BusinessRuleException ex) { Status = ex.Message; }
        catch (Exception ex) { _ui.ShowError(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void Export()
    {
        if (Report == null) return;
        var path = _ui.PickSaveFile($"Balance-sheet-{Report.Current.AsAt:yyyyMMdd}.xlsx", "Excel workbook (*.xlsx)|*.xlsx");
        if (path == null) return;
        try
        {
            BalanceSheetExcelExporter.Write(Report, path);
            Status = $"Saved {Path.GetFileName(path)}.";
            _ui.Info($"The balance sheet was saved to:\n{path}");
        }
        catch (IOException ex) { Status = "The file could not be saved. If it is open in Excel, close it and try again."; Serilog.Log.Warning(ex, "Balance sheet export failed"); }
        catch (UnauthorizedAccessException) { Status = "You do not have permission to save in that folder. Choose another folder."; }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void Print()
    {
        if (Report == null) return;
        var r = Report;
        _ui.ShowDocument("Balance sheet", () => OtherDocuments.BuildBalanceSheet(r));
    }
}
