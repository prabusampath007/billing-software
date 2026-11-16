using System.Collections.ObjectModel;
using System.Globalization;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed partial class PurchaseCartLine : ObservableObject
{
    private readonly Action _changed;
    public PurchaseCartLine(ProductDto product, Action changed, bool canClaimCredit)
    {
        Product = product; _changed = changed;
        _unitCost = product.AvgCostPaise > 0 ? Money.ToRupees(product.AvgCostPaise) : 0;
        _gstPercent = product.GstRateBp / 100m;
        _itcEligible = canClaimCredit;
        CanClaimCredit = canClaimCredit;
    }

    public ProductDto Product { get; }
    public string Name => Product.Name;
    public string Unit => Product.Unit;
    public bool CanClaimCredit { get; }

    [ObservableProperty] private decimal _quantity = 1;
    [ObservableProperty] private decimal _unitCost;
    [ObservableProperty] private decimal _gstPercent;
    [ObservableProperty] private decimal _discount;
    [ObservableProperty] private bool _itcEligible;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private long _lineTotalPaise;

    public string TotalText => Money.FormatPlain(LineTotalPaise);
    public bool HasError => !string.IsNullOrEmpty(Error);

    partial void OnQuantityChanged(decimal value) => _changed();
    partial void OnUnitCostChanged(decimal value) => _changed();
    partial void OnGstPercentChanged(decimal value) => _changed();
    partial void OnDiscountChanged(decimal value) => _changed();
    partial void OnItcEligibleChanged(bool value) => _changed();
    partial void OnLineTotalPaiseChanged(long value) => OnPropertyChanged(nameof(TotalText));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
}

public sealed partial class PurchaseEntryViewModel : ObservableObject
{
    private readonly IPurchaseService _purchases;
    private readonly ISupplierService _suppliers;
    private readonly ICatalogService _catalog;
    private readonly ISettingsService _settings;
    private readonly IUiHost _ui;
    private Guid _commandId = Guid.NewGuid();
    private bool _recalculating;

    public PurchaseEntryViewModel(IPurchaseService purchases, ISupplierService suppliers, ICatalogService catalog, ISettingsService settings, IUiHost ui)
    {
        _purchases = purchases; _suppliers = suppliers; _catalog = catalog; _settings = settings; _ui = ui;
        Lines.CollectionChanged += (_, _) => Recalculate();
    }

    public event Action<bool>? RequestClose;
    public CompanyDto? Company { get; private set; }
    public bool CanClaimCredit => Company is { CollectsTax: true };

    public ObservableCollection<PurchaseCartLine> Lines { get; } = new();
    public ObservableCollection<ProductDto> Matches { get; } = new();
    public ObservableCollection<SupplierDto> SupplierMatches { get; } = new();

    [ObservableProperty] private string _scanText = "";
    [ObservableProperty] private string _supplierQuery = "";
    [ObservableProperty] private SupplierDto? _supplier;
    [ObservableProperty] private string _invoiceNo = "";
    [ObservableProperty] private DateTime? _invoiceDate = DateTime.Today;
    [ObservableProperty] private string _cashText = "";
    [ObservableProperty] private string _upiText = "";
    [ObservableProperty] private string _cardText = "";
    [ObservableProperty] private string _creditText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private long _taxablePaise;
    [ObservableProperty] private long _taxPaise;
    [ObservableProperty] private long _roundOffPaise;
    [ObservableProperty] private long _totalPaise;
    [ObservableProperty] private string _paymentNote = "";
    [ObservableProperty] private bool _paymentOk;

    public bool HasMatches => Matches.Count > 0;
    public bool HasSupplierMatches => SupplierMatches.Count > 0;
    public bool HasSupplier => Supplier != null;
    public string SupplierText => Supplier == null ? "" : Supplier.OutstandingPaise > 0 ? $"{Supplier.Name}  (you owe {Money.Format(Supplier.OutstandingPaise)})" : Supplier.Name;
    public string TotalText => Money.Format(TotalPaise);
    public string TaxableText => Money.FormatPlain(TaxablePaise);
    public string TaxText => Money.FormatPlain(TaxPaise);
    public string RoundOffText => Money.FormatPlain(RoundOffPaise);
    public string ItemCountText => Lines.Count == 0 ? "No items yet" : $"{Lines.Count} item{(Lines.Count == 1 ? "" : "s")}";

    public async Task InitializeAsync()
    {
        try { Company = await _settings.GetCompanyAsync(); OnPropertyChanged(nameof(CanClaimCredit)); Recalculate(); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    // ---------- supplier ----------
    [RelayCommand]
    private async Task SearchSupplierAsync()
    {
        var q = SupplierQuery.Trim();
        SupplierMatches.Clear();
        try
        {
            foreach (var s in await _suppliers.SearchAsync(q, 8)) SupplierMatches.Add(s);
            OnPropertyChanged(nameof(HasSupplierMatches));
            if (SupplierMatches.Count == 0) SetStatus("No supplier found. Add suppliers in the Suppliers screen first.", true);
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand]
    private void PickSupplier(SupplierDto? s)
    {
        if (s == null) return;
        Supplier = s; SupplierQuery = ""; SupplierMatches.Clear();
        OnPropertyChanged(nameof(HasSupplierMatches));
    }

    [RelayCommand] private void ClearSupplier() => Supplier = null;

    partial void OnSupplierChanged(SupplierDto? value)
    {
        OnPropertyChanged(nameof(HasSupplier)); OnPropertyChanged(nameof(SupplierText));
        Recalculate();
    }

    // ---------- items ----------
    [RelayCommand]
    private async Task ScanAsync()
    {
        var text = ScanText.Trim();
        if (text.Length == 0) return;
        Matches.Clear();
        OnPropertyChanged(nameof(HasMatches));
        try
        {
            var exact = await _catalog.ScanAsync(text);
            if (exact != null) { AddProduct(exact.Product, exact.QtyMilli / 1000m, exact.IsPack); ScanText = ""; return; }
            var found = await _catalog.SearchAsync(text, 30);
            if (found.Count == 1) { AddProduct(found[0]); ScanText = ""; }
            else if (found.Count == 0) SetStatus("No item found for \"" + text + "\". Add it in the Items screen first.", true);
            else
            {
                foreach (var p in found) Matches.Add(p);
                OnPropertyChanged(nameof(HasMatches));
                SetStatus($"{found.Count} items match. Choose one.", false);
            }
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand]
    private void PickMatch(ProductDto? product)
    {
        if (product == null) return;
        AddProduct(product);
        Matches.Clear();
        OnPropertyChanged(nameof(HasMatches));
        ScanText = "";
    }

    public void AddProduct(ProductDto product, decimal quantity = 1, bool isPack = false)
    {
        var existing = Lines.FirstOrDefault(l => l.Product.Id == product.Id);
        if (existing != null) existing.Quantity += quantity;
        else Lines.Add(new PurchaseCartLine(product, Recalculate, CanClaimCredit) { Quantity = quantity });
        SetStatus(isPack ? $"Added {quantity:0.###} {product.Unit} of {product.Name} (pack barcode)" : $"Added {product.Name}", false);
    }

    [RelayCommand] private void RemoveLine(PurchaseCartLine? line) { if (line != null) Lines.Remove(line); }

    // ---------- calculation ----------
    private void Recalculate()
    {
        if (_recalculating) return;
        _recalculating = true;
        try
        {
            var interState = Supplier?.StateCode is { Length: > 0 } sc && Company != null && sc != Company.StateCode;
            long taxable = 0, tax = 0, exact = 0;
            foreach (var l in Lines)
            {
                try
                {
                    var t = TaxCalculator.Calculate(Money.ToPaise(l.UnitCost), Qty.ToMilli(l.Quantity), Money.ToPaise(l.Discount),
                        (int)Math.Round(l.GstPercent * 100), interState, priceInclusive: false);
                    l.Error = null; l.LineTotalPaise = t.TotalPaise;
                    taxable += t.TaxablePaise; tax += t.TaxPaise; exact += t.TotalPaise;
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    l.Error = ex.ParamName switch
                    {
                        "discountPaise" => "Discount is more than the amount",
                        "gstRateBp" => "GST must be between 0 and 100",
                        "unitPricePaise" => "Cost cannot be negative",
                        _ => "Quantity must be more than zero",
                    };
                    l.LineTotalPaise = 0;
                }
            }
            var (total, round) = InvoiceRounding.ToNearestRupee(exact);
            TaxablePaise = taxable; TaxPaise = tax; RoundOffPaise = round; TotalPaise = total;
            OnPropertyChanged(nameof(TotalText)); OnPropertyChanged(nameof(TaxableText)); OnPropertyChanged(nameof(TaxText));
            OnPropertyChanged(nameof(RoundOffText)); OnPropertyChanged(nameof(ItemCountText));
        }
        finally { _recalculating = false; }
        RefreshPayment();
    }

    private static bool TryMoney(string? text, out long paise)
    {
        paise = 0;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var d) || d < 0 || d > 100_000_000m) return false;
        paise = Money.ToPaise(d);
        return true;
    }

    private (bool ok, long cash, long upi, long card, long credit) ParsePayments(out string note)
    {
        note = "";
        if (!TryMoney(CashText, out var cashIn) || !TryMoney(UpiText, out var upi) || !TryMoney(CardText, out var card) || !TryMoney(CreditText, out var credit))
        { note = "Enter payment amounts as numbers."; return (false, 0, 0, 0, 0); }
        var others = upi + card + credit;
        if (others > TotalPaise) { note = "UPI, card and on-account add up to more than the purchase."; return (false, 0, 0, 0, 0); }
        var cash = TotalPaise - others;
        if (!string.IsNullOrWhiteSpace(CashText) && cashIn != cash) { note = $"Cash must be {Money.Format(cash)} to match the total, or leave it empty."; return (false, cash, upi, card, credit); }
        note = credit > 0 ? $"{Money.Format(credit)} will be owed to the supplier" : cash > 0 ? $"Pay {Money.Format(cash)} in cash" : "Fully paid by UPI / card";
        return (true, cash, upi, card, credit);
    }

    private void RefreshPayment()
    {
        if (_recalculating) return;
        var (ok, _, _, _, _) = ParsePayments(out var note);
        PaymentNote = Lines.Count == 0 ? "" : note;
        PaymentOk = ok && Lines.Count > 0 && Lines.All(l => !l.HasError) && Supplier != null && !string.IsNullOrWhiteSpace(InvoiceNo);
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnCashTextChanged(string value) => RefreshPayment();
    partial void OnUpiTextChanged(string value) => RefreshPayment();
    partial void OnCardTextChanged(string value) => RefreshPayment();
    partial void OnCreditTextChanged(string value) => RefreshPayment();
    partial void OnInvoiceNoChanged(string value) => RefreshPayment();
    partial void OnIsBusyChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    // ---------- save ----------
    private bool CanSave() => !IsBusy && PaymentOk;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var (ok, cash, upi, card, credit) = ParsePayments(out var note);
        if (!ok || Supplier == null) { SetStatus(note, true); return; }
        var payments = new List<PaymentInput>();
        if (cash > 0) payments.Add(new(PaymentMode.Cash, cash));
        if (upi > 0) payments.Add(new(PaymentMode.Upi, upi));
        if (card > 0) payments.Add(new(PaymentMode.Card, card));
        if (credit > 0) payments.Add(new(PaymentMode.Credit, credit));

        var cmd = new CompletePurchaseCommand(_commandId, Supplier.Id, InvoiceNo, InvoiceDate,
            Lines.Select(l => new PurchaseLineInput(l.Product.Id, Qty.ToMilli(l.Quantity), Money.ToPaise(l.UnitCost),
                (int)Math.Round(l.GstPercent * 100), Money.ToPaise(l.Discount), l.ItcEligible)).ToList(), payments);
        IsBusy = true;
        try
        {
            var result = await _purchases.CompleteAsync(cmd);
            _ui.Info($"Purchase {result.PurchaseNumber} saved. Stock has been updated.");
            RequestClose?.Invoke(true);
        }
        catch (BusinessRuleException ex) { SetStatus(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); SetStatus("The purchase was not saved. You can try again.", true); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (Lines.Count > 0 && !_ui.Confirm("Discard this purchase?")) return;
        RequestClose?.Invoke(false);
    }

    private void SetStatus(string m, bool e) { StatusMessage = m; StatusIsError = e; }
}
