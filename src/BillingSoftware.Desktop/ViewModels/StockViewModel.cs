using System.Collections.ObjectModel;
using System.Globalization;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed class StockRow(ProductDto p)
{
    public ProductDto Product { get; } = p;
    public string Name => p.Name;
    public string Sku => p.Sku;
    public string QtyText => Qty.Format(p.QtyOnHandMilli);
    public string Unit => p.Unit;
    public string AvgCostText => Money.FormatPlain(p.AvgCostPaise);
    public string ValueText => Money.FormatPlain(Costing.Value(Math.Max(0, p.QtyOnHandMilli), p.AvgCostPaise));
    public string StatusText => !p.IsActive ? "Inactive" : p.QtyOnHandMilli <= 0 ? "Out of stock" : "";
    public bool IsLow => p.QtyOnHandMilli <= 0;
}

/// <summary>
/// The Stock tab: how much of each item is on hand, what it cost, and what it is worth; with adding stock,
/// adjusting for loss or damage, and repacking into smaller packs for the chosen item.
/// </summary>
public sealed partial class StockViewModel : ObservableObject
{
    private readonly ICatalogService _catalog;
    private readonly IStockService _stock;
    private readonly IRepackService _repack;
    private readonly ISettingsService _settings;
    private readonly IUiHost _ui;
    private long _editingId;
    private int _previewGeneration;
    private int _loadGeneration;
    private Guid _repackCommandId = Guid.NewGuid();

    public StockViewModel(ICatalogService catalog, IStockService stock, IRepackService repack, ISettingsService settings, IUiHost ui)
    {
        _catalog = catalog; _stock = stock; _repack = repack; _settings = settings; _ui = ui;
    }

    public ObservableCollection<StockRow> Items { get; } = new();
    public ObservableCollection<ProductDto> RepackTargets { get; } = new();

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _onlyOut;
    [ObservableProperty] private StockRow? _selected;
    [ObservableProperty] private bool _isPanelOpen;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _panelTitle = "";
    [ObservableProperty] private string _stockInfo = "";
    [ObservableProperty] private string _openingQty = "";
    [ObservableProperty] private string _openingCost = "";
    [ObservableProperty] private string _adjustQty = "";
    [ObservableProperty] private string _adjustReason = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private ProductDto? _repackTarget;
    [ObservableProperty] private string _repackSourceQtyText = "";
    [ObservableProperty] private string _repackTargetQtyText = "";
    [ObservableProperty] private string _repackNote = "";
    [ObservableProperty] private string _repackPreviewText = "";
    [ObservableProperty] private bool _repackPreviewIsError;

    /// <summary>Results follow the typing, a moment after the last key.</summary>
    public int LiveSearchDelayMs { get; set; } = 250;

    public bool IsExisting => _editingId != 0;

    public async Task LoadAsync()
    {
        try
        {
            var generation = ++_loadGeneration;
            var rows = string.IsNullOrWhiteSpace(Query) ? await _catalog.ListAsync(0, 2000, true) : await _catalog.SearchAsync(Query, 200);
            if (generation != _loadGeneration) return;
            var shown = rows.Where(p => !OnlyOut || p.QtyOnHandMilli <= 0).ToList();
            Items.Clear();
            foreach (var p in shown) Items.Add(new StockRow(p));
            var worth = rows.Where(p => p.QtyOnHandMilli > 0).Sum(p => Costing.Value(p.QtyOnHandMilli, p.AvgCostPaise));
            var out0 = rows.Count(p => p.IsActive && p.QtyOnHandMilli <= 0);
            Summary = $"{shown.Count} item{(shown.Count == 1 ? "" : "s")}   |   Stock worth {Money.Format(worth)} at cost   |   {out0} out of stock";
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    partial void OnQueryChanged(string value) => _ = LiveSearchAsync();
    partial void OnOnlyOutChanged(bool value) => _ = LoadAsync();

    private async Task LiveSearchAsync()
    {
        var typed = Query;
        if (LiveSearchDelayMs > 0) await Task.Delay(LiveSearchDelayMs);
        if (typed != Query) return;
        await LoadAsync();
    }

    [RelayCommand]
    private void ClosePanel()
    {
        IsPanelOpen = false;
        Selected = null;
        _editingId = 0;
        OnPropertyChanged(nameof(IsExisting));
    }

    partial void OnSelectedChanged(StockRow? value)
    {
        if (value == null) return;
        IsPanelOpen = true;
        var p = value.Product;
        _editingId = p.Id;
        PanelTitle = p.Name;
        StockInfo = $"In stock: {Qty.Format(p.QtyOnHandMilli)} {p.Unit}   |   Average cost: {Money.Format(p.AvgCostPaise)}";
        OpeningQty = OpeningCost = AdjustQty = AdjustReason = "";
        OnPropertyChanged(nameof(IsExisting));
        SetStatus("", false);
        ResetRepack();
    }

    private static bool TryDecimal(string? text, out decimal value) =>
        decimal.TryParse((text ?? "").Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out value);

    [RelayCommand]
    private async Task ReceiveStockAsync()
    {
        if (_editingId == 0) { SetStatus("Choose an item first.", true); return; }
        if (!TryDecimal(OpeningQty, out var q) || q <= 0) { SetStatus("Enter the quantity received.", true); return; }
        if (!TryDecimal(OpeningCost, out var c) || c < 0) { SetStatus("Enter the cost per unit.", true); return; }
        try
        {
            await _stock.ReceiveOpeningStockAsync(_editingId, Qty.ToMilli(q), Money.ToPaise(c));
            await RefreshSelectedAsync();
            SetStatus("Stock added.", false);
        }
        catch (BusinessRuleException ex) { SetStatus(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand]
    private async Task AdjustAsync()
    {
        if (_editingId == 0) { SetStatus("Choose an item first.", true); return; }
        if (!TryDecimal(AdjustQty, out var q) || q == 0) { SetStatus("Enter a quantity: negative for loss or damage (for example -2), positive for a gain.", true); return; }
        try
        {
            await _stock.AdjustAsync(_editingId, Qty.ToMilli(q), AdjustReason);
            await RefreshSelectedAsync();
            SetStatus("Stock adjusted.", false);
        }
        catch (BusinessRuleException ex) { SetStatus(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private async Task RefreshSelectedAsync()
    {
        var id = _editingId;
        await LoadAsync();
        Selected = Items.FirstOrDefault(i => i.Product.Id == id);
    }

    // ---------- repacking ----------
    private void ResetRepack()
    {
        RepackTargets.Clear();
        foreach (var r in Items.Where(i => i.Product.Id != _editingId && i.Product.IsActive)) RepackTargets.Add(r.Product);
        RepackTarget = null; RepackSourceQtyText = RepackTargetQtyText = RepackNote = RepackPreviewText = "";
        RepackPreviewIsError = false;
        _repackCommandId = Guid.NewGuid();
    }

    partial void OnRepackTargetChanged(ProductDto? value) => _ = RefreshRepackPreviewAsync();
    partial void OnRepackSourceQtyTextChanged(string value) => _ = RefreshRepackPreviewAsync();
    partial void OnRepackTargetQtyTextChanged(string value) => _ = RefreshRepackPreviewAsync();

    private RepackCommand? BuildRepack()
    {
        if (_editingId == 0 || RepackTarget == null) return null;
        if (!TryDecimal(RepackSourceQtyText, out var s) || s <= 0 || !TryDecimal(RepackTargetQtyText, out var t) || t <= 0) return null;
        return new RepackCommand(_repackCommandId, _editingId, Qty.ToMilli(s), RepackTarget.Id, Qty.ToMilli(t), string.IsNullOrWhiteSpace(RepackNote) ? null : RepackNote);
    }

    private async Task RefreshRepackPreviewAsync()
    {
        var generation = ++_previewGeneration;
        var cmd = BuildRepack();
        if (cmd == null) { RepackPreviewText = ""; RepackPreviewIsError = false; return; }
        try
        {
            var p = await _repack.PreviewAsync(cmd);
            if (generation != _previewGeneration) return;
            RepackPreviewText = $"Uses stock worth {Money.Format(p.SourceValuePaise)}. Each {RepackTarget!.Unit} of {p.TargetName} will cost {Money.Format(p.TargetUnitCostPaise)}.";
            RepackPreviewIsError = false;
        }
        catch (BusinessRuleException ex) { if (generation == _previewGeneration) { RepackPreviewText = ex.Message; RepackPreviewIsError = true; } }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand]
    private async Task RepackNowAsync()
    {
        if (_editingId == 0) { SetStatus("Choose the bulk item first.", true); return; }
        var cmd = BuildRepack();
        if (cmd == null) { SetStatus("Choose the pack item and enter how much stock is used and how many packs are made.", true); return; }
        try
        {
            var r = await _repack.RepackAsync(cmd);
            await RefreshSelectedAsync();
            SetStatus($"Repacked. Each pack now costs {Money.Format(r.TargetUnitCostPaise)}.", false);
        }
        catch (BusinessRuleException ex) { SetStatus(ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private void SetStatus(string m, bool error) { Status = m; StatusIsError = error; }
}
