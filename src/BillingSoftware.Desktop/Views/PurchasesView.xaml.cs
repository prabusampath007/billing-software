using System.Windows.Controls;
using BillingSoftware.Desktop.ViewModels;

namespace BillingSoftware.Desktop.Views;

public partial class PurchasesView : UserControl
{
    public PurchasesView(PurchasesViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => await vm.LoadAsync();
    }
}
