using System.Security.Cryptography;
using System.Text.Json;
using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

internal sealed class ReportService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), IReportService
{
    public async Task<DailySummary> DailySummaryAsync(DateOnly date)
    {
        RequireAdmin();
        var start = date.ToDateTime(TimeOnly.MinValue);
        var end = start.AddDays(1);
        await using var db = Factory.Create();
        var day = db.Sales.AsNoTracking().Where(s => s.SaleDate >= start && s.SaleDate < end);
        var posted = day.Where(s => s.Status == SaleStatus.Posted);

        var billCount = await posted.CountAsync();
        var voided = await day.CountAsync(s => s.Status == SaleStatus.Voided);
        long Sum(long? v) => v ?? 0;

        var taxable = Sum(await posted.SumAsync(s => (long?)s.TaxablePaise));
        var cgst = Sum(await posted.SumAsync(s => (long?)s.CgstPaise));
        var sgst = Sum(await posted.SumAsync(s => (long?)s.SgstPaise));
        var igst = Sum(await posted.SumAsync(s => (long?)s.IgstPaise));
        var discount = Sum(await posted.SumAsync(s => (long?)s.DiscountPaise));
        var roundOff = Sum(await posted.SumAsync(s => (long?)s.RoundOffPaise));
        var total = Sum(await posted.SumAsync(s => (long?)s.TotalPaise));

        var byMode = await (from p in db.SalePayments.AsNoTracking()
                            join s in posted on p.SaleId equals s.Id
                            group p by p.Mode into g
                            select new { Mode = g.Key, Amount = g.Sum(x => x.AmountPaise) }).ToListAsync();

        var byCashier = await (from s in posted
                               join u in db.Users on s.CashierId equals u.Id
                               group s by u.DisplayName into g
                               select new { Cashier = g.Key, Bills = g.Count(), Total = g.Sum(x => x.TotalPaise) }).ToListAsync();

        // Money handed back for returns reduces the matching payment mode (a refund to the customer's account reduces credit).
        var dayReturns = db.SaleReturns.AsNoTracking().Where(r => r.Date >= start && r.Date < end);
        var returnCount = await dayReturns.CountAsync();
        var returnTotal = Sum(await dayReturns.SumAsync(r => (long?)r.TotalPaise));
        var refunds = await dayReturns.GroupBy(r => r.RefundMode).Select(g => new { Mode = g.Key, Amount = g.Sum(x => x.TotalPaise) }).ToListAsync();
        var net = byMode.ToDictionary(x => x.Mode, x => x.Amount);
        foreach (var r in refunds) net[r.Mode] = net.GetValueOrDefault(r.Mode) - r.Amount;

        var dayPurchases = db.Purchases.AsNoTracking().Where(p => p.PurchaseDate >= start && p.PurchaseDate < end);
        var purchaseCount = await dayPurchases.CountAsync();
        var purchaseTotal = Sum(await dayPurchases.SumAsync(p => (long?)p.TotalPaise));

        return new DailySummary(date, billCount, voided, taxable, cgst, sgst, igst, discount, roundOff, total,
            net.OrderBy(x => x.Key).Select(x => (x.Key, x.Value)).ToList(),
            byCashier.OrderByDescending(x => x.Total).Select(x => (x.Cashier, x.Bills, x.Total)).ToList(),
            returnCount, returnTotal, purchaseCount, purchaseTotal);
    }

    public async Task<TrialBalance> TrialBalanceAsync(DateOnly asOf)
    {
        RequireAdmin();
        var end = asOf.ToDateTime(TimeOnly.MinValue).AddDays(1);
        await using var db = Factory.Create();
        var sums = await (from l in db.JournalLines.AsNoTracking()
                          join j in db.Journals on l.JournalId equals j.Id
                          where j.Date < end
                          group l by l.AccountId into g
                          select new { AccountId = g.Key, Debit = g.Sum(x => x.DebitPaise), Credit = g.Sum(x => x.CreditPaise) }).ToListAsync();
        var accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.Code).ToListAsync();
        var rows = new List<TrialBalanceRow>();
        foreach (var a in accounts)
        {
            var s = sums.FirstOrDefault(x => x.AccountId == a.Id);
            var net = (s?.Debit ?? 0) - (s?.Credit ?? 0);
            if (s == null) continue;
            rows.Add(new TrialBalanceRow(a.Code, a.Name, net > 0 ? net : 0, net < 0 ? -net : 0));
        }
        return new TrialBalance(asOf, rows, rows.Sum(r => r.DebitPaise), rows.Sum(r => r.CreditPaise));
    }

    public async Task<IReadOnlyList<StockRow>> StockValuationAsync()
    {
        RequireAdmin();
        await using var db = Factory.Create();
        var rows = await db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).ToListAsync();
        return rows.Select(p => new StockRow(p.Id, p.Sku, p.Name, p.Unit, p.QtyOnHandMilli, p.AvgCostPaise, Costing.Value(p.QtyOnHandMilli, p.AvgCostPaise))).ToList();
    }
}

internal sealed class SettingsService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), ISettingsService
{
    public async Task<string?> GetAsync(string key)
    {
        await using var db = Factory.Create();
        return await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync();
    }

    public async Task SetAsync(string key, string value)
    {
        var admin = RequireAdmin();
        await using var db = Factory.Create();
        await SetCoreAsync(db, key, value);
        await AuditWriter.AppendAsync(db, admin, "setting.changed", "Setting", null, $"{key} = {value}");
    }

    internal static async Task SetCoreAsync(AppDbContext db, string key, string value)
    {
        var s = await db.Settings.FindAsync(key);
        if (s == null) db.Settings.Add(new Setting { Key = key, Value = value }); else s.Value = value;
        await db.SaveChangesAsync();
    }

    public async Task<CompanyDto?> GetCompanyAsync()
    {
        await using var db = Factory.Create();
        var c = await db.Companies.AsNoTracking().FirstOrDefaultAsync();
        return c == null ? null : new CompanyDto(c.Name, c.Gstin, c.StateCode, c.IsComposition, c.Address, c.Phone, c.GstEnabled);
    }

    public async Task SaveCompanyAsync(CompanyDto dto)
    {
        await using var db = Factory.Create();
        var existing = await db.Companies.FirstOrDefaultAsync();
        // First-run setup is allowed before any user exists; afterwards only the Admin may change it.
        var admin = existing == null && !await db.Users.AnyAsync() ? null : RequireAdmin();

        var name = (dto.Name ?? "").Trim();
        if (name.Length == 0) throw new ValidationException("Business name is required.");
        var state = (dto.StateCode ?? "").Trim();
        if (state.Length != 2 || !state.All(char.IsDigit)) throw new ValidationException("State code must be 2 digits (for example 33 for Tamil Nadu).");
        var gstin = string.IsNullOrWhiteSpace(dto.Gstin) ? null : dto.Gstin.Trim().ToUpperInvariant();
        if (gstin != null)
        {
            if (!GstValidation.IsValidGstin(gstin)) throw new ValidationException("The GSTIN format is not valid.");
            if (!gstin.StartsWith(state, StringComparison.Ordinal)) throw new ValidationException("The first two digits of the GSTIN must match the state code.");
        }

        var c = existing ?? new Company();
        if (existing == null) db.Companies.Add(c);
        c.Name = name; c.Gstin = gstin; c.StateCode = state; c.IsComposition = dto.IsComposition; c.GstEnabled = dto.GstEnabled;
        c.Address = dto.Address?.Trim(); c.Phone = dto.Phone?.Trim();
        await db.SaveChangesAsync();
        await AuditWriter.AppendAsync(db, admin, "company.saved", "Company", c.Id, $"gst={(c.GstEnabled ? "on" : "off")}, composition={c.IsComposition}");
    }
}

internal sealed class AuditService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), IAuditService
{
    public async Task<(bool Valid, long? FirstBrokenId)> VerifyChainAsync()
    {
        RequireAdmin();
        await using var db = Factory.Create();
        var prev = "";
        await foreach (var e in db.AuditEvents.AsNoTracking().OrderBy(a => a.Id).AsAsyncEnumerable())
        {
            if (e.PrevHash != prev || e.Hash != AuditWriter.ComputeHash(e)) return (false, e.Id);
            prev = e.Hash;
        }
        return (true, null);
    }
}

internal sealed class BackupService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), IBackupService
{
    private const string Prefix = "billing-";

    public async Task<BackupResult> BackupNowAsync(string? folder = null)
    {
        var user = RequireUser();
        try
        {
            await using var db = Factory.Create();
            folder ??= await db.Settings.Where(s => s.Key == SettingKeys.BackupFolder).Select(s => s.Value).FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(folder))
                return new BackupResult(false, null, null, "No backup folder is set. Choose one in Settings.");

            Directory.CreateDirectory(folder);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var target = Path.Combine(folder, $"{Prefix}{stamp}.db");
            if (File.Exists(target)) target = Path.Combine(folder, $"{Prefix}{stamp}-{Guid.NewGuid():N}.db");

            // SQLite online backup API: safe while the app is running (never a plain file copy of a live database).
            await Task.Run(() =>
            {
                using var source = Factory.OpenRaw(Factory.Paths.DbPath);
                using var dest = Factory.OpenRaw(target);
                source.BackupDatabase(dest);
            });

            // Verify the copy: integrity check on the backup, checksum, manifest.
            string integrity;
            using (var check = Factory.OpenRaw(target))
            using (var cmd = check.CreateCommand())
            {
                cmd.CommandText = "PRAGMA integrity_check;";
                integrity = (cmd.ExecuteScalar() as string) ?? "unknown";
            }
            if (integrity != "ok")
            {
                File.Delete(target);
                return new BackupResult(false, null, null, $"The backup failed its integrity check ({integrity}) and was discarded.");
            }

            string checksum;
            await using (var fs = File.OpenRead(target))
                checksum = Convert.ToHexString(await SHA256.HashDataAsync(fs));
            var manifest = new
            {
                file = Path.GetFileName(target), createdUtc = DateTime.UtcNow, schemaVersion = DatabaseInitializer.CurrentSchemaVersion,
                appVersion = typeof(BackupService).Assembly.GetName().Version?.ToString(), sha256 = checksum, bytes = new FileInfo(target).Length,
            };
            await File.WriteAllTextAsync(target + ".manifest.json", JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            await SettingsService.SetCoreAsync(db, SettingKeys.LastBackupUtc, DateTime.UtcNow.ToString("O"));
            await AuditWriter.AppendAsync(db, user, "backup.created", "Backup", null, $"{Path.GetFileName(target)} sha256 {checksum[..12]}");
            PruneOld(folder);
            return new BackupResult(true, target, checksum, "Backup completed and verified.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return new BackupResult(false, null, null, "Backup failed: " + ex.Message);
        }
    }

    public async Task<BackupStatus> GetStatusAsync()
    {
        await using var db = Factory.Create();
        var folder = await db.Settings.AsNoTracking().Where(s => s.Key == SettingKeys.BackupFolder).Select(s => s.Value).FirstOrDefaultAsync();
        var lastText = await db.Settings.AsNoTracking().Where(s => s.Key == SettingKeys.LastBackupUtc).Select(s => s.Value).FirstOrDefaultAsync();
        DateTime? last = DateTime.TryParse(lastText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

        var onSystem = false;
        if (!string.IsNullOrWhiteSpace(folder))
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            var sysRoot = Path.GetPathRoot(Environment.SystemDirectory);
            onSystem = string.Equals(root, sysRoot, StringComparison.OrdinalIgnoreCase);
        }
        var overdue = last == null || DateTime.UtcNow - last > TimeSpan.FromHours(24);
        var summary = string.IsNullOrWhiteSpace(folder) ? "No backup folder is set."
            : last == null ? "No backup has been made yet."
            : $"Last backup {last.Value.ToLocalTime():dd-MMM-yyyy HH:mm}.";
        return new BackupStatus(folder, last, onSystem, overdue, summary);
    }

    /// <summary>Retention (default 7 daily / 4 weekly / 12 monthly): today keeps every intra-day backup.</summary>
    private static void PruneOld(string folder)
    {
        var files = Directory.GetFiles(folder, Prefix + "*.db")
            .Select(f => new { Path = f, Time = File.GetCreationTime(f) }).OrderByDescending(f => f.Time).ToList();
        var today = DateTime.Today;
        var keep = new HashSet<string>();
        foreach (var f in files.Where(f => f.Time.Date == today)) keep.Add(f.Path);
        foreach (var g in files.GroupBy(f => f.Time.Date).Where(g => g.Key >= today.AddDays(-7))) keep.Add(g.First().Path);
        foreach (var g in files.GroupBy(f => System.Globalization.ISOWeek.GetYear(f.Time) * 100 + System.Globalization.ISOWeek.GetWeekOfYear(f.Time))
                     .Where(g => g.First().Time >= today.AddDays(-28))) keep.Add(g.First().Path);
        foreach (var g in files.GroupBy(f => f.Time.Year * 100 + f.Time.Month).Where(g => g.First().Time >= today.AddMonths(-12))) keep.Add(g.First().Path);
        foreach (var f in files.Where(f => !keep.Contains(f.Path)))
        {
            try { File.Delete(f.Path); File.Delete(f.Path + ".manifest.json"); } catch (IOException) { /* locked: try next time */ }
        }
    }
}
