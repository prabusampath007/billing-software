using System.Windows;

namespace BillingSoftware.Desktop.Views;

public partial class RecoveryKeyWindow : Window
{
    private readonly string _key;

    public RecoveryKeyWindow(string recoveryKey)
    {
        InitializeComponent();
        _key = recoveryKey;
        KeyText.Text = recoveryKey;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_key); }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard busy: user can still select the text */ }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "BillingSoftware-recovery-key.txt", Filter = "Text file|*.txt" };
        if (dlg.ShowDialog(this) == true)
            File.WriteAllText(dlg.FileName, "Billing Software recovery key (keep this safe and private)\r\n\r\n" + _key + "\r\n");
    }

    private void Acknowledge_Changed(object sender, RoutedEventArgs e) => ContinueButton.IsEnabled = Acknowledge.IsChecked == true;

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
