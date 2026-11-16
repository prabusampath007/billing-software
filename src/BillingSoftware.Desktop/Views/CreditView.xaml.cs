using System.Windows;
using System.Windows.Controls;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Desktop.ViewModels;

namespace BillingSoftware.Desktop.Views;

public partial class CreditView : UserControl
{
    private readonly CreditViewModel _vm;

    public CreditView(CreditViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Loaded += async (_, _) => { await _vm.PrepareAsync(); ApplyLayout(); };
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout() => Adaptive.Apply(Scroller, Root, ListPane, FormPane, SecondColumn, FirstRow, secondWidth: 380);
}
