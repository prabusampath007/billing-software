using System.Windows;

namespace BillingSoftware.Desktop.Views;

public partial class RecoveryEntryWindow : Window
{
    public string Key => KeyInput.Text;

    public RecoveryEntryWindow(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
        Loaded += (_, _) => KeyInput.Focus();
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
