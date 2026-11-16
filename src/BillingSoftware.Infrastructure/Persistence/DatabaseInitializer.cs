using BillingSoftware.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BillingSoftware.Infrastructure.Persistence;

public sealed class DatabaseInitializer(DbFactory factory)
{
    public const int CurrentSchemaVersion = 12;

    /// <summary>
    /// Creates the encrypted database on first run (schema version 1) and seeds the standard chart of accounts.
    /// Later schema changes are applied as numbered, transactional scripts after this baseline.
    /// </summary>
    public async Task InitializeAsync()
    {
        await using var db = factory.Create();
        await db.Database.OpenConnectionAsync();

        // Guard against a build that silently lacks SQLCipher: without it the key pragma is ignored and data is plaintext.
        await using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText = "PRAGMA cipher_version;";
            var version = await cmd.ExecuteScalarAsync() as string;
            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidOperationException("Database encryption (SQLCipher) is not available in this build. Refusing to store data unencrypted.");
        }

        await db.Database.EnsureCreatedAsync();
        await AddMissingSchemaAsync(db);
        await EnsureColumnAsync(db, "Sales", "PlaceOfSupply", "TEXT NULL");        // schema version 3
        await EnsureColumnAsync(db, "Parties", "CreditLimitPaise", "INTEGER NOT NULL DEFAULT 0");   // schema version 8
        await EnsureColumnAsync(db, "Products", "TamilName", "TEXT NULL");                          // schema version 12
        await EnsureColumnAsync(db, "SaleLines", "TamilName", "TEXT NULL");
        await EnsureColumnAsync(db, "Companies", "GstEnabled", "INTEGER NOT NULL DEFAULT 1");        // schema version 11: GST is on unless turned off
        await EnsureColumnAsync(db, "Expenses", "SupplierGstin", "TEXT NULL");                      // schema version 9
        foreach (var col in new[] { "GstPaise", "CgstPaise", "SgstPaise", "IgstPaise" })
            await EnsureColumnAsync(db, "Expenses", col, "INTEGER NOT NULL DEFAULT 0");

        // Record each schema step once. Version 1 = baseline; version 2 = returns, suppliers and purchases.
        var descriptions = new Dictionary<int, string> { [1] = "Baseline schema", [2] = "Sales returns, suppliers, purchases, purchase returns", [3] = "GST reporting: place of supply on sales", [4] = "Cash drawer movements and day-end close", [5] = "Business expenses and categories", [6] = "Opening balances, fixed assets, loans", [7] = "Extra barcodes and repacking", [8] = "Customer credit limit", [9] = "Input GST credit on expenses", [10] = "Held bills", [11] = "GST can be turned off", [12] = "Tamil item names" };
        foreach (var (version, text) in descriptions)
            if (version <= CurrentSchemaVersion && !await db.SchemaVersions.AnyAsync(v => v.Version == version))
                db.SchemaVersions.Add(new SchemaVersion { Version = version, AppliedAtUtc = DateTime.UtcNow, Description = text });

        // Add any standard account that is missing (a database made by an earlier release gets the new accounts).
        var have = (await db.Accounts.Select(a => a.Code).ToListAsync()).ToHashSet();
        foreach (var (code, name, type) in AccountCodes.Standard)
            if (!have.Contains(code)) db.Accounts.Add(new Account { Code = code, Name = name, Type = type });

        // Starting expense categories, each with its own expense account (added when missing, so an older database gets them too).
        await db.SaveChangesAsync();
        var haveCategory = (await db.ExpenseCategories.Select(c => c.AccountCode).ToListAsync()).ToHashSet();
        var haveAccount = (await db.Accounts.Select(a => a.Code).ToListAsync()).ToHashSet();
        foreach (var (code, name) in ExpenseDefaults.Categories)
        {
            if (!haveAccount.Contains(code)) db.Accounts.Add(new Account { Code = code, Name = name, Type = AccountType.Expense });
            if (!haveCategory.Contains(code)) db.ExpenseCategories.Add(new ExpenseCategory { Name = name, AccountCode = code, IsDefault = true });
        }

        var defaults = new Dictionary<string, string>
        {
            [SettingKeys.CashierMaxDiscountBp] = "0",
            [SettingKeys.IdleLockMinutes] = "10",
        };
        foreach (var (key, value) in defaults)
            if (!await db.Settings.AnyAsync(s => s.Key == key))
                db.Settings.Add(new Setting { Key = key, Value = value });

        await db.SaveChangesAsync();
    }

    /// <summary>Adds a column to an existing table if it is not there yet (explicit, numbered schema steps; safe to repeat).</summary>
    private static async Task EnsureColumnAsync(AppDbContext db, string table, string column, string definition)
    {
        var conn = db.Database.GetDbConnection();
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            if (Convert.ToInt64(await check.ExecuteScalarAsync()) > 0) return;
        }
        await using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
        await alter.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Additive upgrade: creates any table or index the current model has that the database does not (all statements are
    /// IF NOT EXISTS, so it is safe on every start). It never changes existing tables; a changed column needs its own numbered script.
    /// </summary>
    private static async Task AddMissingSchemaAsync(AppDbContext db)
    {
        var script = db.Database.GenerateCreateScript().Replace("\r\n", "\n");
        await using var tx = await db.Database.BeginTransactionAsync();
        foreach (var raw in script.Split(";\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var stmt = raw.Trim();
            if (stmt.StartsWith("CREATE TABLE ", StringComparison.Ordinal)) stmt = "CREATE TABLE IF NOT EXISTS " + stmt["CREATE TABLE ".Length..];
            else if (stmt.StartsWith("CREATE UNIQUE INDEX ", StringComparison.Ordinal)) stmt = "CREATE UNIQUE INDEX IF NOT EXISTS " + stmt["CREATE UNIQUE INDEX ".Length..];
            else if (stmt.StartsWith("CREATE INDEX ", StringComparison.Ordinal)) stmt = "CREATE INDEX IF NOT EXISTS " + stmt["CREATE INDEX ".Length..];
            else continue;
            await using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.Transaction = tx.GetDbTransaction();
            cmd.CommandText = stmt;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }
}
