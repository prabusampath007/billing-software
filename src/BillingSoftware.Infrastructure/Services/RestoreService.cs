using System.Text.Json;
using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

internal sealed class RestoreService(DbFactory factory, ICurrentUser current, IAuthService auth, AppPaths paths) : ServiceBase(factory, current), IRestoreService
{
    public async Task<IReadOnlyList<BackupFileInfo>> ListBackupsAsync(string? folder = null)
    {
        RequireAdmin();
        if (string.IsNullOrWhiteSpace(folder))
        {
            await using var db = Factory.Create();
            folder = await db.Settings.AsNoTracking().Where(s => s.Key == SettingKeys.BackupFolder).Select(s => s.Value).FirstOrDefaultAsync();
        }
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return Array.Empty<BackupFileInfo>();
        return Directory.GetFiles(folder, "billing-*.db")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new BackupFileInfo(f.FullName, f.Name, ManifestTime(f.FullName) ?? f.LastWriteTime, f.Length, File.Exists(f.FullName + ".manifest.json")))
            .ToList();
    }

    private static DateTime? ManifestTime(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file + ".manifest.json"));
            return doc.RootElement.TryGetProperty("createdUtc", out var c) && c.TryGetDateTime(out var d) ? d.ToLocalTime() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    private static string? ManifestSha(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file + ".manifest.json"));
            return doc.RootElement.TryGetProperty("sha256", out var c) ? c.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    private RestorePreview Fail(string file, string message, bool checksum = false, IReadOnlyList<string>? warnings = null) =>
        new(file, false, message, checksum, null, 0, "", 0, null, null, 0, null, null, warnings ?? Array.Empty<string>());

    public async Task<RestorePreview> PreviewAsync(string backupFile)
    {
        RequireAdmin();
        backupFile = (backupFile ?? "").Trim();
        if (backupFile.Length == 0 || !File.Exists(backupFile)) return Fail(backupFile, "That backup file was not found.");
        if (string.Equals(Path.GetFullPath(backupFile), Path.GetFullPath(paths.DbPath), StringComparison.OrdinalIgnoreCase))
            return Fail(backupFile, "That is the database in use, not a backup.");

        var warnings = new List<string>();
        var checksumOk = false;
        var expected = ManifestSha(backupFile);
        if (expected == null) warnings.Add("This backup has no manifest file, so its checksum could not be checked. It was still checked for damage.");
        else
        {
            var actual = await Task.Run(() => PendingRestore.Sha256Of(backupFile));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                return Fail(backupFile, "This backup has changed since it was made (its checksum does not match). It may be damaged, so it will not be restored.");
            checksumOk = true;
        }

        string keyHex;
        try { keyHex = Convert.ToHexString(System.Security.Cryptography.ProtectedData.Unprotect(File.ReadAllBytes(paths.KeyPath), null, System.Security.Cryptography.DataProtectionScope.LocalMachine)); }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException) { return Fail(backupFile, "The database key could not be read."); }

        var problem = await Task.Run(() => PendingRestore.Problem(paths, backupFile, keyHex));
        if (problem != null) return Fail(backupFile, problem, checksumOk, warnings);

        int schema = 0, bills = 0; string company = ""; string? lastNo = null; DateTime? lastDate = null; DateTime? taken = ManifestTime(backupFile);
        await Task.Run(() =>
        {
            using var conn = new DbFactory(paths, keyHex).OpenRaw(backupFile);
            string? Scalar(string sql) { using var c = conn.CreateCommand(); c.CommandText = sql; return Convert.ToString(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture); }
            schema = int.TryParse(Scalar("SELECT COALESCE(MAX(Version),0) FROM SchemaVersions"), out var v) ? v : 0;
            company = Scalar("SELECT Name FROM Companies LIMIT 1") ?? "";
            bills = int.TryParse(Scalar("SELECT COUNT(*) FROM Sales"), out var b) ? b : 0;
            lastNo = Scalar("SELECT InvoiceNumber FROM Sales ORDER BY Id DESC LIMIT 1");
            lastDate = DateTime.TryParse(Scalar("SELECT SaleDate FROM Sales ORDER BY Id DESC LIMIT 1"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        });
        if (schema > DatabaseInitializer.CurrentSchemaVersion)
            return Fail(backupFile, "This backup was made by a newer version of the software. Install the newer version first.", checksumOk, warnings);

        await using var db = Factory.Create();
        var curBills = await db.Sales.CountAsync();
        var curLast = await db.Sales.AsNoTracking().OrderByDescending(s => s.Id).Select(s => new { s.InvoiceNumber, s.SaleDate }).FirstOrDefaultAsync();
        if (bills < curBills) warnings.Add($"Restoring will lose the {curBills - bills} bill(s) made after this backup. They stay in the safety copy of today's data.");
        if (schema < DatabaseInitializer.CurrentSchemaVersion) warnings.Add("This backup is from an older version; it is brought up to date automatically after the restore.");
        if (taken.HasValue && DateTime.Now - taken.Value > TimeSpan.FromDays(7)) warnings.Add($"This backup is {(DateTime.Now - taken.Value).Days} days old.");

        return new RestorePreview(backupFile, true, "This backup can be restored.", checksumOk, taken, schema, company, bills, lastNo, lastDate,
            curBills, curLast?.InvoiceNumber, curLast?.SaleDate, warnings);
    }

    public async Task ScheduleAsync(string backupFile, string adminPassword)
    {
        var admin = RequireAdmin();
        var preview = await PreviewAsync(backupFile);
        if (!preview.CanRestore) throw new BusinessRuleException(preview.Message);
        await auth.VerifyAdminApprovalAsync(adminPassword);       // wrong password throws

        PendingRestore.Stage(paths, backupFile, admin.Username);
        await using var db = Factory.Create();
        await AuditWriter.AppendAsync(db, admin, "restore.scheduled", "Restore", null, $"{Path.GetFileName(backupFile)} will replace the data at the next start");
    }
}
