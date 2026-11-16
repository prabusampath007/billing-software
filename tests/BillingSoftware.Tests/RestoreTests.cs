using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BillingSoftware.Tests;

public class RestoreTests : IDisposable
{
    private readonly List<string> _temp = new();

    private string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "billing-rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _temp.Add(d);
        return d;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var d in _temp) { try { Directory.Delete(d, true); } catch (IOException) { } }
    }

    /// <summary>A business with one bill, backed up; then a second bill is made so today's data is newer than the backup.</summary>
    private async Task<(TestApp App, string Backup, long Soap)> AppWithBackupAsync()
    {
        var app = TestApp.Create();
        var soap = await app.SeedAsync();
        await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(soap, 1000, 11_800));
        var folder = TempDir();
        var b = await app.Get<IBackupService>().BackupNowAsync(folder);
        Assert.True(b.Success, b.Message);
        await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(soap, 1000, 11_800));        // made after the backup
        return (app, b.Path!, soap);
    }

    private static int BillCount(TestApp app) =>
        app.Get<ISalesService>().ListAsync(DateOnly.FromDateTime(DateTime.Today)).GetAwaiter().GetResult().Count;

    // ------------------------------------------------------------------ checking a backup

    [Fact]
    public async Task A_good_backup_is_previewed_with_what_it_holds_and_what_would_be_lost()
    {
        var (app, backup, _) = await AppWithBackupAsync();
        using (app)
        {
            var p = await app.Get<IRestoreService>().PreviewAsync(backup);
            Assert.True(p.CanRestore, p.Message);
            Assert.True(p.ChecksumVerified);
            Assert.Equal(1, p.BillCount);
            Assert.Equal(2, p.CurrentBillCount);
            Assert.Equal("Test Stores", p.CompanyName);
            Assert.StartsWith("INV/", p.LastBillNumber);
            Assert.Equal(DatabaseInitializer.CurrentSchemaVersion, p.SchemaVersion);
            Assert.Contains(p.Warnings, w => w.Contains("lose the 1 bill"));
            Assert.NotNull(p.BackupTakenLocal);
        }
    }

    [Fact]
    public async Task A_changed_backup_a_missing_file_and_the_live_database_are_refused()
    {
        var (app, backup, _) = await AppWithBackupAsync();
        using (app)
        {
            var restore = app.Get<IRestoreService>();
            // changed after it was made: the checksum no longer matches
            var changed = Path.Combine(TempDir(), "billing-changed.db");
            File.Copy(backup, changed);
            File.Copy(backup + ".manifest.json", changed + ".manifest.json");
            await using (var fs = new FileStream(changed, FileMode.Open, FileAccess.Write)) { fs.Seek(2048, SeekOrigin.Begin); fs.WriteByte(0x5A); }
            var bad = await restore.PreviewAsync(changed);
            Assert.False(bad.CanRestore);
            Assert.Contains("checksum", bad.Message);

            Assert.False((await restore.PreviewAsync(Path.Combine(TempDir(), "nothing.db"))).CanRestore);
            Assert.False((await restore.PreviewAsync("")).CanRestore);
            var live = await restore.PreviewAsync(app.Paths.DbPath);
            Assert.False(live.CanRestore);
            Assert.Contains("in use", live.Message);
        }
    }

    [Fact]
    public async Task A_file_that_is_not_a_database_and_a_backup_from_another_computer_are_refused()
    {
        var (app, _, _) = await AppWithBackupAsync();
        using (app)
        {
            var junk = Path.Combine(TempDir(), "billing-junk.db");
            await File.WriteAllTextAsync(junk, "this is not a database " + new string('x', 5000));
            var p = await app.Get<IRestoreService>().PreviewAsync(junk);
            Assert.False(p.CanRestore);
            Assert.Contains("not a backup", p.Message);

            using var other = TestApp.Create();                                          // a different computer: different key
            await other.SeedAsync();
            var foreign = await other.Get<IBackupService>().BackupNowAsync(TempDir());
            var f = await app.Get<IRestoreService>().PreviewAsync(foreign.Path!);
            Assert.False(f.CanRestore);
            Assert.Contains("recovery key", f.Message);
        }
    }

    [Fact]
    public async Task A_backup_from_a_newer_version_is_refused()
    {
        var (app, backup, _) = await AppWithBackupAsync();
        using (app)
        {
            var newer = Path.Combine(TempDir(), "billing-newer.db");
            File.Copy(backup, newer);
            using (var conn = app.Get<DbFactory>().OpenRaw(newer))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO SchemaVersions (Version, AppliedAtUtc, Description) VALUES (999, '2026-01-01', 'future')";
                cmd.ExecuteNonQuery();
            }
            var p = await app.Get<IRestoreService>().PreviewAsync(newer);
            Assert.False(p.CanRestore);
            Assert.Contains("newer version", p.Message);
        }
    }

    [Fact]
    public async Task Backups_in_the_folder_are_listed_newest_first()
    {
        var (app, first, _) = await AppWithBackupAsync();
        using (app)
        {
            var folder = Path.GetDirectoryName(first)!;
            await Task.Delay(1100);
            var second = await app.Get<IBackupService>().BackupNowAsync(folder);
            var list = await app.Get<IRestoreService>().ListBackupsAsync(folder);
            Assert.Equal(2, list.Count);
            Assert.Equal(second.Path, list[0].Path);
            Assert.All(list, x => Assert.True(x.ManifestFound));
            Assert.Empty(await app.Get<IRestoreService>().ListBackupsAsync(Path.Combine(TempDir(), "nope")));
        }
    }

    // ------------------------------------------------------------------ scheduling and applying

    [Fact]
    public async Task A_restore_needs_the_Admin_password_and_is_Admin_only()
    {
        var (app, backup, _) = await AppWithBackupAsync();
        using (app)
        {
            var restore = app.Get<IRestoreService>();
            await Assert.ThrowsAsync<ValidationException>(() => restore.ScheduleAsync(backup, "wrong-password"));
            Assert.False(PendingRestore.IsPending(app.Paths));

            await app.AddCashierAsync();
            await app.SignInAsCashierAsync();
            await Assert.ThrowsAsync<PermissionDeniedException>(() => restore.ScheduleAsync(backup, TestApp.AdminPassword));
            await Assert.ThrowsAsync<PermissionDeniedException>(() => restore.PreviewAsync(backup));
            await Assert.ThrowsAsync<PermissionDeniedException>(() => restore.ListBackupsAsync());
            Assert.False(PendingRestore.IsPending(app.Paths));
        }
    }

    [Fact]
    public async Task A_bad_backup_cannot_be_scheduled()
    {
        var (app, _, _) = await AppWithBackupAsync();
        using (app)
        {
            var junk = Path.Combine(TempDir(), "billing-junk.db");
            await File.WriteAllTextAsync(junk, new string('x', 6000));
            await Assert.ThrowsAsync<BusinessRuleException>(() => app.Get<IRestoreService>().ScheduleAsync(junk, TestApp.AdminPassword));
            Assert.False(PendingRestore.IsPending(app.Paths));
        }
    }

    [Fact]
    public async Task A_scheduled_restore_is_applied_at_the_next_start_keeps_the_old_data_and_is_logged()
    {
        var (app, backup, _) = await AppWithBackupAsync();
        var dir = app.Dir;
        var paths = app.Paths;
        await app.Get<IRestoreService>().ScheduleAsync(backup, TestApp.AdminPassword);
        Assert.True(PendingRestore.IsPending(paths));
        Assert.Equal(2, BillCount(app));                                   // nothing changed yet

        app.Services.Dispose();                                            // "close the program"
        SqliteConnection.ClearAllPools();
        var (applied, message) = PendingRestore.TryApply(paths);
        Assert.True(applied, message);
        Assert.False(PendingRestore.IsPending(paths));
        Assert.False(Directory.Exists(PendingRestore.StagingDir(paths)));
        var safety = Directory.GetFiles(PendingRestore.SafetyDir(paths), "billing-before-restore-*.db");
        Assert.Single(safety);

        using var reopened = TestApp.Create(dir);                          // "start the program again"
        await reopened.SignInAsAdminAsync();
        Assert.Equal(1, BillCount(reopened));                              // back to the state at backup time
        Assert.True((await reopened.Get<IReportService>().TrialBalanceAsync(DateOnly.FromDateTime(DateTime.Today))).IsBalanced);
        Assert.True((await reopened.Get<IAuditService>().VerifyChainAsync()).Valid);
        await using var db = reopened.Get<DbFactory>().Create();
        Assert.Contains("restore.applied", db.AuditEvents.Select(e => e.Action).ToList());
        Assert.False(File.Exists(PendingRestore.AppliedPath(paths)));

        // the data from before the restore is still there in the safety copy, and opens with the same key
        using var oldConn = reopened.Get<DbFactory>().OpenRaw(safety[0]);
        using var cmd = oldConn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Sales";
        Assert.Equal(2L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public async Task A_restore_whose_staged_file_changed_or_vanished_changes_nothing()
    {
        var (app, backup, _) = await AppWithBackupAsync();
        var paths = app.Paths;
        await app.Get<IRestoreService>().ScheduleAsync(backup, TestApp.AdminPassword);
        var dir = app.Dir;
        app.Services.Dispose();
        SqliteConnection.ClearAllPools();

        await File.AppendAllTextAsync(PendingRestore.StagingFile(paths), "tampered");
        var (applied, message) = PendingRestore.TryApply(paths);
        Assert.False(applied);
        Assert.Contains("nothing was changed", message);
        Assert.False(PendingRestore.IsPending(paths));

        using var again = TestApp.Create(dir);
        await again.SignInAsAdminAsync();
        Assert.Equal(2, BillCount(again));                                 // today's data untouched

        // a marker with no staged file
        await File.WriteAllTextAsync(PendingRestore.MarkerPath(paths), "{\"RequestedUtc\":\"2026-01-01T00:00:00Z\",\"RequestedBy\":\"x\",\"SourceName\":\"x\",\"Sha256\":\"00\",\"KeyProtectedBase64\":null}");
        var (applied2, message2) = PendingRestore.TryApply(paths);
        Assert.False(applied2);
        Assert.Contains("missing", message2);
        // garbage marker
        await File.WriteAllTextAsync(PendingRestore.MarkerPath(paths), "not json");
        Assert.False(PendingRestore.TryApply(paths).Applied);
        Assert.False(PendingRestore.IsPending(paths));
    }

    [Fact]
    public void With_nothing_pending_starting_does_nothing()
    {
        var paths = new AppPaths(TempDir());
        var (applied, message) = PendingRestore.TryApply(paths);
        Assert.False(applied);
        Assert.Equal("", message);
    }

    [Fact]
    public async Task A_backup_from_another_computer_is_restored_with_its_recovery_key_on_a_new_one()
    {
        // computer A: some business data, a backup, and its recovery key
        string backup, recovery;
        using (var a = TestApp.Create())
        {
            var soap = await a.SeedAsync();
            await a.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(soap, 1000, 11_800));
            backup = (await a.Get<IBackupService>().BackupNowAsync(TempDir())).Path!;
            recovery = a.Startup.RecoveryKey!;
            File.Copy(backup, backup + ".copy");
        }
        var keepBackup = backup + ".copy";

        // computer B: an empty, freshly installed program
        var dirB = TempDir();
        var pathsB = new AppPaths(dirB);
        var hex = recovery.Replace("-", "");
        Assert.Null(PendingRestore.Problem(pathsB, keepBackup, hex));                 // opens with the recovery key
        Assert.NotNull(PendingRestore.Problem(pathsB, keepBackup, new string('A', 64)));   // not with a wrong key
        PendingRestore.Stage(pathsB, keepBackup, "setup", hex);
        var (applied, message) = PendingRestore.TryApply(pathsB);
        Assert.True(applied, message);

        using var b = TestApp.Create(dirB);
        await b.SignInAsAdminAsync();
        Assert.Equal(1, BillCount(b));
        Assert.True((await b.Get<IAuditService>().VerifyChainAsync()).Valid);
    }

    [Fact]
    public async Task Restoring_over_an_existing_database_with_another_computers_key_keeps_the_old_key_too()
    {
        string backup, recovery;
        using (var a = TestApp.Create())
        {
            await a.SeedAsync();
            backup = (await a.Get<IBackupService>().BackupNowAsync(TempDir())).Path!;
            recovery = a.Startup.RecoveryKey!;
        }
        using var b = TestApp.Create();                                            // computer B already has its own data and key
        await b.SeedAsync();
        var paths = b.Paths;
        var dir = b.Dir;
        b.Services.Dispose();
        SqliteConnection.ClearAllPools();
        PendingRestore.Stage(paths, backup, "setup", recovery.Replace("-", ""));
        var (applied, message) = PendingRestore.TryApply(paths);
        Assert.True(applied, message);
        Assert.Single(Directory.GetFiles(PendingRestore.SafetyDir(paths), "db.key.before-restore-*"));
        Assert.Single(Directory.GetFiles(PendingRestore.SafetyDir(paths), "billing-before-restore-*.db"));
        using var again = TestApp.Create(dir);                                    // opens with the installed key
        await again.SignInAsAdminAsync();
    }
}
