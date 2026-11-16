using System.Windows;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Infrastructure.Persistence;

namespace BillingSoftware.Desktop.Views;

public partial class SetupWindow : Window
{
    private readonly IAuthService _auth;
    private readonly ISettingsService _settings;
    private readonly AppPaths? _paths;

    public SetupWindow(IAuthService auth, ISettingsService settings, AppPaths? paths = null)
    {
        InitializeComponent();
        _auth = auth;
        _settings = settings;
        _paths = paths;
        StateBox.ItemsSource = StateCodes.All;
        StateBox.SelectedItem = StateCodes.All.First(s => s.Code == "33");
        Loaded += (_, _) => BusinessName.Focus();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        ErrorBox.Visibility = Visibility.Collapsed;
        if (Password.Password != Confirm.Password)
        {
            ShowProblem("The two passwords do not match.");
            return;
        }
        if (StateBox.SelectedItem is not StateItem state) { ShowProblem("Please choose the state."); return; }

        CreateButton.IsEnabled = false;
        try
        {
            await _settings.SaveCompanyAsync(new CompanyDto(BusinessName.Text, string.IsNullOrWhiteSpace(Gstin.Text) ? null : Gstin.Text,
                state.Code, Composition.IsChecked == true && GstOn.IsChecked == true, Address.Text, Phone.Text, GstOn.IsChecked == true));
            await _auth.CreateInitialAdminAsync(Username.Text, DisplayName.Text, Password.Password);
            DialogResult = true;
        }
        catch (BusinessRuleException ex) { ShowProblem(ex.Message); }
        catch (Exception ex) { Ui.ShowError(this, ex); }
        finally { CreateButton.IsEnabled = true; }
    }

    private void ShowProblem(string message)
    {
        ErrorText.Text = message;
        ErrorBox.Visibility = Visibility.Visible;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>A new computer, or a reinstall: bring back the data from a backup file instead of starting empty.</summary>
    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        ErrorBox.Visibility = Visibility.Collapsed;
        if (_paths == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose the backup file", Filter = "Backup files (*.db)|*.db|All files (*.*)|*.*", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;

        var key = new InputWindow("If this backup was made on another computer, type the recovery key that was shown when it was first set up.\nIf it was made on this computer, leave this empty.",
            "Recovery key", false) { Owner = this };
        if (key.ShowDialog() != true) return;
        var typed = (key.Value ?? "").Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();
        try
        {
            string keyHex;
            if (typed.Length > 0)
            {
                if (typed.Length != 64 || !typed.All(Uri.IsHexDigit)) { ShowProblem("The recovery key is not valid. It has 64 letters and digits (0-9, A-F)."); return; }
                keyHex = typed;
            }
            else keyHex = Convert.ToHexString(System.Security.Cryptography.ProtectedData.Unprotect(File.ReadAllBytes(_paths.KeyPath), null, System.Security.Cryptography.DataProtectionScope.LocalMachine));

            var problem = PendingRestore.Problem(_paths, dlg.FileName, keyHex);
            if (problem != null) { ShowProblem(problem); return; }
            if (MessageBox.Show(this, "The data in this backup will replace the empty data on this computer, and the program will restart.\n\nContinue?", "Restore from a backup",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            PendingRestore.Stage(_paths, dlg.FileName, "first-run setup", typed.Length > 0 ? keyHex : null);
            App.Restart();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            ShowProblem("The backup could not be prepared: " + ex.Message);
        }
    }
}
