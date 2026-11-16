using System.Windows;
using BillingSoftware.Application;

namespace BillingSoftware.Desktop.Views;

public partial class ApprovalWindow : Window
{
    private readonly IAuthService _auth;
    public long ApproverId { get; private set; }

    public ApprovalWindow(IAuthService auth, string message)
    {
        InitializeComponent();
        _auth = auth;
        MessageText.Text = message;
        Loaded += (_, _) => PasswordInput.Focus();
    }

    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        ApproveButton.IsEnabled = false;
        ErrorBox.Visibility = Visibility.Collapsed;
        try
        {
            ApproverId = await _auth.VerifyAdminApprovalAsync(PasswordInput.Password);
            DialogResult = true;
        }
        catch (BusinessRuleException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorBox.Visibility = Visibility.Visible;
            PasswordInput.Clear();
            PasswordInput.Focus();
        }
        catch (Exception ex)
        {
            Services.Ui.ShowError(this, ex);
        }
        finally { ApproveButton.IsEnabled = true; }
    }
}
