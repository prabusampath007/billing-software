using System.Collections.ObjectModel;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Export;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed record PeriodOption(string Label, Func<DateTime, (DateTime From, DateTime To)> Range);

public sealed record StatementRow(string Label, string Current, string Previous, string Change, PnlRowKind Kind);

public sealed partial class ProfitLossViewModel : ObservableObject
{
    private readonly IProfitLossService _pnl;
    private readonly IUiHost _ui;
    private bool _settingDates;

    public ProfitLossViewModel(IProfitLossService pnl, IUiHost ui)
    {
        _pnl = pnl; _ui = ui;
        Periods = new[]
        {
            new PeriodOption("This month", t => (new DateTime(t.Year, t.Month, 1), t)),
            new PeriodOption("Last month", t => { var f = new DateTime(t.Year, t.Month, 1).AddMonths(-1); return (f, f.AddMonths(1).AddDays(-1)); }),
            new PeriodOption("This financial year", t => (FinancialYears.For(t).Start, t)),
            new PeriodOption("Last financial year", t => { var fy = FinancialYears.For(FinancialYears.For(t).Start.AddDays(-1)); return (fy.Start, fy.End); }),
            new PeriodOption("Today", t => (t, t)),
            new PeriodOption("Choose dates", t => (t, t)),
        };
        _period = Periods[0];
        (_from, _to) = _period.Range(DateTime.Today);
    }

    public IReadOnlyList<PeriodOption> Periods { get; }
    public ObservableCollection<StatementRow> Rows { get; } = new();
    public ObservableCollection<ItemProfitRow> Items { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();

    [ObservableProperty] private PeriodOption _period;
    [ObservableProperty] private DateTime _from;
    [ObservableProperty] private DateTime _to;
    [ObservableProperty] private ProfitLossReport? _report;
    [ObservableProperty] private string _status = "Choose a period and press Prepare.";
    [ObservableProperty] private bool _isBusy;

    public bool HasReport => Report != null;
    public string CurrentHeader => Report == null ? "This period" : $"{Report.Current.From:dd-MMM-yy} to {Report.Current.To:dd-MMM-yy}";
    public string PreviousHeader => Report == null ? "Before" : $"{Report.Previous.From:dd-MMM-yy} to {Report.Previous.To:dd-MMM-yy}";
    public string Headline => Report == null ? "" : $"Net profit {Money.Format(Report.Current.NetProfitPaise)}  on net sales of {Money.Format(Report.Current.NetSalesPaise)}";
    public bool IsLoss => Report?.Current.NetProfitPaise < 0;

    partial void OnPeriodChanged(PeriodOption value)
    {
        if (value.Label == "Choose dates") return;
        _settingDates = true;
        (From, To) = value.Range(DateTime.Today);
        _settingDates = false;
        _ = PrepareAsync();
    }

    partial void OnFromChanged(DateTime value) { if (!_settingDates) Period = Periods[^1]; }
    partial void OnToChanged(DateTime value) { if (!_settingDates) Period = Periods[^1]; }

    partial void OnReportChanged(ProfitLossReport? value)
    {
        OnPropertyChanged(nameof(HasReport)); OnPropertyChanged(nameof(CurrentHeader)); OnPropertyChanged(nameof(PreviousHeader));
        OnPropertyChanged(nameof(Headline)); OnPropertyChanged(nameof(IsLoss));
        ExportCommand.NotifyCanExecuteChanged(); PrintCommand.NotifyCanExecuteChanged();
    }

    private static string M(long p) => Money.FormatPlain(p);
    private static string Pct(long bp) => (bp / 100m).ToString("0.0") + "%";

    [RelayCommand]
    public async Task PrepareAsync()
    {
        IsBusy = true;
        try
        {
            var r = await _pnl.BuildAsync(DateOnly.FromDateTime(From), DateOnly.FromDateTime(To));
            Report = r;
            Rows.Clear();
            foreach (var x in PnlStatement.Rows(r))
            {
                var change = x.Kind == PnlRowKind.Heading ? "" : x.Kind == PnlRowKind.Percent
                    ? ((x.Current - x.Previous) / 100m).ToString("+0.0;-0.0;0.0") + " pts" : M(x.Current - x.Previous);
                var cur = x.Kind == PnlRowKind.Heading ? "" : x.Kind == PnlRowKind.Percent ? Pct(x.Current) : M(x.Current);
                var prev = x.Kind == PnlRowKind.Heading ? "" : x.Kind == PnlRowKind.Percent ? Pct(x.Previous) : M(x.Previous);
                Rows.Add(new StatementRow(x.Label, cur, prev, change, x.Kind));
            }
            Items.Clear();
            foreach (var i in r.Items) Items.Add(i);
            Notes.Clear();
            foreach (var n in r.Notes) Notes.Add(n);
            Status = r.Current.NetSalesPaise == 0 && r.Current.TotalExpensesPaise == 0 ? "Nothing was billed in this period." : "Read from the books. For you and your accountant to review.";
        }
        catch (BusinessRuleException ex) { Status = ex.Message; }
        catch (Exception ex) { _ui.ShowError(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void Export()
    {
        if (Report == null) return;
        var name = $"Profit-and-loss-{Report.Current.From:yyyyMMdd}-{Report.Current.To:yyyyMMdd}.xlsx";
        var path = _ui.PickSaveFile(name, "Excel workbook (*.xlsx)|*.xlsx");
        if (path == null) return;
        try
        {
            PnlExcelExporter.Write(Report, path);
            Status = $"Saved {Path.GetFileName(path)}.";
            _ui.Info($"The profit and loss was saved to:\n{path}");
        }
        catch (IOException ex) { Status = "The file could not be saved. If it is open in Excel, close it and try again."; Serilog.Log.Warning(ex, "P&L export failed"); }
        catch (UnauthorizedAccessException) { Status = "You do not have permission to save in that folder. Choose another folder."; }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void Print()
    {
        if (Report == null) return;
        var r = Report;
        _ui.ShowDocument("Profit and loss", () => OtherDocuments.BuildProfitLoss(r));
    }
}
