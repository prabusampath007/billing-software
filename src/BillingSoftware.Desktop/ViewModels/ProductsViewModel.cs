using System.Collections.ObjectModel;
using System.Globalization;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed class ProductRow(ProductDto p)
{
    public ProductDto Product { get; } = p;
    public string Name => p.Name;
    public string TamilName => p.TamilName ?? "";
    public string Sku => p.Sku;
    public string Barcode => p.Barcode ?? "";
    public string Hsn => p.HsnCode ?? "";
    public string PriceText => Money.FormatPlain(p.SellingPricePaise);
    public string GstText => $"{p.GstRateBp / 100m:0.##}%";
    public string StockText => $"{Qty.Format(p.QtyOnHandMilli)} {p.Unit}";
    public string StatusText => p.IsActive ? "Active" : "Inactive";
    public bool IsLow => p.QtyOnHandMilli <= 0;
}

public sealed class BarcodeRow(ProductBarcodeDto b, string unit)
{
    public ProductBarcodeDto Barcode { get; } = b;
    public string Text => $"{b.Code}   × {Qty.Format(b.PackQtyMilli)} {unit}" + (string.IsNullOrWhiteSpace(b.Note) ? "" : $"   ({b.Note})");
}

/// <summary>
/// The Items tab: the list of items, and the New / Edit item dialog (details, price and tax, barcodes, labels).
/// Stock (adding stock, adjusting, repacking) is on the Stock tab.
/// </summary>
public sealed partial class ProductsViewModel : ObservableObject
{
    private readonly ICatalogService _catalog;
    private readonly IBarcodeService _barcodes;
    private readonly ISettingsService _settings;
    private readonly IUiHost _ui;
    private long _editingId;
    private int _searchGeneration;

    public ProductsViewModel(ICatalogService catalog, IBarcodeService barcodes, ISettingsService settings, IUiHost ui)
    {
        _catalog = catalog; _barcodes = barcodes; _settings = settings; _ui = ui;
    }

    public ObservableCollection<ProductRow> Items { get; } = new();
    public ObservableCollection<BarcodeRow> Extras { get; } = new();
    public IReadOnlyList<string> Units { get; } = new[] { "pcs", "kg", "g", "ltr", "ml", "box", "pack", "dozen", "mtr", "pair" };

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private ProductRow? _selected;
    [ObservableProperty] private string _sku = "";
    [ObservableProperty] private string _barcode = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _tamilName = "";
    [ObservableProperty] private string _hsn = "";
    [ObservableProperty] private string _gstText = "18";
    [ObservableProperty] private string _unit = "pcs";
    [ObservableProperty] private string _priceText = "";
    [ObservableProperty] private string _mrpText = "";
    [ObservableProperty] private bool _priceInclusive = true;
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private string _stockInfo = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private string _listSummary = "";
    // extra barcodes and labels
    [ObservableProperty] private BarcodeRow? _selectedExtra;
    [ObservableProperty] private string _extraCode = "";
    [ObservableProperty] private string _extraQtyText = "";
    [ObservableProperty] private string _extraNote = "";
    [ObservableProperty] private string _labelCopiesText = "1";

    /// <summary>The GST rate, HSN code and "price includes GST" are hidden while GST is turned off (the values are kept for if it is turned on again).</summary>
    [ObservableProperty] private bool _showGst = true;
    /// <summary>The New / Edit item dialog is open. The screen opens the dialog when this turns on and closes it when it turns off.</summary>
    [ObservableProperty] private bool _isFormOpen;

    /// <summary>Raised after an item was saved, so the dialog can close.</summary>
    public event Action? Saved;

    [RelayCommand]
    private void CloseForm()
    {
        IsFormOpen = false;
        Selected = null;
        _editingId = 0;
        if (StatusIsError) SetStatus("", false);
        OnPropertyChanged(nameof(FormTitle)); OnPropertyChanged(nameof(IsExisting));
    }

    public string FormTitle => _editingId == 0 ? "New item" : "Edit item";
    public bool IsExisting => _editingId != 0;

    public async Task LoadAsync()
    {
        try
        {
            ShowGst = (await _settings.GetCompanyAsync())?.GstEnabled ?? true;
            var generation = ++_searchGeneration;
            var rows = string.IsNullOrWhiteSpace(Query) ? await _catalog.ListAsync(0, 2000, true) : await _catalog.SearchAsync(Query, 200);
            if (generation != _searchGeneration) return;                 // a newer search has started
            Items.Clear();
            foreach (var p in rows) Items.Add(new ProductRow(p));
            ListSummary = string.IsNullOrWhiteSpace(Query)
                ? $"{Items.Count} item{(Items.Count == 1 ? "" : "s")}"
                : $"{Items.Count} item{(Items.Count == 1 ? "" : "s")} found";
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand] private Task SearchAsync() => LoadAsync();

    /// <summary>Results follow the typing, a moment after the last key.</summary>
    public int LiveSearchDelayMs { get; set; } = 250;

    partial void OnQueryChanged(string value) => _ = LiveSearchAsync();

    private async Task LiveSearchAsync()
    {
        var typed = Query;
        if (LiveSearchDelayMs > 0) await Task.Delay(LiveSearchDelayMs);
        if (typed != Query) return;                                      // typing continued
        await LoadAsync();
    }

    partial void OnSelectedChanged(ProductRow? value)
    {
        if (value == null) return;
        IsFormOpen = true;
        var p = value.Product;
        _editingId = p.Id;
        Sku = p.Sku; Barcode = p.Barcode ?? ""; Name = p.Name; TamilName = p.TamilName ?? ""; Hsn = p.HsnCode ?? "";
        GstText = (p.GstRateBp / 100m).ToString("0.##", CultureInfo.CurrentCulture); Unit = p.Unit;
        PriceText = Money.ToRupees(p.SellingPricePaise).ToString("0.00", CultureInfo.CurrentCulture);
        MrpText = p.MrpPaise.HasValue ? Money.ToRupees(p.MrpPaise.Value).ToString("0.00", CultureInfo.CurrentCulture) : "";
        PriceInclusive = p.PriceInclusive; IsActive = p.IsActive;
        StockInfo = $"In stock: {Qty.Format(p.QtyOnHandMilli)} {p.Unit}   |   Average cost: {Money.Format(p.AvgCostPaise)}";
        OnPropertyChanged(nameof(FormTitle)); OnPropertyChanged(nameof(IsExisting));
        SetStatus("", false);
        SelectedExtra = null;
        _ = LoadExtrasAsync(p.Id);
    }

    [RelayCommand]
    private void New()
    {
        _editingId = 0;
        IsFormOpen = true;
        Selected = null;
        Sku = Barcode = Name = TamilName = Hsn = PriceText = MrpText = StockInfo = "";
        GstText = "18"; Unit = "pcs"; PriceInclusive = true; IsActive = true;
        OnPropertyChanged(nameof(FormTitle)); OnPropertyChanged(nameof(IsExisting));
        SetStatus("", false);
        Extras.Clear();
    }

    private static bool TryDecimal(string? text, out decimal value) =>
        decimal.TryParse((text ?? "").Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out value);

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!TryDecimal(PriceText, out var price) || price < 0) { SetStatus("Enter the selling price as a number.", true); return; }
        if (!TryDecimal(GstText, out var gst) || gst < 0 || gst > 50) { SetStatus("GST rate must be a number between 0 and 50.", true); return; }
        long? mrp = null;
        if (!string.IsNullOrWhiteSpace(MrpText))
        {
            if (!TryDecimal(MrpText, out var m) || m < 0) { SetStatus("MRP must be a number.", true); return; }
            mrp = Money.ToPaise(m);
        }
        try
        {
            var existing = _editingId == 0 ? null : Selected?.Product;
            var dto = new ProductDto(_editingId, Sku, string.IsNullOrWhiteSpace(Barcode) ? null : Barcode, Name,
                string.IsNullOrWhiteSpace(Hsn) ? null : Hsn, (int)Math.Round(gst * 100), Unit, Money.ToPaise(price), mrp,
                PriceInclusive, existing?.AvgCostPaise ?? 0, existing?.QtyOnHandMilli ?? 0, IsActive,
                string.IsNullOrWhiteSpace(TamilName) ? null : TamilName);
            var saved = await _catalog.SaveAsync(dto);
            await LoadAsync();
            Selected = Items.FirstOrDefault(i => i.Product.Id == saved.Id);
            SetStatus($"Saved {saved.Name}.", false);
            Saved?.Invoke();
        }
        catch (BusinessRuleException ex) { SetStatus(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    // ---------- barcodes ----------
    private async Task LoadExtrasAsync(long productId)
    {
        try
        {
            var list = await _barcodes.ListAsync(productId);
            if (productId != _editingId) return;                       // another item was chosen meanwhile
            var unit = Selected?.Product.Unit ?? Unit;
            Extras.Clear();
            foreach (var b in list) Extras.Add(new BarcodeRow(b, unit));
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand]
    private async Task GenerateBarcodeAsync()
    {
        try { Barcode = await _barcodes.GenerateAsync(); SetStatus("A new barcode was made. Save the item to keep it, then print its label.", false); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand]
    private async Task GenerateExtraCodeAsync()
    {
        try { ExtraCode = await _barcodes.GenerateAsync(); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand]
    private async Task AddExtraAsync()
    {
        if (_editingId == 0) { SetStatus("Save the item first, then add its extra barcodes.", true); return; }
        if (!TryDecimal(ExtraQtyText, out var q) || q <= 0) { SetStatus($"Enter how much of the item one scan adds (for example 0.25 for a quarter {Unit}, or 12 for a carton of 12).", true); return; }
        try
        {
            await _barcodes.AddAsync(_editingId, ExtraCode, Qty.ToMilli(q), string.IsNullOrWhiteSpace(ExtraNote) ? null : ExtraNote);
            ExtraCode = ExtraQtyText = ExtraNote = "";
            await LoadExtrasAsync(_editingId);
            SetStatus("Extra barcode added.", false);
        }
        catch (BusinessRuleException ex) { SetStatus(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand]
    private async Task RemoveExtraAsync()
    {
        if (SelectedExtra == null) { SetStatus("Select an extra barcode in the list to remove it.", true); return; }
        try
        {
            await _barcodes.RemoveAsync(SelectedExtra.Barcode.Id);
            await LoadExtrasAsync(_editingId);
            SetStatus("Extra barcode removed.", false);
        }
        catch (BusinessRuleException ex) { SetStatus(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    /// <summary>Prints labels for the selected extra barcode, or for the item's own barcode when none is selected.</summary>
    [RelayCommand]
    private async Task PrintLabelsAsync()
    {
        var item = Selected?.Product;
        if (item == null) { SetStatus("Choose an item first.", true); return; }
        if (!int.TryParse(LabelCopiesText.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var copies) || copies < 1 || copies > 500)
        { SetStatus("Enter how many labels to print (1 to 500).", true); return; }

        string code, name; long price;
        if (SelectedExtra != null)
        {
            var b = SelectedExtra.Barcode;
            code = b.Code;
            name = string.IsNullOrWhiteSpace(b.Note) ? $"{item.Name} {Qty.Format(b.PackQtyMilli)} {item.Unit}" : $"{item.Name} {b.Note}";
            price = Costing.Value(b.PackQtyMilli, item.SellingPricePaise);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(item.Barcode)) { SetStatus("This item has no barcode yet. Use \"Make a barcode\", save, then print.", true); return; }
            code = item.Barcode; name = item.Name; price = item.SellingPricePaise;
        }
        if (!Ean13.IsValid(code)) { SetStatus("Labels can be printed only for 13-digit EAN barcodes. Use \"Make a barcode\" to make one for this item.", true); return; }

        try
        {
            var shop = (await _settings.GetCompanyAsync())?.Name ?? "";
            var priceText = Money.Format(price) + (item.PriceInclusive ? "" : " + GST");
            var specs = Enumerable.Range(0, copies).Select(_ => new LabelSpec(shop, name, priceText, code)).ToList();
            if (PrintConfig.LabelMode == "sheet")
            {
                _ui.ShowDocument("Barcode labels", () => LabelBuilder.Build(specs));
                SetStatus($"{copies} label{(copies == 1 ? "" : "s")} ready ({Math.Ceiling(copies / (double)LabelBuilder.LabelsPerSheet)} A4 sheet(s)).", false);
            }
            else
            {
                int w = PrintConfig.LabelWidthMm, h = PrintConfig.LabelHeightMm;
                _ui.ShowDocument("Barcode labels", () => LabelBuilder.BuildRoll(specs, w, h));
                SetStatus($"{copies} label{(copies == 1 ? "" : "s")} ready, one per {w} x {h} mm label.", false);
            }
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private void SetStatus(string m, bool error) { Status = m; StatusIsError = error; }
}
