using System.Windows.Controls;
using BillingSoftware.Desktop.ViewModels;

namespace BillingSoftware.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => await vm.LoadAsync();
    }

    private void Close_Click(object sender, System.Windows.RoutedEventArgs e) => (System.Windows.Window.GetWindow(this) as MainWindow)?.GoToBilling();
}
