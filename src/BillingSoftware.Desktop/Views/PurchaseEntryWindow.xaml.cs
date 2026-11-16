using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Desktop.ViewModels;

namespace BillingSoftware.Desktop.Views;

public partial class PurchaseEntryWindow : Window
{
    private readonly PurchaseEntryViewModel _vm;

    /// <summary>Lets automated checks close the window without the "discard this purchase?" question.</summary>
    public bool SuppressCloseConfirmation { get; set; }

    public PurchaseEntryWindow(PurchaseEntryViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _vm.RequestClose += ok => DialogResult = ok;
        Loaded += async (_, _) => { await _vm.InitializeAsync(); ScanBox.Focus(); ApplyLayout(); };
        Closing += (_, e) =>
        {
            if (!SuppressCloseConfirmation && DialogResult != true && _vm.Lines.Count > 0 && !_vm.IsBusy
                && MessageBox.Show(this, "Discard this purchase?", "Close", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                e.Cancel = true;
        };
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        var wide = Adaptive.Apply(Scroller, Root, ItemsPane, PayPane, SecondColumn, FirstRow, secondWidth: 380);
        PayScroller.VerticalScrollBarVisibility = wide ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        ItemsGrid.MaxHeight = wide ? double.PositiveInfinity : 360;
    }

    private void MatchList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MatchList.SelectedItem is ProductDto p) { _vm.PickMatchCommand.Execute(p); ScanBox.Focus(); }
    }

    private void MatchList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && MatchList.SelectedItem is ProductDto p) { _vm.PickMatchCommand.Execute(p); ScanBox.Focus(); e.Handled = true; }
    }

    private void SupplierList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: SupplierDto s }) _vm.PickSupplierCommand.Execute(s);
    }
}
