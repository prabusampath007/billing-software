using System.Data.Common;
using BillingSoftware.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BillingSoftware.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<FinancialYear> FinancialYears => Set<FinancialYear>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Party> Parties => Set<Party>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleLine> SaleLines => Set<SaleLine>();
    public DbSet<SalePayment> SalePayments => Set<SalePayment>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Journal> Journals => Set<Journal>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<SchemaVersion> SchemaVersions => Set<SchemaVersion>();
    public DbSet<SaleReturn> SaleReturns => Set<SaleReturn>();
    public DbSet<SaleReturnLine> SaleReturnLines => Set<SaleReturnLine>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<PurchaseLine> PurchaseLines => Set<PurchaseLine>();
    public DbSet<PurchasePayment> PurchasePayments => Set<PurchasePayment>();
    public DbSet<SupplierPayment> SupplierPayments => Set<SupplierPayment>();
    public DbSet<PurchaseReturn> PurchaseReturns => Set<PurchaseReturn>();
    public DbSet<PurchaseReturnLine> PurchaseReturnLines => Set<PurchaseReturnLine>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<DayClose> DayCloses => Set<DayClose>();
    public DbSet<ExpenseCategory> ExpenseCategories => Set<ExpenseCategory>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<ExpensePayment> ExpensePayments => Set<ExpensePayment>();
    public DbSet<OpeningBalance> OpeningBalances => Set<OpeningBalance>();
    public DbSet<ProductBarcode> ProductBarcodes => Set<ProductBarcode>();
    public DbSet<Repack> Repacks => Set<Repack>();
    public DbSet<HeldBill> HeldBills => Set<HeldBill>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.Property(x => x.Username).UseCollation("NOCASE");
            e.HasIndex(x => x.Username).IsUnique();
        });

        b.Entity<Product>(e =>
        {
            e.HasIndex(x => x.Sku).IsUnique();
            e.HasIndex(x => x.Barcode).IsUnique().HasFilter("\"Barcode\" IS NOT NULL");
            e.HasIndex(x => x.Name);
            e.ToTable(t => t.HasCheckConstraint("CK_Product_Values",
                "\"SellingPricePaise\" >= 0 AND \"GstRateBp\" >= 0 AND \"GstRateBp\" <= 10000 AND \"AvgCostPaise\" >= 0"));
        });

        b.Entity<Sale>(e =>
        {
            e.HasIndex(x => x.InvoiceNumber).IsUnique();
            e.HasIndex(x => x.CommandId).IsUnique();
            e.HasIndex(x => x.SaleDate);
            e.HasIndex(x => new { x.PartyId, x.Status });
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.SaleId);
            e.HasMany(x => x.Payments).WithOne().HasForeignKey(p => p.SaleId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CashierId);
            e.HasOne<FinancialYear>().WithMany().HasForeignKey(x => x.FinancialYearId);
        });

        b.Entity<SaleLine>(e =>
        {
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId);
            e.ToTable(t => t.HasCheckConstraint("CK_SaleLine_Qty", "\"QtyMilli\" > 0"));
        });

        b.Entity<SalePayment>(e => e.ToTable(t => t.HasCheckConstraint("CK_SalePayment_Amount", "\"AmountPaise\" > 0")));

        b.Entity<StockMovement>(e =>
        {
            e.HasIndex(x => new { x.ProductId, x.Date });
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId);
        });

        b.Entity<Receipt>(e => e.HasOne<Party>().WithMany().HasForeignKey(x => x.PartyId));
        b.Entity<Party>(e => e.HasIndex(x => x.Name));

        b.Entity<Account>(e => e.HasIndex(x => x.Code).IsUnique());

        b.Entity<Journal>(e =>
        {
            e.HasIndex(x => x.Date);
            e.HasIndex(x => new { x.SourceType, x.SourceId });
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.JournalId);
        });

        b.Entity<JournalLine>(e =>
        {
            e.HasIndex(x => x.AccountId);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
            // A journal line is either a debit or a credit, never negative, never both.
            e.ToTable(t => t.HasCheckConstraint("CK_JournalLine_Sides",
                "\"DebitPaise\" >= 0 AND \"CreditPaise\" >= 0 AND NOT (\"DebitPaise\" > 0 AND \"CreditPaise\" > 0)"));
        });

        b.Entity<DocumentSequence>(e => e.HasIndex(x => new { x.SeriesCode, x.FinancialYearId }).IsUnique());
        b.Entity<AuditEvent>(e => e.HasIndex(x => x.TimestampUtc));
        b.Entity<Setting>(e => e.HasKey(x => x.Key));

        b.Entity<SaleReturn>(e =>
        {
            e.HasIndex(x => x.CommandId).IsUnique();
            e.HasIndex(x => x.ReturnNumber).IsUnique();
            e.HasIndex(x => x.SaleId);
            e.HasIndex(x => x.Date);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.SaleReturnId);
            e.HasOne<Sale>().WithMany().HasForeignKey(x => x.SaleId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        });
        b.Entity<SaleReturnLine>(e =>
        {
            e.HasIndex(x => x.SaleLineId);
            e.HasOne<SaleLine>().WithMany().HasForeignKey(x => x.SaleLineId);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId);
            e.ToTable(t => t.HasCheckConstraint("CK_SaleReturnLine_Qty", "\"QtyMilli\" > 0"));
        });

        b.Entity<Supplier>(e => e.HasIndex(x => x.Name));
        b.Entity<Purchase>(e =>
        {
            e.Property(x => x.SupplierInvoiceNo).UseCollation("NOCASE");
            e.HasIndex(x => x.CommandId).IsUnique();
            e.HasIndex(x => x.PurchaseNumber).IsUnique();
            e.HasIndex(x => new { x.SupplierId, x.SupplierInvoiceNo }).IsUnique();   // the same supplier bill can never be entered twice
            e.HasIndex(x => x.PurchaseDate);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.PurchaseId);
            e.HasMany(x => x.Payments).WithOne().HasForeignKey(p => p.PurchaseId);
            e.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId);
        });
        b.Entity<PurchaseLine>(e =>
        {
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId);
            e.ToTable(t => t.HasCheckConstraint("CK_PurchaseLine_Qty", "\"QtyMilli\" > 0"));
        });
        b.Entity<PurchasePayment>(e => e.ToTable(t => t.HasCheckConstraint("CK_PurchasePayment_Amount", "\"AmountPaise\" > 0")));
        b.Entity<SupplierPayment>(e =>
        {
            e.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId);
            e.ToTable(t => t.HasCheckConstraint("CK_SupplierPayment_Amount", "\"AmountPaise\" > 0"));
        });
        b.Entity<PurchaseReturn>(e =>
        {
            e.HasIndex(x => x.CommandId).IsUnique();
            e.HasIndex(x => x.ReturnNumber).IsUnique();
            e.HasIndex(x => x.Date);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.PurchaseReturnId);
            e.HasOne<Purchase>().WithMany().HasForeignKey(x => x.PurchaseId);
            e.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId);
        });
        b.Entity<PurchaseReturnLine>(e =>
        {
            e.HasIndex(x => x.PurchaseLineId);
            e.HasOne<PurchaseLine>().WithMany().HasForeignKey(x => x.PurchaseLineId);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId);
            e.ToTable(t => t.HasCheckConstraint("CK_PurchaseReturnLine_Qty", "\"QtyMilli\" > 0"));
        });

        b.Entity<CashMovement>(e =>
        {
            e.HasIndex(x => x.Date);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            e.ToTable(t => t.HasCheckConstraint("CK_CashMovement_Amount", "\"AmountPaise\" > 0"));
        });
        b.Entity<DayClose>(e =>
        {
            e.HasIndex(x => x.CommandId).IsUnique();
            e.HasIndex(x => x.PeriodEnd);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ClosedByUserId);
        });

        b.Entity<ExpenseCategory>(e =>
        {
            e.Property(x => x.Name).UseCollation("NOCASE");
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.AccountCode).IsUnique();
        });
        b.Entity<Expense>(e =>
        {
            e.HasIndex(x => x.CommandId).IsUnique();
            e.HasIndex(x => x.ExpenseNumber).IsUnique();
            e.HasIndex(x => x.Date);
            e.HasIndex(x => x.RecordedAt);
            e.HasOne<ExpenseCategory>().WithMany().HasForeignKey(x => x.CategoryId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            e.ToTable(t => t.HasCheckConstraint("CK_Expense_Amount", "\"AmountPaise\" > 0"));
        });
        b.Entity<ExpensePayment>(e =>
        {
            e.HasIndex(x => x.ExpenseId);
            e.HasIndex(x => x.Date);
            e.HasOne<Expense>().WithMany().HasForeignKey(x => x.ExpenseId);
            e.ToTable(t => t.HasCheckConstraint("CK_ExpensePayment_Amount", "\"AmountPaise\" > 0"));
        });

        b.Entity<OpeningBalance>(e =>
        {
            e.HasIndex(x => x.CommandId).IsUnique();
            e.HasIndex(x => new { x.Kind, x.IsVoided });
            e.HasOne<Party>().WithMany().HasForeignKey(x => x.PartyId);
            e.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            e.ToTable(t => t.HasCheckConstraint("CK_OpeningBalance_Amount", "\"AmountPaise\" > 0"));
        });

        b.Entity<ProductBarcode>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.ProductId);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId);
            e.ToTable(t => t.HasCheckConstraint("CK_ProductBarcode_Pack", "\"PackQtyMilli\" > 0"));
        });
        b.Entity<HeldBill>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        });
        b.Entity<Repack>(e =>
        {
            e.HasIndex(x => x.CommandId).IsUnique();
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.SourceProductId);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.TargetProductId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            e.ToTable(t => t.HasCheckConstraint("CK_Repack_Qty", "\"SourceQtyMilli\" > 0 AND \"TargetQtyMilli\" > 0 AND \"SourceProductId\" <> \"TargetProductId\""));
        });

        // Financial documents are never deleted, so no cascading deletes anywhere.
        foreach (var fk in b.Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;
    }
}

/// <summary>Sets the SQLCipher key and per-connection pragmas as soon as a connection opens (decisions D3, plan section 5).</summary>
internal sealed class SqlCipherInterceptor(string keyHex) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        DbFactory.ApplyConnectionSetup(connection, keyHex, full: true);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        DbFactory.ApplyConnectionSetup(connection, keyHex, full: true);
        return Task.CompletedTask;
    }
}

public sealed class DbFactory
{
    private readonly AppPaths _paths;
    private readonly string _keyHex;

    static DbFactory() => SQLitePCL.Batteries_V2.Init();

    public DbFactory(AppPaths paths, string keyHex)
    {
        if (keyHex.Length != 64 || !keyHex.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid database key.", nameof(keyHex));
        _paths = paths;
        _keyHex = keyHex;
    }

    public AppPaths Paths => _paths;

    public AppDbContext Create()
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = _paths.DbPath, Pooling = false }.ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(cs)
            .AddInterceptors(new SqlCipherInterceptor(_keyHex))
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>Opens a raw keyed connection to any database file (used by backup and verification).</summary>
    public SqliteConnection OpenRaw(string path, bool full = false)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        ApplyConnectionSetup(conn, _keyHex, full);
        return conn;
    }

    internal static void ApplyConnectionSetup(DbConnection connection, string keyHex, bool full)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA key = \"x'{keyHex}'\";";
        cmd.ExecuteNonQuery();
        if (!full) return;
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
        cmd.ExecuteNonQuery();
    }
}
