using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Desktop.ViewModels;

namespace BillingSoftware.Desktop.Views;

public partial class BillingView : UserControl
{
    private readonly BillingViewModel _vm;

    public BillingView(BillingViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Loaded += async (_, _) =>
        {
            await _vm.InitializeAsync();
            ScanBox.Focus();
            ApplyLayout();
        };
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        var wide = Adaptive.Apply(Scroller, Root, CartPane, PayPane, SecondColumn, FirstRow, secondWidth: Scroller.ActualWidth >= 1250 ? 400 : 360);
        // When stacked, the whole page scrolls; a second scroll bar inside the payment card would fight it.
        PayScroller.VerticalScrollBarVisibility = wide ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        CartGrid.MaxHeight = wide ? double.PositiveInfinity : 380;
    }

    private void ScanBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && MatchList.Items.Count > 0)
        {
            MatchList.SelectedIndex = 0;
            (MatchList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.ScanText = "";
            _vm.Matches.Clear();
        }
    }

    private void MatchList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MatchList.SelectedItem is ProductDto p) { _vm.PickMatchCommand.Execute(p); ScanBox.Focus(); }
    }

    private void MatchList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && MatchList.SelectedItem is ProductDto p) { _vm.PickMatchCommand.Execute(p); ScanBox.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm.Matches.Clear(); ScanBox.Focus(); }
    }

    private void CustomerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: PartyDto p }) _vm.PickCustomerCommand.Execute(p);
    }
}
