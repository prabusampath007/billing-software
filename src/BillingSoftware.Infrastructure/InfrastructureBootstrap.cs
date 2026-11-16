using BillingSoftware.Application;
using BillingSoftware.Infrastructure.Persistence;
using BillingSoftware.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BillingSoftware.Infrastructure;

public static class InfrastructureBootstrap
{
    /// <summary>What happened to a restore staged before this start (applied, or why it was not), for the UI to show once.</summary>
    public static string? RestoreMessage { get; private set; }

    /// <summary>The recovery key in readable form (shown at first run, and again on request after an Admin password check).</summary>
    public static string GetRecoveryKey(AppPaths paths) =>
        DbKeyProvider.FormatRecoveryKey(new DbKeyProvider(paths).GetOrCreateKey(out _));

    /// <summary>
    /// Opens (or creates) the encrypted database and registers all services.
    /// Returns a result the UI can show: first-run recovery key, missing-key recovery, or a startup error.
    /// </summary>
    public static StartupResult Start(AppPaths paths, IServiceCollection services, string? recoveryKeyForRestore = null)
    {
        var keys = new DbKeyProvider(paths);
        try
        {
            var restore = PendingRestore.TryApply(paths);
            if (restore.Message.Length > 0) RestoreMessage = restore.Message;
            if (!string.IsNullOrWhiteSpace(recoveryKeyForRestore)) keys.RestoreFromRecoveryKey(recoveryKeyForRestore);
            var keyHex = keys.GetOrCreateKey(out var created);
            var factory = new DbFactory(paths, keyHex);
            new DatabaseInitializer(factory).InitializeAsync().GetAwaiter().GetResult();
            PendingRestore.RecordAppliedAsync(factory, paths).GetAwaiter().GetResult();
            if (paths.IsStandardInstall) BackupLocation.ApplyDefaultAsync(factory, BackupLocation.ChooseForThisPc).GetAwaiter().GetResult();

            services.AddSingleton(paths);
            services.AddSingleton(factory);
            services.AddSingleton<ICurrentUser, CurrentUser>();
            services.AddSingleton<ApprovalLedger>();
            services.AddSingleton<IAuthService, AuthService>();
            services.AddSingleton<ICatalogService, CatalogService>();
            services.AddSingleton<IStockService, StockService>();
            services.AddSingleton<IPartyService, PartyService>();
            services.AddSingleton<IOpeningBalanceService, OpeningBalanceService>();
            services.AddSingleton<IBarcodeService, BarcodeService>();
            services.AddSingleton<ICreditService, CreditService>();
            services.AddSingleton<IRepackService, RepackService>();
            services.AddSingleton<ISalesService, SalesService>();
            services.AddSingleton<ISalesReturnService, SalesReturnService>();
            services.AddSingleton<ISupplierService, SupplierService>();
            services.AddSingleton<IPurchaseService, PurchaseService>();
            services.AddSingleton<IGstReportService, GstReportService>();
            services.AddSingleton<IDayCloseService, DayCloseService>();
            services.AddSingleton<IProfitLossService, ProfitLossService>();
            services.AddSingleton<IExpenseService, ExpenseService>();
            services.AddSingleton<IBalanceSheetService, BalanceSheetService>();
            services.AddSingleton<IReportService, ReportService>();
            services.AddSingleton<ISettingsService, SettingsService>();
            services.AddSingleton<IBackupService, BackupService>();
            services.AddSingleton<IRestoreService, RestoreService>();
            services.AddSingleton<IPeriodLockService, PeriodLockService>();
            services.AddSingleton<IHoldService, HoldService>();
            services.AddSingleton<IImportService, ImportService>();
            services.AddSingleton<IDiagnosticsService, DiagnosticsService>();
            services.AddSingleton<IAuditService, AuditService>();

            return new StartupResult(true, null, created ? DbKeyProvider.FormatRecoveryKey(keyHex) : null);
        }
        catch (KeyMissingException ex)
        {
            return new StartupResult(false, ex.Message, null, NeedsRecoveryKey: true);
        }
        catch (ArgumentException ex) when (!string.IsNullOrWhiteSpace(recoveryKeyForRestore))
        {
            return new StartupResult(false, ex.Message, null, NeedsRecoveryKey: true);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new StartupResult(false, "The database could not be opened: " + ex.Message, null);
        }
    }
}
