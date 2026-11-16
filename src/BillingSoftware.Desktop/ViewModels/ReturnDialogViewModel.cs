using System.Collections.ObjectModel;
using System.Globalization;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed partial class ReturnLineVm : ObservableObject
{
    private readonly Action _changed;
    public ReturnLineVm(ReturnableLineDto line, Action changed) { Line = line; _changed = changed; }

    public ReturnableLineDto Line { get; }
    public string Name => Line.Name;
    public string SoldText => $"{Qty.Format(Line.OriginalQtyMilli)} {Line.Unit}";
    public string BeforeText => Line.ReturnedQtyMilli == 0 ? "-" : Qty.Format(Line.ReturnedQtyMilli);
    public string RemainingText => Qty.Format(Line.RemainingMilli);
    public bool CanReturn => Line.RemainingMilli > 0;

    [ObservableProperty] private string _qtyText = "";
    [ObservableProperty] private bool _restock = true;
    [ObservableProperty] private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnQtyTextChanged(string value) => _changed();
    partial void OnRestockChanged(bool value) => _changed();

    /// <summary>Parsed quantity in thousandths: 0 when empty, -1 when not a valid number.</summary>
    public long QtyMilli
    {
        get
        {
            if (string.IsNullOrWhiteSpace(QtyText)) return 0;
            return decimal.TryParse(QtyText.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var d) && d >= 0 ? Qty.ToMilli(d) : -1;
        }
    }
}

public sealed partial class ReturnDialogViewModel : ObservableObject
{
    private readonly ReturnDialogRequest _request;
    private readonly IUiHost _ui;
    private readonly Guid _commandId = Guid.NewGuid();
    private int _generation;

    public ReturnDialogViewModel(ReturnDialogRequest request, IUiHost ui)
    {
        _request = request; _ui = ui;
        foreach (var l in request.Lines) Lines.Add(new ReturnLineVm(l, () => _ = RecomputeAsync()));
        Modes = new[]
        {
            new PaymentModeItem(PaymentMode.Cash, "Cash"), new PaymentModeItem(PaymentMode.Upi, "UPI"), new PaymentModeItem(PaymentMode.Card, "Card"),
            new PaymentModeItem(PaymentMode.Credit, request.CreditModeLabel),
        };
        _selectedMode = Modes[0];
    }

    public event Action<bool>? RequestClose;

    public string Title => _request.Title;
    public string Subtitle => _request.Subtitle;
    public bool AllowRestock => _request.AllowRestock;
    public ObservableCollection<ReturnLineVm> Lines { get; } = new();
    public IReadOnlyList<PaymentModeItem> Modes { get; }
    public ReturnResult? Result { get; private set; }

    [ObservableProperty] private PaymentModeItem _selectedMode;
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private string _previewText = "Enter the quantity to return.";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canConfirm;

    partial void OnSelectedModeChanged(PaymentModeItem value) => _ = RecomputeAsync();
    partial void OnReasonChanged(string value) => UpdateCanConfirm();
    partial void OnIsBusyChanged(bool value) { UpdateCanConfirm(); ConfirmCommand.NotifyCanExecuteChanged(); }

    private bool _valid;
    private void UpdateCanConfirm()
    {
        CanConfirm = _valid && !IsBusy && Reason.Trim().Length >= 3;
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    private List<ReturnLineInput>? Collect()
    {
        var inputs = new List<ReturnLineInput>();
        var ok = true;
        foreach (var l in Lines)
        {
            var q = l.QtyMilli;
            l.Error = null;
            if (q < 0) { l.Error = "Enter a number"; ok = false; }
            else if (q > l.Line.RemainingMilli) { l.Error = $"At most {l.RemainingText}"; ok = false; }
            else if (q > 0) inputs.Add(new ReturnLineInput(l.Line.LineId, q, l.Restock));
        }
        return ok ? inputs : null;
    }

    public async Task RecomputeAsync()
    {
        var generation = ++_generation;
        ErrorText = "";
        var inputs = Collect();
        if (inputs == null) { _valid = false; PreviewText = "Fix the quantities marked in red."; UpdateCanConfirm(); return; }
        if (inputs.Count == 0) { _valid = false; PreviewText = "Enter the quantity to return."; UpdateCanConfirm(); return; }
        try
        {
            var p = await _request.Preview(inputs, SelectedMode.Mode, Reason);
            if (generation != _generation) return;                  // a newer edit has already replaced this result
            PreviewText = $"Amount: {Money.Format(p.TotalPaise)}   (tax {Money.FormatPlain(p.TaxPaise)}" + (p.RoundOffPaise != 0 ? $", round off {Money.FormatPlain(p.RoundOffPaise)}" : "") + ")";
            _valid = true;
        }
        catch (BusinessRuleException ex) { if (generation != _generation) return; _valid = false; PreviewText = ""; ErrorText = ex.Message; }
        catch (Exception ex) { _valid = false; _ui.ShowError(ex); }
        UpdateCanConfirm();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var l in Lines.Where(l => l.CanReturn)) l.QtyText = l.RemainingText;
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        var inputs = Collect();
        if (inputs == null || inputs.Count == 0) return;
        IsBusy = true;
        ErrorText = "";
        try
        {
            long? approver = null;
            while (Result == null)
            {
                try { Result = await _request.Submit(inputs, SelectedMode.Mode, Reason.Trim(), _commandId, approver); }
                catch (ApprovalRequiredException ex)
                {
                    approver = await _ui.RequestApprovalAsync(ex.Message);
                    if (approver == null) { ErrorText = "Not approved. Nothing was changed."; return; }
                }
            }
            RequestClose?.Invoke(true);
        }
        catch (BusinessRuleException ex) { ErrorText = ex.Message; }
        catch (Exception ex) { _ui.ShowError(ex); ErrorText = "It was not saved. You can try again."; }
        finally { IsBusy = false; }
    }
}
