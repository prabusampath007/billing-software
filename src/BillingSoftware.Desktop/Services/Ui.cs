using System.Windows;
using System.Windows.Documents;
using BillingSoftware.Application;
using BillingSoftware.Domain;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace BillingSoftware.Desktop.Services;

/// <summary>What the return dialog needs to show and to do; the same dialog serves customer returns and supplier returns.</summary>
public sealed record ReturnDialogRequest(
    string Title, string Subtitle, IReadOnlyList<ReturnableLineDto> Lines, bool AllowRestock, string CreditModeLabel,
    Func<IReadOnlyList<ReturnLineInput>, PaymentMode, string, Task<ReturnPreview>> Preview,
    Func<IReadOnlyList<ReturnLineInput>, PaymentMode, string, Guid, long?, Task<ReturnResult>> Submit);

/// <summary>Everything the view models need from the UI, so they never touch windows directly.</summary>
public interface IUiHost
{
    Task<long?> RequestApprovalAsync(string message);
    void ShowError(Exception ex);
    bool Confirm(string message, string title = "Please confirm");
    void Info(string message, string title = "Billing Software");
    void ShowReceipt(SaleDetailDto sale, CompanyDto? company, bool duplicate);
    string? PickFolder(string? initial);
    string? Prompt(string message, string title, bool isPassword = false);
    void ShowDocument(string title, Func<FlowDocument> build);
    Task<ReturnResult?> ShowReturnDialogAsync(ReturnDialogRequest request);
    /// <summary>Opens the purchase entry window; true when a purchase was saved.</summary>
    bool ShowPurchaseEntry();
    string? PickSaveFile(string suggestedName, string filter);
    string? PickOpenFile(string title, string filter);
    /// <summary>Shows the held bills and returns the one to bring back (null if none); <paramref name="discard"/> removes one the user throws away.</summary>
    Task<long?> PickHeldBillAsync(IReadOnlyList<HeldBillSummary> items, Func<long, Task> discard);
    /// <summary>Starts the program again (after waiting for this copy to close) and closes this one.</summary>
    void RestartApplication();
}

public sealed class UiHost(IAuthService auth, IServiceProvider services) : IUiHost
{
    private static Window? Owner => System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                                    ?? System.Windows.Application.Current?.MainWindow;

    public Task<long?> RequestApprovalAsync(string message)
    {
        var w = new Views.ApprovalWindow(auth, message) { Owner = Owner };
        return Task.FromResult(w.ShowDialog() == true ? w.ApproverId : (long?)null);
    }

    public void ShowError(Exception ex) => Ui.ShowError(Owner, ex);

    public bool Confirm(string message, string title = "Please confirm") =>
        MessageBox.Show(Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void Info(string message, string title = "Billing Software") =>
        MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowReceipt(SaleDetailDto sale, CompanyDto? company, bool duplicate) =>
        new Views.ReceiptWindow(sale, company, duplicate) { Owner = Owner }.ShowDialog();

    public void ShowDocument(string title, Func<FlowDocument> build) =>
        new Views.ReceiptWindow(title, build) { Owner = Owner }.ShowDialog();

    public Task<ReturnResult?> ShowReturnDialogAsync(ReturnDialogRequest request)
    {
        var vm = new ViewModels.ReturnDialogViewModel(request, this);
        var window = new Views.ReturnWindow(vm) { Owner = Owner };
        window.ShowDialog();
        return Task.FromResult(vm.Result);
    }

    public bool ShowPurchaseEntry()
    {
        var window = services.GetRequiredService<Views.PurchaseEntryWindow>();
        window.Owner = Owner;
        return window.ShowDialog() == true;
    }

    public string? PickSaveFile(string suggestedName, string filter)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = suggestedName, Filter = filter, OverwritePrompt = true };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public string? PickOpenFile(string title, string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public void RestartApplication() => App.Restart();

    public Task<long?> PickHeldBillAsync(IReadOnlyList<HeldBillSummary> items, Func<long, Task> discard)
    {
        var w = new Views.HeldBillsWindow(items, discard) { Owner = Owner };
        return Task.FromResult(w.ShowDialog() == true ? w.ChosenId : null);
    }

    public string? Prompt(string message, string title, bool isPassword = false)
    {
        var w = new Views.InputWindow(message, title, isPassword) { Owner = Owner };
        return w.ShowDialog() == true ? w.Value : null;
    }

    public string? PickFolder(string? initial)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a backup folder (a second drive or USB is best)" };
        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial)) dlg.InitialDirectory = initial;
        return dlg.ShowDialog(Owner) == true ? dlg.FolderName : null;
    }
}

public static class Ui
{
    /// <summary>
    /// Business-rule problems are shown in plain words. Anything unexpected is logged with an error ID
    /// the customer can quote to support; technical detail stays out of the dialog (plan section 8).
    /// </summary>
    public static string ShowError(Window? owner, Exception ex)
    {
        if (ex is BusinessRuleException)
        {
            MessageBox.Show(owner, ex.Message, "Please check", MessageBoxButton.OK, MessageBoxImage.Warning);
            return "";
        }
        var id = LogUnexpected(ex);
        MessageBox.Show(owner, $"Something went wrong and the action was not completed.\n\nIf it keeps happening, tell support this error ID: {id}",
            "Billing Software", MessageBoxButton.OK, MessageBoxImage.Error);
        return id;
    }

    public static string LogUnexpected(Exception ex)
    {
        var id = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Log.Error(ex, "Unexpected error {ErrorId}", id);
        return id;
    }

    public static string Money(long paise) => Domain.Money.Format(paise);
}
