namespace BillingSoftware.Domain;

public enum UserRole { Admin = 1, Cashier = 2 }
/// <summary>NonCash is used only for depreciation: an expense where no money moves.</summary>
public enum PaymentMode { Cash = 1, Upi = 2, Card = 3, Credit = 4, NonCash = 5 }

public enum OpeningKind { Cash = 1, Bank = 2, CustomerDue = 3, SupplierDue = 4, Loan = 5, FixedAsset = 6 }
public enum SaleStatus { Posted = 1, Voided = 2 }
public enum AccountType { Asset = 1, Liability = 2, Equity = 3, Income = 4, Expense = 5 }
/// <summary>Money moving in or out of the cash drawer other than sales and purchases (petty cash).</summary>
public enum CashMovementType { OwnerPayIn = 1, BankWithdrawal = 2, Expense = 3, OwnerWithdrawal = 4, BankDeposit = 5, LoanReceived = 6, LoanRepaid = 7 }

public enum ExpenseStatus { Posted = 1, Voided = 2 }

public enum StockMovementType { Opening = 1, Sale = 2, SaleVoid = 3, Loss = 4, Gain = 5, Purchase = 6, PurchaseReturn = 7, SaleReturn = 8, RepackOut = 9, RepackIn = 10 }

public class Company
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Gstin { get; set; }
    public string StateCode { get; set; } = "";        // two-digit GST state code, e.g. "33"
    public bool IsComposition { get; set; }
    /// <summary>False for a business that does not charge GST (for example one that is not registered): bills carry no tax and no input credit is claimed. On by default.</summary>
    public bool GstEnabled { get; set; } = true;
    /// <summary>Whether bills show and collect GST: registered, and not on the composition scheme.</summary>
    public bool CollectsTax => GstEnabled && !IsComposition;
    public string? Address { get; set; }
    public string? Phone { get; set; }
}

public class FinancialYear
{
    public long Id { get; set; }
    public string Label { get; set; } = "";             // "26-27"
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsLocked { get; set; }
}

public class User
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public UserRole Role { get; set; }
    public byte[] PasswordHash { get; set; } = Array.Empty<byte>();
    public byte[] Salt { get; set; } = Array.Empty<byte>();
    public int Iterations { get; set; }
    public int FailedAttempts { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Product
{
    public long Id { get; set; }
    public string Sku { get; set; } = "";
    public string? Barcode { get; set; }
    public string Name { get; set; } = "";
    /// <summary>The item's name in Tamil, printed on receipts instead of the English name when it is filled in.</summary>
    public string? TamilName { get; set; }
    public string? HsnCode { get; set; }
    public int GstRateBp { get; set; }                  // basis points: 1800 = 18%
    public string Unit { get; set; } = "pcs";
    public long SellingPricePaise { get; set; }
    public long? MrpPaise { get; set; }
    public bool PriceInclusive { get; set; } = true;    // MRP style: price includes GST
    public long AvgCostPaise { get; set; }              // weighted average cost per unit
    public long QtyOnHandMilli { get; set; }
    public bool IsActive { get; set; } = true;
}

public class StockMovement
{
    public long Id { get; set; }
    public long ProductId { get; set; }
    public DateTime Date { get; set; }
    public long QtyDeltaMilli { get; set; }
    public long UnitCostPaise { get; set; }
    public StockMovementType Type { get; set; }
    public string? RefType { get; set; }
    public long? RefId { get; set; }
    public string? Note { get; set; }
}

public class Party
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public string? Gstin { get; set; }
    public string? StateCode { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>The most this customer may owe on credit; 0 means no limit is set.</summary>
    public long CreditLimitPaise { get; set; }
}

public class Sale
{
    public long Id { get; set; }
    public Guid CommandId { get; set; }                  // idempotency key
    public string SeriesCode { get; set; } = "INV";
    public string InvoiceNumber { get; set; } = "";
    public long FinancialYearId { get; set; }
    public DateTime SaleDate { get; set; }
    public long CashierId { get; set; }
    public long? PartyId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerGstin { get; set; }
    public bool IsInterState { get; set; }
    public string? PlaceOfSupply { get; set; }          // two-digit state code snapshot (null on bills made before this was recorded)
    public long TaxablePaise { get; set; }
    public long CgstPaise { get; set; }
    public long SgstPaise { get; set; }
    public long IgstPaise { get; set; }
    public long DiscountPaise { get; set; }
    public long RoundOffPaise { get; set; }
    public long TotalPaise { get; set; }
    public long CogsPaise { get; set; }
    public SaleStatus Status { get; set; } = SaleStatus.Posted;
    public long? ApprovedByUserId { get; set; }
    public string? ApprovalNote { get; set; }
    public DateTime? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
    public long? VoidedByUserId { get; set; }
    public int ReprintCount { get; set; }
    public List<SaleLine> Lines { get; set; } = new();
    public List<SalePayment> Payments { get; set; } = new();
}

public class SaleLine
{
    public long Id { get; set; }
    public long SaleId { get; set; }
    public long ProductId { get; set; }
    public string ProductName { get; set; } = "";       // snapshots so history never depends on the current catalog
    public string? TamilName { get; set; }
    public string? HsnCode { get; set; }
    public string Unit { get; set; } = "";
    public long QtyMilli { get; set; }
    public long UnitPricePaise { get; set; }
    public bool PriceInclusive { get; set; }
    public long DiscountPaise { get; set; }
    public int GstRateBp { get; set; }
    public long TaxablePaise { get; set; }
    public long CgstPaise { get; set; }
    public long SgstPaise { get; set; }
    public long IgstPaise { get; set; }
    public long LineTotalPaise { get; set; }
    public long UnitCostPaise { get; set; }
}

public class SalePayment
{
    public long Id { get; set; }
    public long SaleId { get; set; }
    public PaymentMode Mode { get; set; }
    public long AmountPaise { get; set; }
}

public class Receipt
{
    public long Id { get; set; }
    public long PartyId { get; set; }
    public DateTime Date { get; set; }
    public PaymentMode Mode { get; set; }
    public long AmountPaise { get; set; }
    public long ReceivedByUserId { get; set; }
}

public class Account
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public AccountType Type { get; set; }
}

public class Journal
{
    public long Id { get; set; }
    public DateTime Date { get; set; }
    public string Narration { get; set; } = "";
    public string SourceType { get; set; } = "";
    public long SourceId { get; set; }
    public long CreatedByUserId { get; set; }
    public List<JournalLine> Lines { get; set; } = new();
}

public class JournalLine
{
    public long Id { get; set; }
    public long JournalId { get; set; }
    public long AccountId { get; set; }
    public long DebitPaise { get; set; }
    public long CreditPaise { get; set; }
}

public class DocumentSequence
{
    public long Id { get; set; }
    public string SeriesCode { get; set; } = "";
    public long FinancialYearId { get; set; }
    public long NextNumber { get; set; } = 1;
}

public class AuditEvent
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public long? UserId { get; set; }
    public string Username { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public long? EntityId { get; set; }
    public string Details { get; set; } = "";
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
}

public class Setting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public class SchemaVersion
{
    public int Id { get; set; }
    public int Version { get; set; }
    public DateTime AppliedAtUtc { get; set; }
    public string Description { get; set; } = "";
}

public static class SettingKeys
{
    public const string CashierMaxDiscountBp = "cashier.max_discount_bp";   // default 0: every discount needs Admin approval
    public const string LockedThrough = "period.locked_through";            // yyyy-MM-dd; no postings on or before this date
    public const string BackupFolder = "backup.folder";
    public const string LastBackupUtc = "backup.last_utc";
    public const string IdleLockMinutes = "security.idle_lock_minutes";
    public const string PrintReceiptPrinter = "print.receipt_printer";
    public const string PrintDocumentPrinter = "print.document_printer";
    public const string PrintLabelPrinter = "print.label_printer";
    public const string PrintPaperMm = "print.paper_mm";                       // 80 or 58
    public const string PrintLabelWidthMm = "print.label_width_mm";
    public const string PrintLabelHeightMm = "print.label_height_mm";
    public const string PrintLabelMode = "print.label_mode";                   // roll or sheet
    public const string B2ClThresholdPaise = "gst.b2cl_threshold_paise";     // confirm the current legal limit with the accountant
}

// ---------- Sales returns (credit notes) ----------
public class SaleReturn
{
    public long Id { get; set; }
    public Guid CommandId { get; set; }
    public string ReturnNumber { get; set; } = "";          // CN/26-27/000001
    public long FinancialYearId { get; set; }
    public DateTime Date { get; set; }
    public long SaleId { get; set; }
    public long? PartyId { get; set; }
    public string Reason { get; set; } = "";
    public PaymentMode RefundMode { get; set; }              // Credit = reduces what the customer owes
    public long TaxablePaise { get; set; }
    public long CgstPaise { get; set; }
    public long SgstPaise { get; set; }
    public long IgstPaise { get; set; }
    public long RoundOffPaise { get; set; }
    public long TotalPaise { get; set; }
    public long CogsReversalPaise { get; set; }
    public long UserId { get; set; }
    public long? ApprovedByUserId { get; set; }
    public List<SaleReturnLine> Lines { get; set; } = new();
}

public class SaleReturnLine
{
    public long Id { get; set; }
    public long SaleReturnId { get; set; }
    public long SaleLineId { get; set; }
    public long ProductId { get; set; }
    public long QtyMilli { get; set; }
    public bool Restock { get; set; }                        // false = damaged / not put back on the shelf
    public long TaxablePaise { get; set; }
    public long CgstPaise { get; set; }
    public long SgstPaise { get; set; }
    public long IgstPaise { get; set; }
    public long LineTotalPaise { get; set; }
    public long UnitCostPaise { get; set; }
}

// ---------- Suppliers and purchases ----------
public class Supplier
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public string? Gstin { get; set; }
    public string? StateCode { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Purchase
{
    public long Id { get; set; }
    public Guid CommandId { get; set; }
    public string PurchaseNumber { get; set; } = "";         // PUR/26-27/000001
    public long FinancialYearId { get; set; }
    public DateTime PurchaseDate { get; set; }
    public long SupplierId { get; set; }
    public string SupplierInvoiceNo { get; set; } = "";
    public DateTime? SupplierInvoiceDate { get; set; }
    public bool IsInterState { get; set; }
    public long TaxablePaise { get; set; }
    public long CgstPaise { get; set; }
    public long SgstPaise { get; set; }
    public long IgstPaise { get; set; }
    public long RoundOffPaise { get; set; }
    public long TotalPaise { get; set; }
    public long CreatedByUserId { get; set; }
    public List<PurchaseLine> Lines { get; set; } = new();
    public List<PurchasePayment> Payments { get; set; } = new();
}

public class PurchaseLine
{
    public long Id { get; set; }
    public long PurchaseId { get; set; }
    public long ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public string? HsnCode { get; set; }
    public string Unit { get; set; } = "";
    public long QtyMilli { get; set; }
    public long UnitCostPaise { get; set; }                  // supplier price before tax
    public long DiscountPaise { get; set; }
    public int GstRateBp { get; set; }
    public long TaxablePaise { get; set; }
    public long CgstPaise { get; set; }
    public long SgstPaise { get; set; }
    public long IgstPaise { get; set; }
    public long LineTotalPaise { get; set; }
    public bool ItcEligible { get; set; } = true;            // input tax credit (decision D14); false = tax becomes part of cost
    public long InventoryCostPaise { get; set; }             // taxable + (tax when credit is not claimed)
}

public class PurchasePayment
{
    public long Id { get; set; }
    public long PurchaseId { get; set; }
    public PaymentMode Mode { get; set; }                    // Credit = owed to the supplier
    public long AmountPaise { get; set; }
}

public class SupplierPayment
{
    public long Id { get; set; }
    public long SupplierId { get; set; }
    public DateTime Date { get; set; }
    public PaymentMode Mode { get; set; }
    public long AmountPaise { get; set; }
    public long UserId { get; set; }
}

public class PurchaseReturn
{
    public long Id { get; set; }
    public Guid CommandId { get; set; }
    public string ReturnNumber { get; set; } = "";           // DN/26-27/000001 (debit note)
    public long FinancialYearId { get; set; }
    public DateTime Date { get; set; }
    public long PurchaseId { get; set; }
    public long SupplierId { get; set; }
    public string Reason { get; set; } = "";
    public PaymentMode RefundMode { get; set; }              // Credit = reduces what we owe the supplier
    public long TaxablePaise { get; set; }
    public long CgstPaise { get; set; }
    public long SgstPaise { get; set; }
    public long IgstPaise { get; set; }
    public long RoundOffPaise { get; set; }
    public long TotalPaise { get; set; }
    public long UserId { get; set; }
    public List<PurchaseReturnLine> Lines { get; set; } = new();
}

public class PurchaseReturnLine
{
    public long Id { get; set; }
    public long PurchaseReturnId { get; set; }
    public long PurchaseLineId { get; set; }
    public long ProductId { get; set; }
    public long QtyMilli { get; set; }
    public long TaxablePaise { get; set; }
    public long CgstPaise { get; set; }
    public long SgstPaise { get; set; }
    public long IgstPaise { get; set; }
    public long LineTotalPaise { get; set; }
    public long InventoryCostPaise { get; set; }
    public long UnitCostPaise { get; set; }
}

// ---------- Cash drawer and day end ----------
public class CashMovement
{
    public long Id { get; set; }
    public DateTime Date { get; set; }
    public CashMovementType Type { get; set; }
    public long AmountPaise { get; set; }
    public string Reason { get; set; } = "";
    public long UserId { get; set; }
}

/// <summary>
/// One closing of the cash drawer. It covers everything since the previous close; the report is stored as it was at the
/// moment of closing, so a reprinted Z-report never changes.
/// </summary>
public class DayClose
{
    public long Id { get; set; }
    public Guid CommandId { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public long ClosedByUserId { get; set; }
    public long OpeningCashPaise { get; set; }
    public long ExpectedCashPaise { get; set; }
    public long CountedCashPaise { get; set; }
    public long VariancePaise { get; set; }
    public string? Note { get; set; }
    public string ReportJson { get; set; } = "";
}

// ---------- Business expenses (rent, salaries, electricity ...) ----------
public class ExpenseCategory
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string AccountCode { get; set; } = "";          // every category has its own expense account, so the profit and loss shows it by name
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
}

public class Expense
{
    public long Id { get; set; }
    public Guid CommandId { get; set; }
    public string ExpenseNumber { get; set; } = "";        // EXP/26-27/000001
    public long FinancialYearId { get; set; }
    public DateTime Date { get; set; }                     // the day the expense belongs to (may be earlier than the day it was entered)
    public DateTime RecordedAt { get; set; }               // when it was entered (and, for cash, when the money left the drawer)
    public long CategoryId { get; set; }
    public string PayTo { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Reference { get; set; }
    public long AmountPaise { get; set; }                  // everything paid or payable, GST included
    public PaymentMode PaymentMode { get; set; }           // Credit = not paid yet
    public string? SupplierGstin { get; set; }
    public long GstPaise { get; set; }                     // GST charged on the bill (0 if none was entered)
    public long CgstPaise { get; set; }                    // input credit claimed, by head (all 0 when GST was not claimed)
    public long SgstPaise { get; set; }
    public long IgstPaise { get; set; }
    public long ItcPaise => CgstPaise + SgstPaise + IgstPaise;
    public ExpenseStatus Status { get; set; } = ExpenseStatus.Posted;
    public DateTime? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
    public long? VoidedByUserId { get; set; }
    public long UserId { get; set; }
}

/// <summary>A payment made later against an expense that was entered as "not paid yet".</summary>
public class ExpensePayment
{
    public long Id { get; set; }
    public long ExpenseId { get; set; }
    public DateTime Date { get; set; }
    public PaymentMode Mode { get; set; }
    public long AmountPaise { get; set; }
    public long UserId { get; set; }
}

public static class ExpenseDefaults
{
    /// <summary>Starting categories, each with its own account code. Custom categories get the next free code from 6200.</summary>
    public static IReadOnlyList<(string Code, string Name)> Categories { get; } = new[]
    {
        ("6010", "Rent"), ("6020", "Salaries and wages"), ("6030", "Electricity"), ("6040", "Water"), ("6050", "Phone and internet"),
        ("6060", "Transport and freight"), ("6070", "Repairs and maintenance"), ("6080", "Packaging"), ("6090", "Advertising"),
        ("6100", "Bank charges"), ("6110", "Interest paid"), ("6120", "Insurance"), ("6130", "Licences and fees"), ("6140", "Stationery"),
        ("6150", "Cleaning"), ("6160", "Tea and refreshments"), ("6170", "Professional fees"), ("6180", "Depreciation"), ("6900", "Other expenses"),
    };
    public const int FirstCustomCode = 6200;
    public const int LastCustomCode = 6890;
}

// ---------- Opening balances (the position the business started from) ----------
public class OpeningBalance
{
    public long Id { get; set; }
    public Guid CommandId { get; set; }
    public OpeningKind Kind { get; set; }
    public DateTime Date { get; set; }                     // the date the balance is "as at" (the books show it from this date)
    public long? PartyId { get; set; }                     // customer, for CustomerDue
    public long? SupplierId { get; set; }                  // supplier, for SupplierDue
    public string Name { get; set; } = "";                 // bank, lender or asset name
    public long AmountPaise { get; set; }                  // what is recorded: for a fixed asset, its value after depreciation so far
    public long? CostPaise { get; set; }                   // fixed assets: the original cost
    public string? Note { get; set; }
    public bool IsVoided { get; set; }
    public DateTime? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
    public long UserId { get; set; }
    public DateTime RecordedAt { get; set; }
}

// ---------- Extra barcodes and repacking ----------
public class ProductBarcode
{
    public long Id { get; set; }
    public long ProductId { get; set; }
    public string Code { get; set; } = "";
    public long PackQtyMilli { get; set; } = Qty.Unit;     // how much of the item one scan stands for
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Repack
{
    public long Id { get; set; }
    public Guid CommandId { get; set; }
    public DateTime Date { get; set; }
    public long SourceProductId { get; set; }
    public long TargetProductId { get; set; }
    public long SourceQtyMilli { get; set; }
    public long TargetQtyMilli { get; set; }
    public long SourceUnitCostPaise { get; set; }
    public long TargetUnitCostPaise { get; set; }
    public long ValuePaise { get; set; }                   // the cost of the stock used
    public string? Note { get; set; }
    public long UserId { get; set; }
}

// ---------- Held bills (put aside at the counter) ----------
public class HeldBill
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public long UserId { get; set; }
    public string Label { get; set; } = "";
    public long? PartyId { get; set; }
    public string LinesJson { get; set; } = "[]";           // item, quantity and discount only: prices are taken fresh when the bill is recalled
}
