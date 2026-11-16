using System.Windows;
using System.Windows.Controls;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Desktop.ViewModels;

namespace BillingSoftware.Desktop.Views;

public partial class CustomersView : UserControl
{
    private readonly CustomersViewModel _vm;

    public CustomersView(CustomersViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Loaded += async (_, _) => { await _vm.LoadAsync(); ApplyLayout(); };
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        var wide = Adaptive.Apply(Scroller, Root, ListPane, FormPane, SecondColumn, FirstRow, secondWidth: 380);
        FormScroller.VerticalScrollBarVisibility = wide ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }
}
