using System.Collections.ObjectModel;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Export;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed record SummaryLine(string Label, string Taxable, string Igst, string Cgst, string Sgst, bool IsHeading = false);

public sealed record MonthItem(int Number, string Name);

public sealed partial class GstViewModel : ObservableObject
{
    private readonly IGstReportService _gst;
    private readonly IUiHost _ui;

    public GstViewModel(IGstReportService gst, IUiHost ui)
    {
        _gst = gst; _ui = ui;
        var today = DateTime.Today;
        // The month people usually want is the one that just ended; during the first days of a month that is last month.
        var start = today.Day <= 10 ? today.AddMonths(-1) : today;
        for (var y = today.Year - 3; y <= today.Year; y++) Years.Add(y);
        for (var m = 1; m <= 12; m++) Months.Add(new MonthItem(m, new DateTime(2000, m, 1).ToString("MMMM")));
        _year = start.Year;
        _month = Months[start.Month - 1];
    }

    public ObservableCollection<int> Years { get; } = new();
    public ObservableCollection<MonthItem> Months { get; } = new();
    public ObservableCollection<SummaryLine> Summary { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();

    [ObservableProperty] private int _year;
    [ObservableProperty] private MonthItem _month;
    [ObservableProperty] private GstReport? _report;
    [ObservableProperty] private string _status = "Choose a month and press Prepare.";
    [ObservableProperty] private bool _isBusy;

    public bool HasReport => Report != null;
    public bool IsComposition => Report?.IsComposition == true;
    public string Heading => Report == null ? "GST worksheets" : $"GST worksheets for {Report.PeriodLabel}";

    partial void OnReportChanged(GstReport? value)
    {
        OnPropertyChanged(nameof(HasReport)); OnPropertyChanged(nameof(IsComposition)); OnPropertyChanged(nameof(Heading));
        ExportCommand.NotifyCanExecuteChanged(); PrintSummaryCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task PrepareAsync()
    {
        IsBusy = true;
        try
        {
            var r = await _gst.BuildAsync(Year, Month.Number);
            Report = r;
            BuildSummary(r);
            Warnings.Clear();
            foreach (var w in r.Warnings) Warnings.Add(w);
            Status = $"Prepared {r.PeriodLabel}: {r.SalesRegister.Count} sales documents, {r.PurchaseRegister.Count} purchases. For your accountant to review; nothing has been filed.";
        }
        catch (BusinessRuleException ex) { Status = ex.Message; }
        catch (Exception ex) { _ui.ShowError(ex); }
        finally { IsBusy = false; }
    }

    private static string M(long p) => Money.FormatPlain(p);

    private void BuildSummary(GstReport r)
    {
        Summary.Clear();
        if (r.IsComposition)
        {
            Summary.Add(new("Composition scheme: turnover for the month (after returns)", M(r.CompositionTurnoverPaise), "", "", ""));
            return;
        }
        var g = r.Gstr3b;
        Summary.Add(new("3.1 Outward supplies (after credit notes)", "Taxable value", "IGST", "CGST", "SGST", true));
        Summary.Add(new("(a) Taxable supplies", M(g.OutTaxablePaise), M(g.OutIgstPaise), M(g.OutCgstPaise), M(g.OutSgstPaise)));
        Summary.Add(new("(c) Nil-rated / exempt (0% items, to be classified)", M(g.NilRatedTaxablePaise), "", "", ""));
        Summary.Add(new("3.2 Inter-state to unregistered customers", "Taxable value", "IGST", "", "", true));
        if (g.InterStateToUnregistered.Count == 0) Summary.Add(new("None this month", "", "", "", ""));
        foreach (var p in g.InterStateToUnregistered) Summary.Add(new($"Place of supply {p.PlaceOfSupply}", M(p.TaxablePaise), M(p.IgstPaise), "", ""));
        Summary.Add(new("4 Input tax credit", "", "IGST", "CGST", "SGST", true));
        Summary.Add(new("(A)(5) Credit claimed on purchases (after debit notes)", "", M(g.ItcIgstPaise), M(g.ItcCgstPaise), M(g.ItcSgstPaise)));
        Summary.Add(new("Tax on purchases where credit was not claimed", "", M(g.IneligibleIgstPaise), M(g.IneligibleCgstPaise), M(g.IneligibleSgstPaise)));
        Summary.Add(new("5 Inward supplies at 0%", "Taxable value", "", "", "", true));
        Summary.Add(new("From inside the state", M(g.InwardNilIntraPaise), "", "", ""));
        Summary.Add(new("From other states", M(g.InwardNilInterPaise), "", "", ""));
        Summary.Add(new("Net tax by head (negative = credit left over)", "", "IGST", "CGST", "SGST", true));
        Summary.Add(new("Output tax less input credit", "", M(g.NetIgstPaise), M(g.NetCgstPaise), M(g.NetSgstPaise)));
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void Export()
    {
        if (Report == null) return;
        var name = $"GST-{Report.Year}-{Report.Month:D2}-{new string(Report.CompanyName.Where(char.IsLetterOrDigit).Take(20).ToArray())}.xlsx";
        var path = _ui.PickSaveFile(name, "Excel workbook (*.xlsx)|*.xlsx");
        if (path == null) return;
        try
        {
            GstExcelExporter.Write(Report, path);
            Status = $"Saved {Path.GetFileName(path)}. Send it to your accountant.";
            _ui.Info($"The GST worksheets were saved to:\n{path}");
        }
        catch (IOException ex)
        {
            Status = "The file could not be saved. If it is open in Excel, close it and try again.";
            Serilog.Log.Warning(ex, "GST export failed");
        }
        catch (UnauthorizedAccessException) { Status = "You do not have permission to save in that folder. Choose another folder."; }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void PrintSummary()
    {
        if (Report == null) return;
        var r = Report;
        _ui.ShowDocument($"GST summary {r.PeriodLabel}", () => OtherDocuments.BuildGstSummary(r));
    }
}
