using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Xunit;

namespace BillingSoftware.Tests;

public class BackupLocationTests
{
    private const long GB = 1024L * 1024 * 1024;
    private const string Shared = @"C:\Users\Public\Documents";

    private static string Choose(params DriveCandidate[] drives) => BackupLocation.Choose(drives, @"C:\", Shared);

    private static readonly string SharedFolder = Path.Combine(Shared, "BillingSoftware Backups");

    [Fact]
    public void The_C_drive_shared_Documents_folder_is_always_the_first_choice_even_with_other_disks()
    {
        Assert.Equal(SharedFolder, Choose());
        Assert.Equal(SharedFolder, Choose(new DriveCandidate(@"C:\", true, true, 100 * GB)));
        Assert.Equal(SharedFolder, Choose(new DriveCandidate(@"C:\", true, true, 100 * GB), new DriveCandidate(@"D:\", true, true, 900 * GB)));
    }

    [Fact]
    public void Other_fixed_disks_are_only_fallbacks_biggest_free_space_first()
    {
        var list = BackupLocation.Candidates(new[] { new DriveCandidate(@"D:\", true, true, 10 * GB), new DriveCandidate(@"E:\", true, true, 300 * GB), new DriveCandidate(@"C:\", true, true, 900 * GB) }, @"C:\", Shared);
        Assert.Equal(new[] { SharedFolder, @"E:\BillingSoftware Backups", @"D:\BillingSoftware Backups" }, list.ToArray());
    }

    [Fact]
    public void Removable_not_ready_and_nearly_full_disks_are_never_fallbacks()
    {
        var list = BackupLocation.Candidates(new[]
        {
            new DriveCandidate(@"D:\", false, true, 500 * GB),       // a USB stick: it may be unplugged tomorrow
            new DriveCandidate(@"E:\", true, false, 500 * GB),       // not ready
            new DriveCandidate(@"F:\", true, true, 1 * GB),          // under 2 GB free
        }, @"C:\", Shared);
        Assert.Equal(new[] { SharedFolder }, list.ToArray());
    }

    [Fact]
    public void The_system_disk_is_matched_without_regard_to_letter_case_and_is_not_listed_twice()
    {
        Assert.Equal(new[] { SharedFolder }, BackupLocation.Candidates(new[] { new DriveCandidate(@"c:\", true, true, 500 * GB) }, @"C:\", Shared).ToArray());
    }

    [Fact]
    public async Task A_new_installation_gets_the_default_folder_with_a_note_in_it_and_it_is_logged()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var folder = Path.Combine(Path.GetTempPath(), "bl-" + Guid.NewGuid().ToString("N"), "BillingSoftware Backups");
        try
        {
            Assert.Null(await app.Get<ISettingsService>().GetAsync(SettingKeys.BackupFolder));
            await BackupLocation.ApplyDefaultAsync(app.Get<DbFactory>(), () => folder);
            Assert.Equal(folder, await app.Get<ISettingsService>().GetAsync(SettingKeys.BackupFolder));
            Assert.True(File.Exists(Path.Combine(folder, "READ ME FIRST.txt")));
            Assert.Contains("RECOVERY KEY", await File.ReadAllTextAsync(Path.Combine(folder, "READ ME FIRST.txt")));
            await using (var db = app.Get<DbFactory>().Create())
                Assert.Contains("backup.folder_defaulted", db.AuditEvents.Select(e => e.Action).ToList());

            // backups now work with no further setup
            var r = await app.Get<IBackupService>().BackupNowAsync();
            Assert.True(r.Success, r.Message);
            Assert.StartsWith(folder, r.Path);
            Assert.False((await app.Get<IBackupService>().GetStatusAsync()).Overdue);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(folder)!, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task A_folder_the_owner_chose_is_never_replaced_and_no_usable_folder_changes_nothing()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.Get<ISettingsService>().SetAsync(SettingKeys.BackupFolder, @"E:\My Backups");
        await BackupLocation.ApplyDefaultAsync(app.Get<DbFactory>(), () => @"C:\should-not-be-used");
        Assert.Equal(@"E:\My Backups", await app.Get<ISettingsService>().GetAsync(SettingKeys.BackupFolder));

        using var other = TestApp.Create();
        await other.SeedAsync();
        await BackupLocation.ApplyDefaultAsync(other.Get<DbFactory>(), () => null);
        Assert.Null(await other.Get<ISettingsService>().GetAsync(SettingKeys.BackupFolder));
    }

    [Fact]
    public void Only_the_standard_data_folder_counts_as_an_installation()
    {
        Assert.False(new AppPaths(Path.Combine(Path.GetTempPath(), "bl-" + Guid.NewGuid().ToString("N"))).IsStandardInstall);
    }

    [Fact]
    public void The_real_choice_for_this_PC_is_the_shared_Documents_folder_on_the_Windows_drive()
    {
        var folder = BackupLocation.ChooseForThisPc();
        Assert.NotNull(folder);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "BillingSoftware Backups"), folder);
        Assert.StartsWith(Path.GetPathRoot(Environment.SystemDirectory)!, folder, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(folder));
        Assert.DoesNotContain("ProgramData", folder, StringComparison.OrdinalIgnoreCase);
    }
}
