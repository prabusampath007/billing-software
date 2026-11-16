namespace BillingSoftware.Application;

public enum ImportKind { Items = 1, Customers = 2, Suppliers = 3 }

public enum ImportRowStatus { Ready = 1, Duplicate = 2, Error = 3, Imported = 4 }

/// <summary>One line of the file and what will happen (or happened) to it.</summary>
public sealed record ImportRow(int RowNumber, string Name, ImportRowStatus Status, string Detail, IReadOnlyList<string>? Cells = null);

public sealed record ImportPreview(
    ImportKind Kind, string FileName, string? FileProblem, int TotalRows, int ReadyCount, int DuplicateCount, int ErrorCount,
    IReadOnlyList<string> IgnoredColumns, bool NeedsAsAtDate, IReadOnlyList<ImportRow> Rows, IReadOnlyList<string>? Columns = null);

public sealed record ImportResult(int Imported, int Duplicates, int Errors, IReadOnlyList<ImportRow> Rows, IReadOnlyList<string>? Columns = null);

public interface IImportService
{
    /// <summary>Reads a CSV or Excel file and checks every row the same way a person typing it in would be checked. Nothing is saved.</summary>
    Task<ImportPreview> PreviewAsync(ImportKind kind, string file);
    /// <summary>
    /// Saves the rows that are fine. Rows already in the software (same code, barcode, name or GSTIN) are skipped, so running it again is safe.
    /// <paramref name="asAt"/> is the date opening dues are "as at" (needed when the file has them).
    /// </summary>
    Task<ImportResult> ImportAsync(ImportKind kind, string file, DateOnly? asAt);
    /// <summary>Writes an empty Excel file with the right column headings and one example row.</summary>
    Task WriteTemplateAsync(ImportKind kind, string path);
}
