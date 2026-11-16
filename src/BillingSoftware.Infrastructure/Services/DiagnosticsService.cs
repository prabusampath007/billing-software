using System.IO.Compression;
using System.Text;
using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

internal sealed class DiagnosticsService(DbFactory factory, ICurrentUser current, IAuditService audit, IBackupService backup, AppPaths paths)
    : ServiceBase(factory, current), IDiagnosticsService
{
    private const long WarnFreeBytes = 2L * 1024 * 1024 * 1024;
    private const long LowFreeBytes = 500L * 1024 * 1024;
    private const long MaxLogBytes = 5L * 1024 * 1024;

    public async Task<IReadOnlyList<HealthCheck>> RunAsync()
    {
        RequireAdmin();
        var list = new List<HealthCheck>();

        // database
        try
        {
            using var conn = Factory.OpenRaw(paths.DbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var result = cmd.ExecuteScalar() as string;
            list.Add(result == "ok"
                ? new("Database", CheckLevel.Ok, $"The database opens and passes its integrity check ({new FileInfo(paths.DbPath).Length / 1024.0 / 1024.0:0.0} MB).")
                : new("Database", CheckLevel.Problem, "The database failed its integrity check: " + result + ". Restore the latest backup (Settings, Restore from a backup)."));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            list.Add(new("Database", CheckLevel.Problem, "The database could not be read: " + ex.Message));
        }

        // books and schema
        await using (var db = Factory.Create())
        {
            var debit = await db.JournalLines.SumAsync(l => (long?)l.DebitPaise) ?? 0;
            var credit = await db.JournalLines.SumAsync(l => (long?)l.CreditPaise) ?? 0;
            list.Add(debit == credit
                ? new("Books", CheckLevel.Ok, "Every entry in the books balances (money in equals money out).")
                : new("Books", CheckLevel.Problem, $"The books do not balance (difference {Money.Format(Math.Abs(debit - credit))}). Contact support."));
            var version = await db.SchemaVersions.MaxAsync(v => (int?)v.Version) ?? 0;
            list.Add(version == DatabaseInitializer.CurrentSchemaVersion
                ? new("Database version", CheckLevel.Ok, $"The database is at version {version}, the version this program expects.")
                : new("Database version", CheckLevel.Warning, $"The database is at version {version}; this program expects {DatabaseInitializer.CurrentSchemaVersion}."));
            var lockDate = await db.Settings.AsNoTracking().Where(s => s.Key == SettingKeys.LockedThrough).Select(s => s.Value).FirstOrDefaultAsync();
            list.Add(new("Locked period", CheckLevel.Ok, string.IsNullOrWhiteSpace(lockDate) ? "No period is locked." : $"Everything up to {lockDate} is locked."));
        }

        // activity log
        var (valid, broken) = await audit.VerifyChainAsync();
        list.Add(valid ? new("Activity log", CheckLevel.Ok, "The activity log is intact: no record has been changed.")
                       : new("Activity log", CheckLevel.Problem, $"The activity log was altered at record {broken}. Contact support."));

        // backups
        var b = await backup.GetStatusAsync();
        var bLevel = string.IsNullOrWhiteSpace(b.Folder) || b.LastBackupUtc == null || b.Overdue ? CheckLevel.Warning : CheckLevel.Ok;
        list.Add(new("Backups", bLevel, b.Summary + (bLevel != CheckLevel.Ok ? " Back up now." : b.FolderOnSystemDisk ? " Tip: copy the backup folder to a USB drive now and then." : "")));

        // disk space and folders
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(paths.DataDir))!);
            var free = drive.AvailableFreeSpace;
            var gb = free / 1024.0 / 1024 / 1024;
            list.Add(free < LowFreeBytes ? new("Disk space", CheckLevel.Problem, $"Only {gb:0.0} GB is free on the data drive. Free some space now.")
                   : free < WarnFreeBytes ? new("Disk space", CheckLevel.Warning, $"{gb:0.0} GB is free on the data drive. It is getting low.")
                   : new("Disk space", CheckLevel.Ok, $"{gb:0.0} GB is free on the data drive."));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            list.Add(new("Disk space", CheckLevel.Warning, "The free space could not be read."));
        }
        try
        {
            var probe = Path.Combine(paths.DataDir, $".check-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probe, "x");
            File.Delete(probe);
            list.Add(new("Data folder", CheckLevel.Ok, "The data folder can be written to."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            list.Add(new("Data folder", CheckLevel.Problem, "The data folder cannot be written to: " + ex.Message));
        }

        if (PendingRestore.IsPending(paths)) list.Add(new("Restore", CheckLevel.Warning, "A restore is waiting to be carried out. Restart the program to finish it."));
        return list;
    }

    public async Task CreateSupportFileAsync(string zipPath)
    {
        RequireAdmin();
        if (string.IsNullOrWhiteSpace(zipPath)) throw new ValidationException("Choose where to save the file.");
        var checks = await RunAsync();

        var env = new StringBuilder();
        env.AppendLine($"Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        env.AppendLine($"Program version: {typeof(DiagnosticsService).Assembly.GetName().Version}");
        env.AppendLine($"Database version: {DatabaseInitializer.CurrentSchemaVersion}");
        env.AppendLine($"Windows: {Environment.OSVersion}");
        env.AppendLine($".NET: {Environment.Version}");
        env.AppendLine($"64-bit process: {Environment.Is64BitProcess}");
        env.AppendLine($"Region/culture: {System.Globalization.CultureInfo.CurrentCulture.Name}");
        env.AppendLine($"Data drive: {Path.GetPathRoot(paths.DataDir)}");

        var counts = new StringBuilder();
        await using (var db = Factory.Create())
        {
            counts.AppendLine("Record counts (numbers only, no content):");
            counts.AppendLine($"Items: {await db.Products.CountAsync()}");
            counts.AppendLine($"Customers: {await db.Parties.CountAsync()}");
            counts.AppendLine($"Suppliers: {await db.Suppliers.CountAsync()}");
            counts.AppendLine($"Bills: {await db.Sales.CountAsync()}");
            counts.AppendLine($"Purchases: {await db.Purchases.CountAsync()}");
            counts.AppendLine($"Expenses: {await db.Expenses.CountAsync()}");
            counts.AppendLine($"Journal entries: {await db.Journals.CountAsync()}");
            counts.AppendLine($"Activity log entries: {await db.AuditEvents.CountAsync()}");
            counts.AppendLine($"Users: {await db.Users.CountAsync()}");
            counts.AppendLine($"Held bills: {await db.HeldBills.CountAsync()}");
        }

        var tmp = zipPath + ".part";
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                void Add(string name, string text)
                {
                    var e = zip.CreateEntry(name);
                    using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                    w.Write(text);
                }
                Add("health-check.txt", string.Join(Environment.NewLine, checks.Select(c => $"[{c.Level}] {c.Name}: {c.Message}")));
                Add("environment.txt", env.ToString());
                Add("record-counts.txt", counts.ToString());
                Add("README.txt", "This file was made by Billing Software for support. It contains the health check, program and Windows versions, record counts and recent log files. " +
                                  "It does not contain bills, customer or supplier details, passwords, the database, the database key or the recovery key.");

                if (Directory.Exists(paths.LogsDir))
                    foreach (var f in new DirectoryInfo(paths.LogsDir).GetFiles().OrderByDescending(f => f.LastWriteTime).Take(5))
                    {
                        if (f.Length > MaxLogBytes) continue;
                        try
                        {
                            using var src = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            var entry = zip.CreateEntry("logs/" + f.Name);
                            using var dst = entry.Open();
                            src.CopyTo(dst);
                        }
                        catch (IOException) { /* a log being written right now: skip it */ }
                    }
            }
            File.Move(tmp, zipPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw new BusinessRuleException("The support file could not be saved: " + ex.Message);
        }
    }
}
