using System.Collections.ObjectModel;
using System.Globalization;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed partial class CartLine : ObservableObject
{
    public CartLine(ProductDto product, Action changed) { Product = product; _changed = changed; }
    private readonly Action _changed;

    public ProductDto Product { get; }
    public string Name => Product.Name;
    public string Unit => Product.Unit;
    public long UnitPricePaise => Product.SellingPricePaise;
    public string PriceText => Money.FormatPlain(Product.SellingPricePaise);

    [ObservableProperty] private decimal _quantity = 1;
    [ObservableProperty] private decimal _discount;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private long _lineTotalPaise;

    public string TotalText => Money.FormatPlain(LineTotalPaise);
    public bool HasError => !string.IsNullOrEmpty(Error);

    partial void OnQuantityChanged(decimal value) => _changed();
    partial void OnDiscountChanged(decimal value) => _changed();
    partial void OnLineTotalPaiseChanged(long value) => OnPropertyChanged(nameof(TotalText));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
}

public sealed partial class BillingViewModel : ObservableObject
{
    private readonly ICatalogService _catalog;
    private readonly IPartyService _parties;
    private readonly ISalesService _sales;
    private readonly IUiHost _ui;
    private Guid _commandId = Guid.NewGuid();
    private bool _recalculating;

    public BillingViewModel(ICatalogService catalog, IPartyService parties, ISalesService sales, ISettingsService settings, IHoldService hold, IUiHost ui)
    {
        _catalog = catalog; _parties = parties; _sales = sales; _ui = ui; _settings = settings; _hold = hold;
        Lines.CollectionChanged += (_, _) => Recalculate();
    }

    private readonly ISettingsService _settings;
    private readonly IHoldService _hold;
    public CompanyDto? Company { get; private set; }
    [ObservableProperty] private int _heldCount;
    public string HeldText => HeldCount == 0 ? "Held bills" : $"Held bills ({HeldCount})";
    partial void OnHeldCountChanged(int value) => OnPropertyChanged(nameof(HeldText));

    public ObservableCollection<CartLine> Lines { get; } = new();
    public ObservableCollection<ProductDto> Matches { get; } = new();
    public ObservableCollection<PartyDto> CustomerMatches { get; } = new();

    [ObservableProperty] private string _scanText = "";
    [ObservableProperty] private string _customerQuery = "";
    [ObservableProperty] private PartyDto? _customer;
    [ObservableProperty] private string _cashText = "";
    [ObservableProperty] private string _upiText = "";
    [ObservableProperty] private string _cardText = "";
    [ObservableProperty] private string _creditText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private long _taxablePaise;
    [ObservableProperty] private long _taxPaise;
    [ObservableProperty] private long _discountPaise;
    [ObservableProperty] private long _roundOffPaise;
    [ObservableProperty] private long _totalPaise;
    [ObservableProperty] private string _paymentNote = "";
    [ObservableProperty] private bool _paymentOk;

    public bool HasOpenBill => Lines.Count > 0;
    public bool HasMatches => Matches.Count > 0;

    // ---------- results while typing ----------
    private int _liveGeneration;
    /// <summary>How long to wait after the last key before searching, so a scanner typing fast does not search on every digit.</summary>
    public int LiveSearchDelayMs { get; set; } = 220;

    partial void OnScanTextChanged(string value)
    {
        var generation = ++_liveGeneration;                         // any search already waiting is now out of date
        if (string.IsNullOrWhiteSpace(value))
        {
            if (Matches.Count > 0) { Matches.Clear(); OnPropertyChanged(nameof(HasMatches)); }
            return;
        }
        _ = LiveSearchAsync(generation, value.Trim(), LiveSearchDelayMs);
    }

    /// <summary>Fills the list of matching items for what has been typed so far (nothing is added to the bill until one is chosen).</summary>
    public async Task LiveSearchAsync(int generation, string text, int delayMs)
    {
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            if (generation != _liveGeneration) return;
            var found = await _catalog.SearchAsync(text, 15);
            if (generation != _liveGeneration) return;
            Matches.Clear();
            foreach (var p in found) Matches.Add(p);
            OnPropertyChanged(nameof(HasMatches));
        }
        catch (BusinessRuleException) { /* the list is a convenience; Enter still searches */ }
    }

    /// <summary>Runs the search for the current text straight away (used when checking the screen).</summary>
    public Task SearchNowAsync() => LiveSearchAsync(++_liveGeneration, ScanText.Trim(), 0);
    public bool HasCustomerMatches => CustomerMatches.Count > 0;
    public bool HasCustomer => Customer != null;
    public string TotalText => Money.Format(TotalPaise);
    public string TaxableText => Money.FormatPlain(TaxablePaise);
    public string TaxText => Money.FormatPlain(TaxPaise);
    public string DiscountText => Money.FormatPlain(DiscountPaise);
    public string RoundOffText => Money.FormatPlain(RoundOffPaise);
    public bool ShowTax => Company is { CollectsTax: true };
    public string ItemCountText => Lines.Count == 0 ? "No items yet" : $"{Lines.Count} item{(Lines.Count == 1 ? "" : "s")}";
    public string CustomerText => Customer == null ? "Walk-in customer" :
        Customer.OutstandingPaise > 0 || Customer.CreditLimitPaise > 0
            ? $"{Customer.Name}  (owes {Money.Format(Math.Max(0, Customer.OutstandingPaise))}" + (Customer.CreditLimitPaise > 0 ? $" of {Money.Format(Customer.CreditLimitPaise)} limit{(Customer.IsOverLimit ? " - OVER LIMIT" : "")}" : "") + ")"
            : Customer.Name;

    public async Task InitializeAsync()
    {
        try { Company = await _settings.GetCompanyAsync(); OnPropertyChanged(nameof(ShowTax)); Recalculate(); await RefreshHeldAsync(); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    // ---------- adding items ----------
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
            else if (found.Count == 0) SetStatus("No item found for \"" + text + "\".", true);
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
        else Lines.Add(new CartLine(product, Recalculate) { Quantity = quantity });
        SetStatus(isPack ? $"Added {quantity:0.###} {product.Unit} of {product.Name} (pack barcode)" : $"Added {product.Name}", false);
    }

    [RelayCommand]
    private void RemoveLine(CartLine? line)
    {
        if (line != null) Lines.Remove(line);
    }

    [RelayCommand]
    private void ClearCart()
    {
        if (Lines.Count > 0 && !_ui.Confirm("Clear this bill?")) return;
        ResetBill();
    }

    // ---------- hold and recall ----------
    private async Task RefreshHeldAsync()
    {
        try { HeldCount = (await _hold.ListAsync()).Count; } catch (Exception ex) when (ex is BusinessRuleException) { HeldCount = 0; }
    }

    private async Task HoldCoreAsync()
    {
        var first = Lines[0].Name + (Lines.Count > 1 ? $" +{Lines.Count - 1} more" : "");
        var label = Customer != null ? $"{Customer.Name}: {first}" : first;
        await _hold.HoldAsync(new HoldBillCommand(label, Customer?.Id,
            Lines.Select(l => new HeldLine(l.Product.Id, Qty.ToMilli(l.Quantity), Money.ToPaise(l.Discount))).ToList()));
    }

    [RelayCommand]
    private async Task HoldAsync()
    {
        if (Lines.Count == 0) { SetStatus("There is nothing on the bill to hold.", true); return; }
        try
        {
            await HoldCoreAsync();
            ResetBill();
            await RefreshHeldAsync();
            SetStatus("The bill is on hold. Bring it back with Held bills.", false);
        }
        catch (BusinessRuleException ex) { SetStatus(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand]
    private async Task RecallAsync()
    {
        try
        {
            var held = await _hold.ListAsync();
            HeldCount = held.Count;
            if (held.Count == 0) { SetStatus("No bills are on hold.", false); return; }
            var id = await _ui.PickHeldBillAsync(held, _hold.DiscardAsync);
            await RefreshHeldAsync();
            if (id == null) return;
            if (Lines.Count > 0)
            {
                if (!_ui.Confirm("The bill on the screen will be put on hold first. Continue?", "Bring back a held bill")) return;
                await HoldCoreAsync();
            }
            var bill = await _hold.RecallAsync(id.Value);
            if (bill == null) { SetStatus("That bill was already taken by someone else.", true); await RefreshHeldAsync(); return; }
            ResetBill();
            var skipped = 0;
            foreach (var l in bill.Lines)
            {
                var p = await _catalog.GetAsync(l.ProductId);
                if (p == null || !p.IsActive) { skipped++; continue; }
                Lines.Add(new CartLine(p, Recalculate) { Quantity = l.QtyMilli / 1000m, Discount = Money.ToRupees(l.DiscountPaise) });
            }
            if (bill.PartyId is { } pid)
                Customer = (await _parties.SearchAsync(bill.CustomerName ?? "", 20)).FirstOrDefault(c => c.Id == pid);
            await RefreshHeldAsync();
            SetStatus($"Brought back: {bill.Label}." + (skipped > 0 ? $" {skipped} item(s) are no longer for sale and were left out." : ""), skipped > 0);
        }
        catch (BusinessRuleException ex) { SetStatus(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private void ResetBill()
    {
        Lines.Clear();
        Customer = null; CustomerQuery = ""; CustomerMatches.Clear();
        CashText = UpiText = CardText = CreditText = "";
        Matches.Clear();
        _commandId = Guid.NewGuid();
        Recalculate();
        OnPropertyChanged(nameof(HasMatches)); OnPropertyChanged(nameof(HasCustomerMatches));
    }

    // ---------- customer ----------
    [RelayCommand]
    private async Task SearchCustomerAsync()
    {
        var q = CustomerQuery.Trim();
        CustomerMatches.Clear();
        if (q.Length == 0) { OnPropertyChanged(nameof(HasCustomerMatches)); return; }
        try
        {
            foreach (var p in await _parties.SearchAsync(q, 8)) CustomerMatches.Add(p);
            OnPropertyChanged(nameof(HasCustomerMatches));
            if (CustomerMatches.Count == 0) SetStatus("No customer found. Add customers in the Customers screen.", true);
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand]
    private void PickCustomer(PartyDto? p)
    {
        if (p == null) return;
        Customer = p; CustomerQuery = ""; CustomerMatches.Clear();
        OnPropertyChanged(nameof(HasCustomerMatches));
    }

    [RelayCommand]
    private void ClearCustomer() { Customer = null; CreditText = ""; }

    partial void OnCustomerChanged(PartyDto? value)
    {
        OnPropertyChanged(nameof(HasCustomer)); OnPropertyChanged(nameof(CustomerText));
        Recalculate();
    }

    partial void OnCashTextChanged(string value) => RefreshPayment();
    partial void OnUpiTextChanged(string value) => RefreshPayment();
    partial void OnCardTextChanged(string value) => RefreshPayment();
    partial void OnCreditTextChanged(string value) => RefreshPayment();

    // ---------- calculation (same domain code the service uses, so the screen and the saved bill always agree) ----------
    private void Recalculate()
    {
        if (_recalculating) return;
        _recalculating = true;
        try
        {
            var interState = Customer?.StateCode is { Length: > 0 } sc && Company != null && sc != Company.StateCode;
            long taxable = 0, tax = 0, disc = 0, exact = 0;
            foreach (var l in Lines)
            {
                try
                {
                    var rate = Company is { CollectsTax: false } ? 0 : l.Product.GstRateBp;
                    var t = TaxCalculator.Calculate(l.Product.SellingPricePaise, Qty.ToMilli(l.Quantity), Money.ToPaise(l.Discount),
                        rate, interState, l.Product.PriceInclusive);
                    l.Error = null; l.LineTotalPaise = t.TotalPaise;
                    taxable += t.TaxablePaise; tax += t.TaxPaise; disc += t.DiscountPaise; exact += t.TotalPaise;
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    l.Error = ex.ParamName == "discountPaise" ? "Discount is more than the amount" : "Quantity must be more than zero";
                    l.LineTotalPaise = 0;
                }
            }
            var (total, round) = InvoiceRounding.ToNearestRupee(exact);
            TaxablePaise = taxable; TaxPaise = tax; DiscountPaise = disc; RoundOffPaise = round; TotalPaise = total;
            OnPropertyChanged(nameof(TotalText)); OnPropertyChanged(nameof(TaxableText)); OnPropertyChanged(nameof(TaxText));
            OnPropertyChanged(nameof(DiscountText)); OnPropertyChanged(nameof(RoundOffText)); OnPropertyChanged(nameof(ItemCountText));
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

    private (bool ok, long cashApplied, long upi, long card, long credit, long tendered) ParsePayments(out string note)
    {
        note = "";
        if (!TryMoney(UpiText, out var upi) || !TryMoney(CardText, out var card) || !TryMoney(CreditText, out var credit) || !TryMoney(CashText, out var cashIn))
        { note = "Enter payment amounts as numbers."; return (false, 0, 0, 0, 0, 0); }
        var others = upi + card + credit;
        if (others > TotalPaise) { note = "UPI, card and credit add up to more than the bill."; return (false, 0, 0, 0, 0, 0); }
        var cashApplied = TotalPaise - others;
        var tendered = string.IsNullOrWhiteSpace(CashText) ? cashApplied : cashIn;
        if (cashApplied > 0 && tendered < cashApplied) { note = $"Cash received is short by {Money.Format(cashApplied - tendered)}."; return (false, cashApplied, upi, card, credit, tendered); }
        if (credit > 0 && Customer == null) { note = "Choose a customer to put the bill on credit."; return (false, cashApplied, upi, card, credit, tendered); }
        note = tendered > cashApplied && cashApplied > 0 ? $"Return change {Money.Format(tendered - cashApplied)}"
             : cashApplied > 0 ? $"Collect {Money.Format(cashApplied)} in cash" : "Fully paid by UPI / card / credit";
        return (true, cashApplied, upi, card, credit, tendered);
    }

    private void RefreshPayment()
    {
        if (_recalculating) return;
        var (ok, _, _, _, _, _) = ParsePayments(out var note);
        PaymentNote = Lines.Count == 0 ? "" : note;
        PaymentOk = ok && Lines.Count > 0 && Lines.All(l => !l.HasError);
        CompleteCommand.NotifyCanExecuteChanged();
    }

    // ---------- completing the sale ----------
    private bool CanComplete() => !IsBusy && PaymentOk && Lines.Count > 0;
    partial void OnIsBusyChanged(bool value) => CompleteCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanComplete))]
    private async Task CompleteAsync()
    {
        var (ok, cash, upi, card, credit, _) = ParsePayments(out var note);
        if (!ok) { SetStatus(note, true); return; }
        var payments = new List<PaymentInput>();
        if (cash > 0) payments.Add(new(PaymentMode.Cash, cash));
        if (upi > 0) payments.Add(new(PaymentMode.Upi, upi));
        if (card > 0) payments.Add(new(PaymentMode.Card, card));
        if (credit > 0) payments.Add(new(PaymentMode.Credit, credit));

        var cmd = new CompleteSaleCommand(_commandId,
            Lines.Select(l => new SaleLineInput(l.Product.Id, Qty.ToMilli(l.Quantity), Money.ToPaise(l.Discount))).ToList(),
            payments, Customer?.Id, Customer?.Name, Customer?.Gstin, Customer?.StateCode);

        IsBusy = true;
        try
        {
            long? approver = null;
            SaleResult? result = null;
            while (result == null)
            {
                try { result = await _sales.CompleteSaleAsync(cmd with { ApprovedByUserId = approver }); }
                catch (ApprovalRequiredException ex)
                {
                    approver = await _ui.RequestApprovalAsync(ex.Message);
                    if (approver == null) { SetStatus("Not approved. The bill has not been saved.", true); return; }
                }
            }

            var detail = await _sales.GetAsync(result.SaleId);
            SetStatus($"Bill {result.InvoiceNumber} saved.", false);
            ResetBill();
            if (detail != null) _ui.ShowReceipt(detail, Company, false);
        }
        catch (BusinessRuleException ex) { SetStatus(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); SetStatus("The bill was not saved. You can try again.", true); }
        finally { IsBusy = false; }
    }

    private void SetStatus(string message, bool error) { StatusMessage = message; StatusIsError = error; }
}
