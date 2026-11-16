using System.Globalization;
using System.Printing;
using BillingSoftware.Application;
using BillingSoftware.Domain;

namespace BillingSoftware.Desktop.Services;

/// <summary>
/// Which printer prints what, and the paper sizes. The counter prints receipts on an 80 mm (or 58 mm) thermal roll; A4 documents
/// (statements, reports, invoices) and labels go to their own printer, or ask which one when none is set.
/// </summary>
public static class PrintConfig
{
    public static string? ReceiptPrinter { get; set; }
    public static string? DocumentPrinter { get; set; }
    public static string? LabelPrinter { get; set; }
    public static int PaperMm { get; set; } = 80;
    public static int LabelWidthMm { get; set; } = 50;
    public static int LabelHeightMm { get; set; } = 25;
    /// <summary>"roll" prints one label per label on a label printer; "sheet" fills an A4 sheet of labels.</summary>
    public static string LabelMode { get; set; } = "roll";

    public const double PxPerMm = 96 / 25.4;
    /// <summary>Width the receipt is laid out in: the printable part of the roll (80 mm paper prints about 72 mm, 58 mm paper about 48 mm).</summary>
    public static double PaperWidthPx => (PaperMm == 58 ? 54 : 76) * PxPerMm;
    public static int RuleChars => PaperMm == 58 ? 30 : 44;

    public static double Px(int mm) => mm * PxPerMm;

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static async Task LoadAsync(ISettingsService settings)
    {
        ReceiptPrinter = Clean(await settings.GetAsync(SettingKeys.PrintReceiptPrinter));
        DocumentPrinter = Clean(await settings.GetAsync(SettingKeys.PrintDocumentPrinter));
        LabelPrinter = Clean(await settings.GetAsync(SettingKeys.PrintLabelPrinter));
        PaperMm = (await settings.GetAsync(SettingKeys.PrintPaperMm)) == "58" ? 58 : 80;
        LabelWidthMm = Math.Clamp(int.TryParse(await settings.GetAsync(SettingKeys.PrintLabelWidthMm), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ? w : 50, 20, 120);
        LabelHeightMm = Math.Clamp(int.TryParse(await settings.GetAsync(SettingKeys.PrintLabelHeightMm), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? h : 25, 15, 120);
        LabelMode = (await settings.GetAsync(SettingKeys.PrintLabelMode)) == "sheet" ? "sheet" : "roll";
    }

    /// <summary>The printers installed on this computer.</summary>
    public static IReadOnlyList<string> InstalledPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            return server.GetPrintQueues().Select(q => q.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException or System.ComponentModel.Win32Exception) { return Array.Empty<string>(); }
    }
}
