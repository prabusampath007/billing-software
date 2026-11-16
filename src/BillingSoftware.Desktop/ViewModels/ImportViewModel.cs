using System.Collections.ObjectModel;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed record ImportKindItem(ImportKind Kind, string Label, string Help);

public sealed class ImportRowItem(ImportRow r)
{
    public string RowText => r.RowNumber.ToString();
    /// <summary>The values of the file's columns for this row, in column order.</summary>
    public string[] Cells { get; } = (r.Cells ?? Array.Empty<string>()).ToArray();
    public string Name => r.Name;
    public string StatusText => r.Status switch
    {
        ImportRowStatus.Ready => "Ready", ImportRowStatus.Duplicate => "Already there", ImportRowStatus.Error => "Problem", _ => "Imported",
    };
    public string Detail => r.Detail;
    public bool IsError => r.Status == ImportRowStatus.Error;
    public bool IsSkipped => r.Status == ImportRowStatus.Duplicate;
}

public sealed partial class ImportViewModel : ObservableObject
{
    private readonly IImportService _import;
    private readonly IUiHost _ui;
    private int _generation;

    public ImportViewModel(IImportService import, IUiHost ui)
    {
        _import = import; _ui = ui;
        Kinds = new[]
        {
            new ImportKindItem(ImportKind.Items, "Items", "Name and price are needed. Code, barcode, HSN, GST %, unit, MRP, \"Price includes GST\", opening stock and its cost are optional."),
            new ImportKindItem(ImportKind.Customers, "Customers", "Name is needed. Phone, GSTIN, state code, credit limit and what the customer owes today (opening due) are optional."),
            new ImportKindItem(ImportKind.Suppliers, "Suppliers", "Name is needed. Phone, GSTIN, state code and what you owe the supplier today (opening payable) are optional."),
        };
        _kind = Kinds[0];
        _asAt = DateTime.Today.AddDays(-1);
    }

    public IReadOnlyList<ImportKindItem> Kinds { get; }
    public ObservableCollection<ImportRowItem> Rows { get; } = new();

    [ObservableProperty] private ImportKindItem _kind;
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private DateTime _asAt;
    [ObservableProperty] private bool _needsAsAt;
    [ObservableProperty] private int _readyCount;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _isBusy;
    /// <summary>The panel on the right (what to import, which file, the button). It closes once the import is done.</summary>
    [ObservableProperty] private bool _isPanelOpen = true;
    [ObservableProperty] private bool _isDone;

    /// <summary>Headings of the file's columns shown in the table; the screen rebuilds its table when they change.</summary>
    public IReadOnlyList<string> Columns { get; private set; } = Array.Empty<string>();
    public event Action? ColumnsChanged;
    private void SetColumns(IReadOnlyList<string>? columns) { Columns = columns ?? Array.Empty<string>(); ColumnsChanged?.Invoke(); }

    public bool HasFile => FilePath.Length > 0;
    public string ImportButtonText => ReadyCount > 0 ? $"Import {ReadyCount} row{(ReadyCount == 1 ? "" : "s")}" : "Import";

    partial void OnKindChanged(ImportKindItem value) { if (HasFile) _ = PreviewAsync(); }
    partial void OnReadyCountChanged(int value) { OnPropertyChanged(nameof(ImportButtonText)); ImportNowCommand.NotifyCanExecuteChanged(); }
    partial void OnIsBusyChanged(bool value) => ImportNowCommand.NotifyCanExecuteChanged();
    partial void OnFilePathChanged(string value) => OnPropertyChanged(nameof(HasFile));

    [RelayCommand]
    private async Task ChooseFileAsync()
    {
        var f = _ui.PickOpenFile("Choose the file to import", "Excel or CSV files (*.xlsx;*.csv)|*.xlsx;*.csv|All files (*.*)|*.*");
        if (f == null) return;
        FilePath = f;
        await PreviewAsync();
    }

    [RelayCommand]
    private async Task SaveTemplateAsync()
    {
        var path = _ui.PickSaveFile($"{Kind.Label}-import-template.xlsx", "Excel workbook (*.xlsx)|*.xlsx");
        if (path == null) return;
        try { await _import.WriteTemplateAsync(Kind.Kind, path); Set($"Saved a blank {Kind.Label.ToLowerInvariant()} template. Fill it in, then choose it above.", false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Set("The file could not be saved. Is it open in another program?", true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    public async Task PreviewAsync()
    {
        var generation = ++_generation;
        IsBusy = true;
        try
        {
            var p = await _import.PreviewAsync(Kind.Kind, FilePath);
            if (generation != _generation) return;
            Rows.Clear();
            foreach (var r in p.Rows) Rows.Add(new ImportRowItem(r));
            SetColumns(p.Columns);
            ReadyCount = p.ReadyCount; NeedsAsAt = p.NeedsAsAtDate;
            Problem = p.FileProblem ?? "";
            Summary = p.FileProblem != null ? "" :
                $"{p.TotalRows} row{(p.TotalRows == 1 ? "" : "s")} in the file: {p.ReadyCount} ready, {p.DuplicateCount} already in the software (skipped), {p.ErrorCount} with a problem (not imported)."
                + (p.IgnoredColumns.Count > 0 ? $" Columns not used: {string.Join(", ", p.IgnoredColumns)}." : "");
            Set("", false);
        }
        catch (BusinessRuleException ex) { Problem = ex.Message; Rows.Clear(); ReadyCount = 0; Summary = ""; }
        catch (Exception ex) { _ui.ShowError(ex); }
        finally { IsBusy = false; }
    }

    /// <summary>Back to the normal state: nothing chosen, no table, the panel open again.</summary>
    [RelayCommand]
    private void Reset()
    {
        _generation++;
        Rows.Clear(); SetColumns(null);
        FilePath = Summary = Problem = Status = "";
        StatusIsError = false; ReadyCount = 0; NeedsAsAt = false; IsDone = false; IsPanelOpen = true;
        AsAt = DateTime.Today.AddDays(-1);
    }

    private bool CanImport() => ReadyCount > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportNowAsync()
    {
        if (!_ui.Confirm($"Import {ReadyCount} {Kind.Label.ToLowerInvariant()} into the software?\n\nRows already there and rows with a problem are skipped.", "Import")) return;
        IsBusy = true;
        try
        {
            var r = await _import.ImportAsync(Kind.Kind, FilePath, NeedsAsAt ? DateOnly.FromDateTime(AsAt) : null);
            Rows.Clear();
            foreach (var x in r.Rows) Rows.Add(new ImportRowItem(x));
            SetColumns(r.Columns);
            ReadyCount = 0;
            Summary = $"Imported {r.Imported}. {r.Duplicates} already there (skipped). {r.Errors} with a problem (not imported). Fix those rows in the file and import it again: rows already imported are skipped.";
            Set("", false);
            IsPanelOpen = false;                       // the table now has the whole width; "Import another file" brings the panel back
            IsDone = true;
        }
        catch (BusinessRuleException ex) { Set(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
        finally { IsBusy = false; }
    }

    private void Set(string m, bool e) { Status = m; StatusIsError = e; }
}
