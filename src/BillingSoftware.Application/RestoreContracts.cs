namespace BillingSoftware.Application;

/// <summary>A backup file found in the backup folder.</summary>
public sealed record BackupFileInfo(string Path, string FileName, DateTime? CreatedLocal, long Bytes, bool ManifestFound);

/// <summary>What a backup holds and whether it is safe to restore, shown to the owner before anything changes.</summary>
public sealed record RestorePreview(
    string Path, bool CanRestore, string Message, bool ChecksumVerified,
    DateTime? BackupTakenLocal, int SchemaVersion, string CompanyName,
    int BillCount, string? LastBillNumber, DateTime? LastBillDate,
    int CurrentBillCount, string? CurrentLastBillNumber, DateTime? CurrentLastBillDate,
    IReadOnlyList<string> Warnings);

public interface IRestoreService
{
    /// <summary>Backups in the configured backup folder (or the folder given), newest first.</summary>
    Task<IReadOnlyList<BackupFileInfo>> ListBackupsAsync(string? folder = null);
    /// <summary>Checks a backup (integrity, checksum, books balance, version) and compares it with today's data. Changes nothing.</summary>
    Task<RestorePreview> PreviewAsync(string backupFile);
    /// <summary>
    /// Admin only, needs the Admin password. Checks the backup again, stages a safe copy and asks for the restore to happen the next time the
    /// program starts (the caller then restarts it). The current data is kept as a safety copy before anything is replaced.
    /// </summary>
    Task ScheduleAsync(string backupFile, string adminPassword);
}
