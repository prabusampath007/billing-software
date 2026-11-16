using System.Windows;
using BillingSoftware.Desktop.ViewModels;

namespace BillingSoftware.Desktop.Views;

public partial class ReturnWindow : Window
{
    public ReturnWindow(ReturnDialogViewModel vm)
    {
        InitializeComponent();
        Title = vm.Title;
        DataContext = vm;
        vm.RequestClose += ok => DialogResult = ok;
    }
}
