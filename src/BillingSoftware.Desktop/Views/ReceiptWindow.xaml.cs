using System.Windows;
using System.Windows.Documents;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;

namespace BillingSoftware.Desktop.Views;

/// <summary>Preview and print window for any bill, credit note, debit note or purchase voucher.</summary>
public partial class ReceiptWindow : Window
{
    private readonly Func<FlowDocument> _build;

    public ReceiptWindow(string title, Func<FlowDocument> build)
    {
        InitializeComponent();
        _build = build;
        Title = title;
        Viewer.Document = build();
        Loaded += (_, _) => PrintButton.Focus();
    }

    public ReceiptWindow(SaleDetailDto sale, CompanyDto? company, bool duplicate)
        : this($"Bill {sale.InvoiceNumber}", () => ReceiptBuilder.Build(sale, company, duplicate)) { }

    private void Print_Click(object sender, RoutedEventArgs e) => TryPrint(false);
    private void PrintDialog_Click(object sender, RoutedEventArgs e) => TryPrint(true);

    private void TryPrint(bool dialog)
    {
        try
        {
            // A fresh document for printing: the one in the viewer is already laid out for the screen.
            ReceiptBuilder.Print(_build(), dialog);
        }
        catch (Exception ex) when (ex is System.Printing.PrintSystemException or System.Printing.PrintQueueException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, "It could not be printed. Please check the printer is on and has paper, then try again. Nothing was lost: the document is saved.",
                "Printer problem", MessageBoxButton.OK, MessageBoxImage.Warning);
            Serilog.Log.Warning(ex, "Print failed");
        }
    }
}
