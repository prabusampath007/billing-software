using System.Windows;

namespace BillingSoftware.Desktop.Views;

public partial class InputWindow : Window
{
    private readonly bool _secret;
    public string Value => _secret ? SecretInput.Password : PlainInput.Text;

    public InputWindow(string message, string title, bool isPassword)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        _secret = isPassword;
        if (isPassword) { PlainInput.Visibility = Visibility.Collapsed; SecretInput.Visibility = Visibility.Visible; }
        Loaded += (_, _) => (isPassword ? (UIElement)SecretInput : PlainInput).Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
