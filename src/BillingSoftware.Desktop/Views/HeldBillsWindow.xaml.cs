using System.Collections.ObjectModel;
using System.Windows;
using BillingSoftware.Application;

namespace BillingSoftware.Desktop.Views;

public sealed class HeldRow(HeldBillSummary s)
{
    public long Id { get; } = s.Id;
    public string Text => $"{s.Label}   -   {s.ItemCount} item{(s.ItemCount == 1 ? "" : "s")}"
                          + (string.IsNullOrWhiteSpace(s.CustomerName) ? "" : $"   -   {s.CustomerName}")
                          + $"   -   {s.UserName}, {Ago(s.CreatedAt)}";

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalMinutes < 1 ? "just now" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min ago" : d.TotalHours < 24 ? $"{(int)d.TotalHours} h ago" : $"{(int)d.TotalDays} d ago";
    }
}

public partial class HeldBillsWindow : Window
{
    private readonly ObservableCollection<HeldRow> _rows;
    private readonly Func<long, Task> _discard;
    public long? ChosenId { get; private set; }

    public HeldBillsWindow(IReadOnlyList<HeldBillSummary> items, Func<long, Task> discard)
    {
        InitializeComponent();
        _discard = discard;
        _rows = new ObservableCollection<HeldRow>(items.Select(i => new HeldRow(i)));
        List.ItemsSource = _rows;
        if (_rows.Count > 0) List.SelectedIndex = 0;
        Loaded += (_, _) => List.Focus();
    }

    private void Recall_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not HeldRow row) return;
        ChosenId = row.Id;
        DialogResult = true;
    }

    private void List_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Recall_Click(sender, e);

    private async void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not HeldRow row) return;
        if (MessageBox.Show(this, "Discard this held bill? It cannot be brought back.", "Discard", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await _discard(row.Id); _rows.Remove(row); }
        catch (Exception ex) { Services.Ui.ShowError(this, ex); }
        if (_rows.Count == 0) DialogResult = false;
    }
}
