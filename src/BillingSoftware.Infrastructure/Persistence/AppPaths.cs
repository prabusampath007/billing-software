namespace BillingSoftware.Infrastructure.Persistence;

/// <summary>Where mutable data lives: separate from the program binaries (plan section 10).</summary>
public sealed class AppPaths
{
    public string DataDir { get; }
    public string DbPath => Path.Combine(DataDir, "billing.db");
    public string KeyPath => Path.Combine(DataDir, "keys", "db.key");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string DefaultBackupDir => Path.Combine(DataDir, "backups");
    /// <summary>True for a real installation (the standard data folder), false when the folder was chosen on purpose (tests, the self-test, a support check).</summary>
    public bool IsStandardInstall { get; }

    public AppPaths(string dataDir, bool isStandardInstall = false)
    {
        DataDir = dataDir;
        IsStandardInstall = isStandardInstall;
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        Directory.CreateDirectory(LogsDir);
    }

    /// <summary>ProgramData\BillingSoftware if writable (installer sets ACLs), else the user's local app data.</summary>
    public static AppPaths ResolveDefault()
    {
        var overrideDir = Environment.GetEnvironmentVariable("BILLINGSOFTWARE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir)) return new AppPaths(overrideDir);

        var programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BillingSoftware");
        try
        {
            Directory.CreateDirectory(programData);
            var probe = Path.Combine(programData, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return new AppPaths(programData, isStandardInstall: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new AppPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BillingSoftware"), isStandardInstall: true);
        }
    }
}
