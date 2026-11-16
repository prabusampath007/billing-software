using System.IO.Compression;
using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BillingSoftware.Tests;

public class DiagnosticsTests : IDisposable
{
    private readonly List<string> _files = new();
    private string Temp(string name) { var p = Path.Combine(Path.GetTempPath(), $"diag-{Guid.NewGuid():N}-{name}"); _files.Add(p); return p; }
    public void Dispose() { SqliteConnection.ClearAllPools(); foreach (var f in _files) { try { File.Delete(f); } catch (IOException) { } } }

    // ------------------------------------------------------------------ amount in words

    [Theory]
    [InlineData(0, "Rupees Zero Only")]
    [InlineData(11_800, "Rupees One Hundred Eighteen Only")]
    [InlineData(100_000, "Rupees One Thousand Only")]
    [InlineData(2_100, "Rupees Twenty One Only")]
    [InlineData(25_000_050, "Rupees Two Lakh Fifty Thousand and Fifty Paise Only")]
    [InlineData(1_000_000_000, "Rupees One Crore Only")]
    [InlineData(99, "Ninety Nine Paise Only")]
    [InlineData(10_001_00, "Rupees Ten Thousand One Only")]
    [InlineData(1_00_000_00, "Rupees One Lakh Only")]
    [InlineData(1_23_45_678_90, "Rupees One Crore Twenty Three Lakh Forty Five Thousand Six Hundred Seventy Eight and Ninety Paise Only")]
    [InlineData(100_00_00_000_00, "Rupees One Hundred Crore Only")]
    [InlineData(-11_800, "Minus Rupees One Hundred Eighteen Only")]
    public void Amounts_are_written_out_the_Indian_way(long paise, string expected) => Assert.Equal(expected, AmountInWords.Rupees(paise));

    // ------------------------------------------------------------------ health check

    [Fact]
    public async Task A_healthy_installation_passes_and_a_missing_backup_is_a_warning()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale((await app.Get<ICatalogService>().SearchAsync("SKU1")).Single().Id, 1000, 11_800));
        var checks = await app.Get<IDiagnosticsService>().RunAsync();

        CheckLevel Level(string name) => checks.Single(c => c.Name == name).Level;
        Assert.Equal(CheckLevel.Ok, Level("Database"));
        Assert.Equal(CheckLevel.Ok, Level("Books"));
        Assert.Equal(CheckLevel.Ok, Level("Database version"));
        Assert.Equal(CheckLevel.Ok, Level("Activity log"));
        Assert.Equal(CheckLevel.Ok, Level("Data folder"));
        Assert.Equal(CheckLevel.Warning, Level("Backups"));
        Assert.Contains("No backup folder", checks.Single(c => c.Name == "Backups").Message);
        Assert.DoesNotContain(checks, c => c.Level == CheckLevel.Problem);
    }

    [Fact]
    public async Task A_changed_activity_log_and_a_pending_restore_are_reported()
    {
        var app = TestApp.Create();
        await app.SeedAsync();
        var factory = app.Get<DbFactory>();
        var path = app.Paths.DbPath;
        SqliteConnection.ClearAllPools();
        using (var raw = factory.OpenRaw(path))
        using (var cmd = raw.CreateCommand()) { cmd.CommandText = "UPDATE AuditEvents SET Details = 'edited' WHERE Id = 2"; cmd.ExecuteNonQuery(); }
        await File.WriteAllTextAsync(PendingRestore.MarkerPath(app.Paths), "{}");
        using (app)
        {
            var checks = await app.Get<IDiagnosticsService>().RunAsync();
            Assert.Equal(CheckLevel.Problem, checks.Single(c => c.Name == "Activity log").Level);
            Assert.Equal(CheckLevel.Warning, checks.Single(c => c.Name == "Restore").Level);
        }
    }

    [Fact]
    public async Task After_a_backup_the_backup_check_shows_the_time()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var folder = Temp("backups");
        await app.Get<ISettingsService>().SetAsync(SettingKeys.BackupFolder, folder);
        Assert.True((await app.Get<IBackupService>().BackupNowAsync()).Success);
        var b = (await app.Get<IDiagnosticsService>().RunAsync()).Single(c => c.Name == "Backups");
        Assert.Contains("Last backup", b.Message);
        Directory.Delete(folder, true);
    }

    [Fact]
    public async Task The_locked_period_is_shown()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<IPeriodLockService>().SetAsync(Helpers.Today.AddDays(-9), TestApp.AdminPassword, null);
        Assert.Contains("locked", (await app.Get<IDiagnosticsService>().RunAsync()).Single(c => c.Name == "Locked period").Message);
    }

    // ------------------------------------------------------------------ support file

    [Fact]
    public async Task The_support_file_has_the_checks_versions_counts_and_logs_but_no_customer_data_or_keys()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<IPartyService>().SaveAsync(new PartyDto(0, "Secret Customer Name", "9876501234", null, "33", 0));
        File.WriteAllText(Path.Combine(app.Paths.LogsDir, "log-test.json"), "{\"message\":\"hello\"}");
        var zipPath = Temp("support.zip");
        await app.Get<IDiagnosticsService>().CreateSupportFileAsync(zipPath);

        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("health-check.txt", names);
        Assert.Contains("environment.txt", names);
        Assert.Contains("record-counts.txt", names);
        Assert.Contains("README.txt", names);
        Assert.Contains("logs/log-test.json", names);
        Assert.DoesNotContain(names, n => n.EndsWith(".db") || n.Contains("key", StringComparison.OrdinalIgnoreCase));

        var all = string.Concat(zip.Entries.Where(e => !e.FullName.EndsWith(".db")).Select(e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); }));
        Assert.DoesNotContain("Secret Customer Name", all);
        Assert.DoesNotContain("9876501234", all);
        Assert.DoesNotContain(TestApp.AdminPassword, all);
        Assert.DoesNotContain(app.Startup.RecoveryKey!.Replace("-", ""), all.Replace("-", ""));
        Assert.Contains("Customers: 1", all);
        Assert.Contains("Database version: " + DatabaseInitializer.CurrentSchemaVersion, all);
        Assert.False(File.Exists(zipPath + ".part"));
    }

    [Fact]
    public async Task A_support_file_that_cannot_be_saved_is_explained_and_only_the_Admin_can_make_one()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var svc = app.Get<IDiagnosticsService>();
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.CreateSupportFileAsync(Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid().ToString("N"), "x.zip")));
        await Assert.ThrowsAsync<ValidationException>(() => svc.CreateSupportFileAsync(" "));
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => svc.RunAsync());
        await Assert.ThrowsAsync<PermissionDeniedException>(() => svc.CreateSupportFileAsync(Temp("x.zip")));
    }
}
