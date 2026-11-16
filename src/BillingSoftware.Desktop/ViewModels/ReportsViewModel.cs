using System.Collections.ObjectModel;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed record KeyValueRow(string Label, string Value);
public sealed record TrialRow(string Code, string Name, string Debit, string Credit);
public sealed record StockLine(string Sku, string Name, string Qty, string Cost, string Value);

public sealed partial class ReportsViewModel : ObservableObject
{
    private readonly IReportService _reports;
    private readonly IUiHost _ui;

    public ReportsViewModel(IReportService reports, IUiHost ui) { _reports = reports; _ui = ui; }

    public ObservableCollection<KeyValueRow> Figures { get; } = new();
    public ObservableCollection<KeyValueRow> ByMode { get; } = new();
    public ObservableCollection<KeyValueRow> ByCashier { get; } = new();
    public ObservableCollection<TrialRow> Trial { get; } = new();
    public ObservableCollection<StockLine> Stock { get; } = new();

    [ObservableProperty] private DateTime _date = DateTime.Today;
    [ObservableProperty] private string _trialTotals = "";
    [ObservableProperty] private string _trialCheck = "";
    [ObservableProperty] private bool _trialOk = true;
    [ObservableProperty] private string _stockTotal = "";

    partial void OnDateChanged(DateTime value) => _ = LoadAsync();

    public async Task LoadAsync()
    {
        try
        {
            var day = DateOnly.FromDateTime(Date);
            var s = await _reports.DailySummaryAsync(day);
            Figures.Clear();
            Figures.Add(new("Bills", s.BillCount.ToString()));
            Figures.Add(new("Total sales", Money.Format(s.TotalPaise)));
            Figures.Add(new("Returns (credit notes)", $"{Money.Format(s.ReturnTotalPaise)}  ({s.ReturnCount})"));
            Figures.Add(new("Net sales after returns", Money.Format(s.TotalPaise - s.ReturnTotalPaise)));
            Figures.Add(new("Purchases", $"{Money.Format(s.PurchaseTotalPaise)}  ({s.PurchaseCount})"));
            Figures.Add(new("Taxable value", Money.Format(s.TaxablePaise)));
            Figures.Add(new("CGST", Money.Format(s.CgstPaise)));
            Figures.Add(new("SGST", Money.Format(s.SgstPaise)));
            Figures.Add(new("IGST", Money.Format(s.IgstPaise)));
            Figures.Add(new("Discount given", Money.Format(s.DiscountPaise)));
            Figures.Add(new("Round off", Money.Format(s.RoundOffPaise)));
            Figures.Add(new("Voided bills", s.VoidedCount.ToString()));
            ByMode.Clear();
            foreach (var (mode, amount) in s.ByPaymentMode)
                ByMode.Add(new(mode switch { PaymentMode.Cash => "Cash", PaymentMode.Upi => "UPI", PaymentMode.Card => "Card", _ => "On credit" }, Money.Format(amount)));
            ByCashier.Clear();
            foreach (var (cashier, bills, total) in s.ByCashier) ByCashier.Add(new($"{cashier} ({bills} bill{(bills == 1 ? "" : "s")})", Money.Format(total)));

            var tb = await _reports.TrialBalanceAsync(day);
            Trial.Clear();
            foreach (var r in tb.Rows)
                Trial.Add(new(r.Code, r.Name, r.DebitPaise == 0 ? "" : Money.FormatPlain(r.DebitPaise), r.CreditPaise == 0 ? "" : Money.FormatPlain(r.CreditPaise)));
            TrialTotals = $"Total debit {Money.Format(tb.TotalDebitPaise)}    Total credit {Money.Format(tb.TotalCreditPaise)}";
            TrialOk = tb.IsBalanced;
            TrialCheck = tb.IsBalanced ? "Books are balanced." : "WARNING: debit and credit totals differ. Do not continue; contact support.";

            var stock = await _reports.StockValuationAsync();
            Stock.Clear();
            foreach (var r in stock) Stock.Add(new(r.Sku, r.Name, $"{Qty.Format(r.QtyMilli)} {r.Unit}", Money.FormatPlain(r.AvgCostPaise), Money.FormatPlain(r.ValuePaise)));
            StockTotal = $"Stock value at cost: {Money.Format(stock.Sum(r => r.ValuePaise))}";
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand] private Task RefreshAsync() => LoadAsync();
}
