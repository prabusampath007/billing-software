using System.Windows;
using System.Windows.Controls;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Desktop.ViewModels;

namespace BillingSoftware.Desktop.Views;

public partial class DayEndView : UserControl
{
    private readonly DayEndViewModel _vm;

    public DayEndView(DayEndViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Loaded += async (_, _) => { await _vm.RefreshAsync(); ApplyLayout(); };
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        var wide = Adaptive.Apply(Scroller, Root, LeftPane, RightPane, SecondColumn, FirstRow, secondWidth: 400);
        LeftScroller.VerticalScrollBarVisibility = wide ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        RightScroller.VerticalScrollBarVisibility = wide ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }
}
