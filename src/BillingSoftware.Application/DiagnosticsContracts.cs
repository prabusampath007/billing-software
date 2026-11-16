namespace BillingSoftware.Application;

public enum CheckLevel { Ok = 1, Warning = 2, Problem = 3 }

public sealed record HealthCheck(string Name, CheckLevel Level, string Message);

public interface IDiagnosticsService
{
    /// <summary>Checks the installation: database, books, activity log, backups, disk space and folders. Reads only; changes nothing.</summary>
    Task<IReadOnlyList<HealthCheck>> RunAsync();
    /// <summary>
    /// Writes one zip file the owner can send to support: the health check, versions, record counts and recent log files.
    /// It holds no bills, customer details, passwords, database key or recovery key.
    /// </summary>
    Task CreateSupportFileAsync(string zipPath);
}
