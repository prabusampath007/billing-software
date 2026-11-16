using BillingSoftware.Domain;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Persistence;

/// <summary>A disk the program could keep backups on.</summary>
public sealed record DriveCandidate(string Root, bool IsFixed, bool IsReady, long FreeBytes);

/// <summary>
/// Chooses where backups go when nobody has chosen: first the shared Documents folder on the Windows (C:) drive, which is easy to find, writable by
/// every user, outside the program's own data folder, and is left alone when the program is uninstalled or Windows restarts. Only if that cannot be
/// used does it fall back to another fixed hard disk in the PC.
/// </summary>
public static class BackupLocation
{
    public const string FolderName = "BillingSoftware Backups";
    private const long MinFreeBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>The folders to try, best first: the shared Documents folder (C:), then other fixed disks with room, the one with most free space first. The pure rule, so it can be checked without real disks.</summary>
    public static IReadOnlyList<string> Candidates(IEnumerable<DriveCandidate> drives, string systemRoot, string sharedDocuments)
    {
        var list = new List<string> { Path.Combine(sharedDocuments, FolderName) };
        list.AddRange(drives
            .Where(d => d.IsFixed && d.IsReady && d.FreeBytes >= MinFreeBytes && !string.Equals(d.Root, systemRoot, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.FreeBytes)
            .Select(d => Path.Combine(d.Root, FolderName)));
        return list;
    }

    /// <summary>The first choice.</summary>
    public static string Choose(IEnumerable<DriveCandidate> drives, string systemRoot, string sharedDocuments) => Candidates(drives, systemRoot, sharedDocuments)[0];

    /// <summary>Looks at the disks in this PC. Returns the first folder from <see cref="Candidates"/> that can be created and written to, or null.</summary>
    public static string? ChooseForThisPc()
    {
        var shared = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        var drives = new List<DriveCandidate>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try { drives.Add(new DriveCandidate(d.RootDirectory.FullName, d.DriveType == DriveType.Fixed, d.IsReady, d.IsReady ? d.AvailableFreeSpace : 0)); }
                catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        foreach (var candidate in Candidates(drives, system, shared))
            if (CanWrite(candidate)) return candidate;
        return null;
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>A note left in the backup folder so whoever finds it later knows what the files are and how to use them.</summary>
    public static void WriteReadMe(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "READ ME FIRST.txt");
            if (File.Exists(path)) return;
            File.WriteAllText(path,
                "These are the automatic backups made by Billing Software.\r\n\r\n" +
                "- Each file named billing-<date>-<time>.db is a complete copy of the shop's data (bills, items, customers, accounts).\r\n" +
                "- The files are encrypted. They can only be opened by Billing Software, and on another computer only with the RECOVERY KEY\r\n" +
                "  that was shown when the software was first set up. Keep that key somewhere safe, away from this computer.\r\n" +
                "- The .manifest.json next to each file is its checksum; it lets the program prove the copy is not damaged.\r\n" +
                "- To bring a backup back: in Billing Software open Settings, Restore from a backup. On a new computer, choose\r\n" +
                "  \"I already have a backup\" on the first screen and give the file and the recovery key.\r\n" +
                "- Copy this whole folder to a USB drive or another computer regularly. Backups kept only on this computer do not protect\r\n" +
                "  against the computer being lost, stolen or its disk failing.\r\n" +
                "- Old backups are removed automatically (about a week of daily copies, a month of weekly copies, a year of monthly copies).\r\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the note is a courtesy */ }
    }

    /// <summary>
    /// First start on a standard install: if no backup folder has been chosen, use the default one so backups work from day one.
    /// <paramref name="choose"/> finds the folder (a test can pass its own).
    /// </summary>
    public static async Task ApplyDefaultAsync(DbFactory factory, Func<string?> choose)
    {
        await using var db = factory.Create();
        var current = await db.Settings.AsNoTracking().Where(s => s.Key == SettingKeys.BackupFolder).Select(s => s.Value).FirstOrDefaultAsync();
        if (!string.IsNullOrWhiteSpace(current)) return;
        var folder = choose();
        if (folder == null) return;
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        WriteReadMe(folder);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingKeys.BackupFolder);
        if (row == null) db.Settings.Add(new Setting { Key = SettingKeys.BackupFolder, Value = folder }); else row.Value = folder;
        await db.SaveChangesAsync();
        await Services.AuditWriter.AppendAsync(db, null, "backup.folder_defaulted", "Setting", null, $"No backup folder was chosen, so {folder} is used");
    }
}
