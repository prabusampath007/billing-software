using System.Windows;
using System.Windows.Controls;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Desktop.ViewModels;

namespace BillingSoftware.Desktop.Views;

/// <summary>The Stock tab: what is on hand, with a panel for adding, adjusting and repacking the chosen item.</summary>
public partial class StockView : UserControl
{
    private readonly StockViewModel _vm;

    public StockView(StockViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(StockViewModel.IsPanelOpen)) ApplyLayout(); };
        Loaded += async (_, _) => { await _vm.LoadAsync(); ApplyLayout(); };
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        var wide = Adaptive.Apply(Scroller, Root, ListPane, FormPane, SecondColumn, FirstRow, secondWidth: 400, secondVisible: _vm.IsPanelOpen);
        FormScroller.VerticalScrollBarVisibility = wide ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }
}
