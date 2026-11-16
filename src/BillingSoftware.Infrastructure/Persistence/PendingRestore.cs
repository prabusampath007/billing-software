using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Persistence;

/// <summary>
/// Restoring replaces the live database file, which cannot be done while the program has it open. So a restore is staged (checked and copied
/// next to the data) and carried out at the next start, before the database is opened. The data being replaced is always kept first.
/// </summary>
public static class PendingRestore
{
    private sealed record Marker(DateTime RequestedUtc, string RequestedBy, string SourceName, string Sha256, string? KeyProtectedBase64);
    private sealed record Applied(DateTime AppliedUtc, string SourceName, string? SafetyCopy, string RequestedBy);

    public static string MarkerPath(AppPaths p) => Path.Combine(p.DataDir, "restore.pending.json");
    public static string AppliedPath(AppPaths p) => Path.Combine(p.DataDir, "restore.applied.json");
    public static string StagingDir(AppPaths p) => Path.Combine(p.DataDir, "restore-staging");
    public static string StagingFile(AppPaths p) => Path.Combine(StagingDir(p), "billing.db");
    public static string SafetyDir(AppPaths p) => Path.Combine(p.DataDir, "before-restore");

    public static bool IsPending(AppPaths p) => File.Exists(MarkerPath(p));

    public static string Sha256Of(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>
    /// Checks that a database file opens with <paramref name="keyHex"/>, passes SQLite's integrity check and has balanced books.
    /// Returns null when it is fine, otherwise a plain-words reason.
    /// </summary>
    public static string? Problem(AppPaths paths, string file, string keyHex)
    {
        try
        {
            var factory = new DbFactory(paths, keyHex);
            using var conn = factory.OpenRaw(file);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var integrity = cmd.ExecuteScalar() as string;
            if (integrity != "ok") return "The backup file is damaged (" + integrity + ").";
            cmd.CommandText = "SELECT COALESCE(SUM(DebitPaise),0), COALESCE(SUM(CreditPaise),0) FROM JournalLines";
            using var r = cmd.ExecuteReader();
            if (r.Read() && r.GetInt64(0) != r.GetInt64(1)) return "The books in this backup do not balance, so it cannot be trusted.";
            return null;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            return ex.SqliteErrorCode == 26 || ex.Message.Contains("not a database", StringComparison.OrdinalIgnoreCase)
                ? "This file is not a backup that can be opened with this computer's key. If it was made on another computer, use the recovery key."
                : "The backup could not be read: " + ex.Message;
        }
    }

    /// <summary>
    /// Copies a backup next to the data and records that it should replace the database at the next start.
    /// When the backup came from another computer, <paramref name="installKeyHex"/> (from the recovery key) is kept, protected for this computer, and installed with it.
    /// </summary>
    public static void Stage(AppPaths paths, string backupFile, string requestedBy, string? installKeyHex = null)
    {
        Directory.CreateDirectory(StagingDir(paths));
        var staged = StagingFile(paths);
        File.Copy(backupFile, staged, overwrite: true);
        var sha = Sha256Of(staged);
        if (sha != Sha256Of(backupFile)) { File.Delete(staged); throw new IOException("The copy of the backup did not match the original. Try again."); }
        string? protectedKey = installKeyHex == null ? null
            : Convert.ToBase64String(ProtectedData.Protect(Convert.FromHexString(installKeyHex), null, DataProtectionScope.LocalMachine));
        File.WriteAllText(MarkerPath(paths), JsonSerializer.Serialize(new Marker(DateTime.UtcNow, requestedBy, Path.GetFileName(backupFile), sha, protectedKey)));
    }

    /// <summary>Removes a staged restore that has not been applied yet.</summary>
    public static void Cancel(AppPaths paths)
    {
        try { File.Delete(MarkerPath(paths)); if (Directory.Exists(StagingDir(paths))) Directory.Delete(StagingDir(paths), true); }
        catch (IOException) { /* it is cleaned up at the next start */ }
    }

    /// <summary>Carries out a staged restore. Call before the database is opened. Never destroys data it has not first saved.</summary>
    public static (bool Applied, string Message) TryApply(AppPaths paths)
    {
        if (!File.Exists(MarkerPath(paths))) return (false, "");
        Marker marker;
        try { marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerPath(paths)))!; }
        catch (Exception ex) when (ex is JsonException or IOException or NullReferenceException)
        { Cancel(paths); return (false, "A restore was requested but its details could not be read, so nothing was changed."); }

        var staged = StagingFile(paths);
        if (!File.Exists(staged)) { Cancel(paths); return (false, "A restore was requested but the staged backup is missing, so nothing was changed."); }
        if (Sha256Of(staged) != marker.Sha256) { Cancel(paths); return (false, "The staged backup no longer matches what was checked, so nothing was changed."); }

        try
        {
            string keyHex;
            if (marker.KeyProtectedBase64 != null)
                keyHex = Convert.ToHexString(ProtectedData.Unprotect(Convert.FromBase64String(marker.KeyProtectedBase64), null, DataProtectionScope.LocalMachine));
            else if (File.Exists(paths.KeyPath))
                keyHex = Convert.ToHexString(ProtectedData.Unprotect(File.ReadAllBytes(paths.KeyPath), null, DataProtectionScope.LocalMachine));
            else { Cancel(paths); return (false, "The database key is missing, so the restore was not carried out."); }

            var problem = Problem(paths, staged, keyHex);
            if (problem != null) { Cancel(paths); return (false, "The restore was not carried out. " + problem); }

            // 1. keep what is there now (a safe copy through SQLite's own backup, and the old key if it is being replaced)
            Directory.CreateDirectory(SafetyDir(paths));
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string? safety = null;
            if (File.Exists(paths.DbPath))
            {
                safety = Path.Combine(SafetyDir(paths), $"billing-before-restore-{stamp}.db");
                if (File.Exists(paths.KeyPath))
                {
                    var oldKey = Convert.ToHexString(ProtectedData.Unprotect(File.ReadAllBytes(paths.KeyPath), null, DataProtectionScope.LocalMachine));
                    var oldFactory = new DbFactory(paths, oldKey);
                    using (var src = oldFactory.OpenRaw(paths.DbPath)) using (var dst = oldFactory.OpenRaw(safety)) src.BackupDatabase(dst);
                    if (marker.KeyProtectedBase64 != null) File.Copy(paths.KeyPath, Path.Combine(SafetyDir(paths), $"db.key.before-restore-{stamp}"), true);
                }
                else File.Copy(paths.DbPath, safety, true);
            }

            // 2. install the key that goes with the backup, if it came from another computer
            if (marker.KeyProtectedBase64 != null) new DbKeyProvider(paths).RestoreFromRecoveryKey(keyHex);

            // 3. replace the database: remove the old journal files first (they belong to the old database), then move the new file in
            foreach (var extra in new[] { paths.DbPath + "-wal", paths.DbPath + "-shm", paths.DbPath + "-journal" })
                if (File.Exists(extra)) File.Delete(extra);
            File.Move(staged, paths.DbPath, overwrite: true);

            File.WriteAllText(AppliedPath(paths), JsonSerializer.Serialize(new Applied(DateTime.UtcNow, marker.SourceName, safety, marker.RequestedBy)));
            Cancel(paths);
            return (true, $"The backup \"{marker.SourceName}\" was restored." + (safety == null ? "" : $" The data from before is kept in {SafetyDir(paths)}."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or CryptographicException)
        {
            return (false, "The restore could not be carried out and your data was left as it was: " + ex.Message);
        }
    }

    /// <summary>After a restore, writes it into the (restored) activity log. Call once the database is open.</summary>
    public static async Task RecordAppliedAsync(DbFactory factory, AppPaths paths)
    {
        if (!File.Exists(AppliedPath(paths))) return;
        try
        {
            var a = JsonSerializer.Deserialize<Applied>(File.ReadAllText(AppliedPath(paths)));
            if (a != null)
            {
                await using var db = factory.Create();
                await Services.AuditWriter.AppendAsync(db, null, "restore.applied", "Restore", null,
                    $"Backup {a.SourceName} restored at {a.AppliedUtc:O}, requested by {a.RequestedBy}" + (a.SafetyCopy == null ? "" : $"; previous data kept as {Path.GetFileName(a.SafetyCopy)}"));
            }
            File.Delete(AppliedPath(paths));
        }
        catch (Exception ex) when (ex is IOException or JsonException or DbUpdateException) { /* the log entry is a nicety; never block starting */ }
    }
}
