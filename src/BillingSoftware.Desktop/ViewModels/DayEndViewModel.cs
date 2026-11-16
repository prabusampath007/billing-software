using System.Collections.ObjectModel;
using System.Globalization;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed record MovementTypeItem(CashMovementType Type, string Label);

public sealed class MovementRow(CashMovementDto m)
{
    public string TimeText => m.Date.ToString("dd-MMM HH:mm");
    public string Text => $"{DayEndViewModel.MovementLabel(m.Type)}: {m.Reason}";
    public string AmountText => (m.IsMoneyIn ? "+ " : "- ") + Money.FormatPlain(m.AmountPaise);
}

public sealed class CloseRow(DayCloseListItem c)
{
    public DayCloseListItem Item { get; } = c;
    public string DateText => c.PeriodEnd.ToString("dd-MMM-yy HH:mm");
    public string CountedText => Money.FormatPlain(c.CountedPaise);
    public string DifferenceText => c.VariancePaise == 0 ? "0.00" : (c.VariancePaise > 0 ? "+" : "-") + Money.FormatPlain(Math.Abs(c.VariancePaise));
}

public sealed partial class DayEndViewModel : ObservableObject
{
    private readonly IDayCloseService _close;
    private readonly ISettingsService _settings;
    private readonly IUiHost _ui;
    private int _generation;

    public DayEndViewModel(IDayCloseService close, ISettingsService settings, IUiHost ui)
    {
        _close = close; _settings = settings; _ui = ui;
        MovementTypes = new[]
        {
            new MovementTypeItem(CashMovementType.Expense, "Shop expense paid from the drawer (tea, courier ...)"),
            new MovementTypeItem(CashMovementType.BankDeposit, "Cash deposited in the bank"),
            new MovementTypeItem(CashMovementType.OwnerWithdrawal, "Money taken out by the owner"),
            new MovementTypeItem(CashMovementType.OwnerPayIn, "Money put in by the owner"),
            new MovementTypeItem(CashMovementType.BankWithdrawal, "Cash taken from the bank"),
            new MovementTypeItem(CashMovementType.LoanReceived, "Loan received in cash"),
            new MovementTypeItem(CashMovementType.LoanRepaid, "Loan repaid in cash"),
        };
        _movementType = MovementTypes[0];
    }

    public static string MovementLabel(CashMovementType t) => t switch
    {
        CashMovementType.OwnerPayIn => "Put in by owner", CashMovementType.BankWithdrawal => "From bank", CashMovementType.Expense => "Expense",
        CashMovementType.OwnerWithdrawal => "Owner took", CashMovementType.BankDeposit => "To bank", CashMovementType.LoanReceived => "Loan received", CashMovementType.LoanRepaid => "Loan repaid", _ => t.ToString(),
    };

    public IReadOnlyList<MovementTypeItem> MovementTypes { get; }
    public ObservableCollection<KeyValueRow> CashLines { get; } = new();
    public ObservableCollection<KeyValueRow> SalesLines { get; } = new();
    public ObservableCollection<MovementRow> Movements { get; } = new();
    public ObservableCollection<CloseRow> Closes { get; } = new();

    [ObservableProperty] private DayClosePreview? _preview;
    [ObservableProperty] private string _openingFloatText = "";
    [ObservableProperty] private string _countedText = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private MovementTypeItem _movementType;
    [ObservableProperty] private string _movementAmountText = "";
    [ObservableProperty] private string _movementReason = "";
    [ObservableProperty] private CloseRow? _selectedClose;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private string _differenceText = "Enter the cash you counted.";
    [ObservableProperty] private int _differenceKind;        // 0 none, 1 exact, 2 over/short
    [ObservableProperty] private bool _isBusy;

    public bool IsFirstClose => Preview?.IsFirstClose == true;
    /// <summary>The cash in hand was entered under Opening balances, so no opening cash is asked for here.</summary>
    public bool AskForOpeningCash => IsFirstClose && Preview?.OpeningCashRecorded != true;
    public string ExpectedText => Preview == null ? "" : Money.Format(Preview.Report.ExpectedCashPaise);
    public string PeriodText => Preview == null ? "" :
        Preview.LastClosedAt is { } t ? $"Since the last close on {t:dd-MMM-yyyy HH:mm}" : "Since you started using the software (first close)";
    public bool ExpectedNegative => Preview?.Report.ExpectedCashPaise < 0;
    public bool NeedsNote => DifferenceKind == 2;

    partial void OnPreviewChanged(DayClosePreview? value)
    {
        OnPropertyChanged(nameof(IsFirstClose)); OnPropertyChanged(nameof(AskForOpeningCash)); OnPropertyChanged(nameof(ExpectedText)); OnPropertyChanged(nameof(PeriodText)); OnPropertyChanged(nameof(ExpectedNegative));
        UpdateDifference();
    }
    partial void OnDifferenceKindChanged(int value) { OnPropertyChanged(nameof(NeedsNote)); CloseDayCommand.NotifyCanExecuteChanged(); }
    partial void OnCountedTextChanged(string value) => UpdateDifference();
    partial void OnNoteChanged(string value) => CloseDayCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => CloseDayCommand.NotifyCanExecuteChanged();
    partial void OnOpeningFloatTextChanged(string value) => _ = RefreshAsync();

    private static bool TryMoney(string? text, out long paise)
    {
        paise = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var d) || d < 0 || d > 1_000_000_000m) return false;
        paise = Money.ToPaise(d);
        return true;
    }

    private void UpdateDifference()
    {
        if (Preview == null) return;
        if (string.IsNullOrWhiteSpace(CountedText)) { DifferenceText = "Enter the cash you counted."; DifferenceKind = 0; return; }
        if (!TryMoney(CountedText, out var counted)) { DifferenceText = "Enter the counted cash as a number."; DifferenceKind = 0; return; }
        var diff = counted - Preview.Report.ExpectedCashPaise;
        if (diff == 0) { DifferenceText = "Exactly right. The cash matches the books."; DifferenceKind = 1; }
        else { DifferenceText = (diff > 0 ? "Over by " : "Short by ") + Money.Format(Math.Abs(diff)) + ". Write a note below."; DifferenceKind = 2; }
    }

    public async Task RefreshAsync()
    {
        var generation = ++_generation;
        try
        {
            TryMoney(OpeningFloatText, out var floatPaise);
            var p = await _close.PreviewAsync(floatPaise);
            if (generation != _generation) return;
            var r = p.Report;
            CashLines.Clear();
            foreach (var l in r.CashLines) CashLines.Add(new KeyValueRow(l.Label, (l.AmountPaise < 0 ? "- " : "") + Money.FormatPlain(Math.Abs(l.AmountPaise))));
            SalesLines.Clear();
            SalesLines.Add(new("Bills made", r.BillsMade.ToString()));
            SalesLines.Add(new("Sales", Money.Format(r.GrossSalesPaise)));
            if (r.BillsVoided > 0) SalesLines.Add(new($"Bills voided ({r.BillsVoided})", "- " + Money.Format(r.VoidedPaise)));
            if (r.ReturnCount > 0) SalesLines.Add(new($"Returns ({r.ReturnCount})", "- " + Money.Format(r.ReturnsPaise)));
            SalesLines.Add(new("Net sales", Money.Format(r.NetSalesPaise)));
            foreach (var m in r.SalesByMode) SalesLines.Add(new("   " + m.Label, Money.Format(m.AmountPaise)));
            foreach (var c in r.ByCashier) SalesLines.Add(new($"   {c.Cashier} ({c.Bills} bill{(c.Bills == 1 ? "" : "s")})", Money.Format(c.TotalPaise)));
            Movements.Clear();
            foreach (var m in await _close.ListMovementsAsync()) Movements.Add(new MovementRow(m));
            Closes.Clear();
            foreach (var c in await _close.ListAsync(30)) Closes.Add(new CloseRow(c));
            Preview = p;
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand]
    private async Task RecordMovementAsync()
    {
        if (!TryMoney(MovementAmountText, out var amount) || amount <= 0) { Set("Enter the amount as a number.", true); return; }
        try
        {
            await _close.RecordMovementAsync(MovementType.Type, amount, MovementReason);
            MovementAmountText = MovementReason = "";
            Set("Recorded.", false);
            await RefreshAsync();
        }
        catch (BusinessRuleException ex) { Set(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private bool CanClose() => !IsBusy && Preview != null && DifferenceKind != 0 && (DifferenceKind == 1 || Note.Trim().Length >= 3);

    [RelayCommand(CanExecute = nameof(CanClose))]
    private async Task CloseDayAsync()
    {
        if (Preview == null || !TryMoney(CountedText, out var counted)) return;
        TryMoney(OpeningFloatText, out var floatPaise);
        if (Preview.Report.ExpectedCashPaise < 0 && !_ui.Confirm("The books say the drawer should hold less than zero, which usually means a pay-out or purchase was entered wrongly. Close anyway?")) return;
        if (!_ui.Confirm($"Close the day?\n\nCounted: {Money.Format(counted)}\nExpected: {Money.Format(Preview.Report.ExpectedCashPaise)}\n\nThis cannot be undone. The counted cash becomes the start of the next period.")) return;
        IsBusy = true;
        try
        {
            var result = await _close.CloseAsync(new DayCloseCommand(Guid.NewGuid(), counted, floatPaise, string.IsNullOrWhiteSpace(Note) ? null : Note));
            CountedText = Note = OpeningFloatText = "";
            Set(result.VariancePaise == 0 ? "The day is closed. The cash matched exactly." : $"The day is closed. The cash was {(result.VariancePaise > 0 ? "over" : "short")} by {Money.Format(Math.Abs(result.VariancePaise))}.", false);
            await RefreshAsync();
        }
        catch (BusinessRuleException ex) { Set(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); Set("The day was not closed. You can try again.", true); }
        finally { IsBusy = false; }
    }

    private void Set(string m, bool e) { Status = m; StatusIsError = e; }
}
