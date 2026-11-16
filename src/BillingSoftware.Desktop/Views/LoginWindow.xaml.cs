using System.Windows;
using BillingSoftware.Application;

namespace BillingSoftware.Desktop.Views;

public partial class LoginWindow : Window
{
    private readonly IAuthService _auth;
    public SessionUser? Result { get; private set; }

    /// <param name="lockedUsername">When set, the screen is a lock screen for that user (the same person must unlock it).</param>
    public LoginWindow(IAuthService auth, string? businessName, string? lockedUsername = null)
    {
        InitializeComponent();
        _auth = auth;
        if (!string.IsNullOrWhiteSpace(businessName)) BusinessText.Text = businessName;
        if (lockedUsername != null)
        {
            Title = "Locked";
            SubtitleText.Text = "Screen locked after a period of no activity. Enter your password to continue.";
            UserInput.Text = lockedUsername;
            UserInput.IsReadOnly = true;
            SignInButton.Content = "Unlock";
        }
        Loaded += (_, _) => (UserInput.IsReadOnly ? (UIElement)PasswordInput : UserInput).Focus();
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        SignInButton.IsEnabled = false;
        ErrorBox.Visibility = Visibility.Collapsed;
        try
        {
            Result = await _auth.LoginAsync(UserInput.Text, PasswordInput.Password);
            DialogResult = true;
        }
        catch (BusinessRuleException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorBox.Visibility = Visibility.Visible;
            PasswordInput.Clear();
            PasswordInput.Focus();
        }
        catch (Exception ex) { Services.Ui.ShowError(this, ex); }
        finally { SignInButton.IsEnabled = true; }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
