using System.Windows.Controls;
using BillingSoftware.Desktop.ViewModels;

namespace BillingSoftware.Desktop.Views;

public partial class ReportsView : UserControl
{
    public ReportsView(ReportsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => await vm.LoadAsync();
    }
}
