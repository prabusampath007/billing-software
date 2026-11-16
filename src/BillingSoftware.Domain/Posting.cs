namespace BillingSoftware.Domain;

public static class AccountCodes
{
    public const string Cash = "1000";
    public const string PettyCash = "1005";
    public const string Bank = "1010";          // UPI and card settle to bank
    public const string Receivables = "1200";
    public const string Inventory = "1300";
    public const string FixedAssets = "1500";
    public const string Loans = "2300";
    public const string InputCgst = "1410";
    public const string InputSgst = "1420";
    public const string InputIgst = "1430";
    public const string Payables = "2000";
    public const string ExpensesPayable = "2010";
    public const string CgstPayable = "2110";
    public const string SgstPayable = "2120";
    public const string IgstPayable = "2130";
    public const string OpeningEquity = "3000";
    public const string OwnerFunds = "3010";
    public const string OwnerDrawings = "3020";
    public const string Sales = "4000";
    public const string Cogs = "5000";
    public const string RoundOff = "5900";
    public const string CashOverShort = "5910";
    public const string InventoryCostVariance = "5920";
    public const string InventoryLoss = "5930";
    public const string ShopExpenses = "5940";

    /// <summary>For purchases and supplier money, "credit" means we owe the supplier (payables), not that a customer owes us.</summary>
    /// <summary>For an expense, "credit" means it has not been paid yet, so it sits in Expenses payable.</summary>
    public static string ForExpensePayment(PaymentMode mode) => mode switch
    {
        PaymentMode.Credit => ExpensesPayable,
        PaymentMode.NonCash => FixedAssets,        // depreciation reduces the value of the assets
        _ => ForPayment(mode),
    };

    public static string ForPurchasePayment(PaymentMode mode) => mode == PaymentMode.Credit ? Payables : ForPayment(mode);

    public static string ForPayment(PaymentMode mode) => mode switch
    {
        PaymentMode.Cash => Cash,
        PaymentMode.Upi => Bank,
        PaymentMode.Card => Bank,
        PaymentMode.Credit => Receivables,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>The standard chart of accounts seeded on first run (final chart to be signed off by the accountant).</summary>
    public static IReadOnlyList<(string Code, string Name, AccountType Type)> Standard { get; } = new[]
    {
        (Cash, "Cash in hand", AccountType.Asset),
        (PettyCash, "Petty cash", AccountType.Asset),
        (Bank, "Bank and UPI/card settlements", AccountType.Asset),
        (Receivables, "Accounts receivable", AccountType.Asset),
        (Inventory, "Inventory", AccountType.Asset),
        (FixedAssets, "Furniture, equipment and other assets (after depreciation)", AccountType.Asset),
        (InputCgst, "CGST input credit", AccountType.Asset),
        (InputSgst, "SGST input credit", AccountType.Asset),
        (InputIgst, "IGST input credit", AccountType.Asset),
        (CgstPayable, "CGST payable", AccountType.Liability),
        (SgstPayable, "SGST payable", AccountType.Liability),
        (IgstPayable, "IGST payable", AccountType.Liability),
        (Payables, "Accounts payable", AccountType.Liability),
        (ExpensesPayable, "Expenses payable (not paid yet)", AccountType.Liability),
        (Loans, "Loans taken", AccountType.Liability),
        (OpeningEquity, "Opening balance equity", AccountType.Equity),
        (OwnerFunds, "Owner's funds introduced", AccountType.Equity),
        (OwnerDrawings, "Owner's drawings", AccountType.Equity),
        (Sales, "Sales", AccountType.Income),
        (Cogs, "Cost of goods sold", AccountType.Expense),
        (RoundOff, "Round-off", AccountType.Expense),
        (CashOverShort, "Cash over/short", AccountType.Expense),
        (InventoryCostVariance, "Inventory cost variance", AccountType.Expense),
        (InventoryLoss, "Inventory loss", AccountType.Expense),
        (ShopExpenses, "Shop expenses paid from the drawer", AccountType.Expense),
    };
}

public sealed record JournalLineDraft(string AccountCode, long DebitPaise, long CreditPaise);

public sealed class UnbalancedJournalException(long debit, long credit)
    : Exception($"Journal is not balanced: debit {debit} paise, credit {credit} paise.");

/// <summary>Pure posting rules. Every journal built here is verified balanced before it can be saved.</summary>
public static class PostingBuilder
{
    public static List<JournalLineDraft> ForSale(
        long taxablePaise, long cgstPaise, long sgstPaise, long igstPaise,
        long roundOffPaise, long cogsPaise,
        IEnumerable<(PaymentMode Mode, long AmountPaise)> payments)
    {
        var lines = new List<JournalLineDraft>();
        foreach (var g in payments.GroupBy(p => AccountCodes.ForPayment(p.Mode)))
            lines.Add(new(g.Key, g.Sum(p => p.AmountPaise), 0));

        lines.Add(new(AccountCodes.Sales, 0, taxablePaise));
        lines.Add(new(AccountCodes.CgstPayable, 0, cgstPaise));
        lines.Add(new(AccountCodes.SgstPayable, 0, sgstPaise));
        lines.Add(new(AccountCodes.IgstPayable, 0, igstPaise));
        if (roundOffPaise > 0) lines.Add(new(AccountCodes.RoundOff, 0, roundOffPaise));
        else if (roundOffPaise < 0) lines.Add(new(AccountCodes.RoundOff, -roundOffPaise, 0));
        lines.Add(new(AccountCodes.Cogs, cogsPaise, 0));
        lines.Add(new(AccountCodes.Inventory, 0, cogsPaise));

        return Finish(lines);
    }

    /// <summary>Customer return (credit note): reverses the sale; the refund goes out through the chosen payment account.</summary>
    public static List<JournalLineDraft> ForSaleReturn(long taxable, long cgst, long sgst, long igst, long roundOff, long cogsReversal,
        IEnumerable<(PaymentMode Mode, long AmountPaise)> refunds)
    {
        var lines = new List<JournalLineDraft>
        {
            new(AccountCodes.Sales, taxable, 0), new(AccountCodes.CgstPayable, cgst, 0),
            new(AccountCodes.SgstPayable, sgst, 0), new(AccountCodes.IgstPayable, igst, 0),
        };
        if (roundOff > 0) lines.Add(new(AccountCodes.RoundOff, roundOff, 0));
        else if (roundOff < 0) lines.Add(new(AccountCodes.RoundOff, 0, -roundOff));
        foreach (var g in refunds.GroupBy(r => AccountCodes.ForPayment(r.Mode))) lines.Add(new(g.Key, 0, g.Sum(r => r.AmountPaise)));
        lines.Add(new(AccountCodes.Inventory, cogsReversal, 0));
        lines.Add(new(AccountCodes.Cogs, 0, cogsReversal));
        return Finish(lines);
    }

    /// <summary>Supplier bill: stock (plus any tax we cannot claim) and input credit in; cash, bank or payables out.</summary>
    public static List<JournalLineDraft> ForPurchase(long inventoryCost, long inCgst, long inSgst, long inIgst, long roundOff,
        IEnumerable<(PaymentMode Mode, long AmountPaise)> payments)
    {
        var lines = new List<JournalLineDraft>
        {
            new(AccountCodes.Inventory, inventoryCost, 0), new(AccountCodes.InputCgst, inCgst, 0),
            new(AccountCodes.InputSgst, inSgst, 0), new(AccountCodes.InputIgst, inIgst, 0),
        };
        if (roundOff > 0) lines.Add(new(AccountCodes.RoundOff, roundOff, 0));
        else if (roundOff < 0) lines.Add(new(AccountCodes.RoundOff, 0, -roundOff));
        foreach (var g in payments.GroupBy(p => AccountCodes.ForPurchasePayment(p.Mode))) lines.Add(new(g.Key, 0, g.Sum(p => p.AmountPaise)));
        return Finish(lines);
    }

    /// <summary>Goods sent back to a supplier (debit note): reverses the purchase; the supplier refunds or reduces what we owe.</summary>
    public static List<JournalLineDraft> ForPurchaseReturn(long inventoryCost, long inCgst, long inSgst, long inIgst, long roundOff,
        IEnumerable<(PaymentMode Mode, long AmountPaise)> refunds)
    {
        var lines = new List<JournalLineDraft>
        {
            new(AccountCodes.Inventory, 0, inventoryCost), new(AccountCodes.InputCgst, 0, inCgst),
            new(AccountCodes.InputSgst, 0, inSgst), new(AccountCodes.InputIgst, 0, inIgst),
        };
        if (roundOff > 0) lines.Add(new(AccountCodes.RoundOff, 0, roundOff));
        else if (roundOff < 0) lines.Add(new(AccountCodes.RoundOff, -roundOff, 0));
        foreach (var g in refunds.GroupBy(r => AccountCodes.ForPurchasePayment(r.Mode))) lines.Add(new(g.Key, g.Sum(r => r.AmountPaise), 0));
        return Finish(lines);
    }

    /// <summary>Petty cash in or out of the drawer, against the account that explains it.</summary>
    public static List<JournalLineDraft> ForCashMovement(CashMovementType type, long amountPaise) => type switch
    {
        CashMovementType.OwnerPayIn => Finish(new() { new(AccountCodes.Cash, amountPaise, 0), new(AccountCodes.OwnerFunds, 0, amountPaise) }),
        CashMovementType.BankWithdrawal => Finish(new() { new(AccountCodes.Cash, amountPaise, 0), new(AccountCodes.Bank, 0, amountPaise) }),
        CashMovementType.Expense => Finish(new() { new(AccountCodes.ShopExpenses, amountPaise, 0), new(AccountCodes.Cash, 0, amountPaise) }),
        CashMovementType.OwnerWithdrawal => Finish(new() { new(AccountCodes.OwnerDrawings, amountPaise, 0), new(AccountCodes.Cash, 0, amountPaise) }),
        CashMovementType.BankDeposit => Finish(new() { new(AccountCodes.Bank, amountPaise, 0), new(AccountCodes.Cash, 0, amountPaise) }),
        CashMovementType.LoanReceived => Finish(new() { new(AccountCodes.Cash, amountPaise, 0), new(AccountCodes.Loans, 0, amountPaise) }),
        CashMovementType.LoanRepaid => Finish(new() { new(AccountCodes.Loans, amountPaise, 0), new(AccountCodes.Cash, 0, amountPaise) }),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Cash counted differently from what the books expect. Over = credit, short = debit, both against Cash over/short.</summary>
    public static List<JournalLineDraft> ForCashVariance(long variancePaise) => variancePaise >= 0
        ? Finish(new() { new(AccountCodes.Cash, variancePaise, 0), new(AccountCodes.CashOverShort, 0, variancePaise) })
        : Finish(new() { new(AccountCodes.CashOverShort, -variancePaise, 0), new(AccountCodes.Cash, 0, -variancePaise) });

    /// <summary>
    /// A balance the business already had when it started: the other side is Opening balance equity (what the owner started with).
    /// </summary>
    public static List<JournalLineDraft> ForOpening(OpeningKind kind, long amountPaise) => kind switch
    {
        OpeningKind.Cash => Finish(new() { new(AccountCodes.Cash, amountPaise, 0), new(AccountCodes.OpeningEquity, 0, amountPaise) }),
        OpeningKind.Bank => Finish(new() { new(AccountCodes.Bank, amountPaise, 0), new(AccountCodes.OpeningEquity, 0, amountPaise) }),
        OpeningKind.CustomerDue => Finish(new() { new(AccountCodes.Receivables, amountPaise, 0), new(AccountCodes.OpeningEquity, 0, amountPaise) }),
        OpeningKind.FixedAsset => Finish(new() { new(AccountCodes.FixedAssets, amountPaise, 0), new(AccountCodes.OpeningEquity, 0, amountPaise) }),
        OpeningKind.SupplierDue => Finish(new() { new(AccountCodes.OpeningEquity, amountPaise, 0), new(AccountCodes.Payables, 0, amountPaise) }),
        OpeningKind.Loan => Finish(new() { new(AccountCodes.OpeningEquity, amountPaise, 0), new(AccountCodes.Loans, 0, amountPaise) }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Cash already in the drawer when the owner started using the software.</summary>
    public static List<JournalLineDraft> ForOpeningCash(long amountPaise) => ForCashMovement(CashMovementType.OwnerPayIn, amountPaise);

    /// <summary>An expense: debit its own account, credit cash, bank or expenses payable.</summary>
    /// <summary>
    /// An expense. When input credit is claimed the GST goes to the input-credit accounts and only the amount before GST is the expense;
    /// the whole bill is still paid (or owed).
    /// </summary>
    public static List<JournalLineDraft> ForExpense(string expenseAccountCode, long amountPaise, PaymentMode mode, long cgstPaise = 0, long sgstPaise = 0, long igstPaise = 0)
    {
        var credit = cgstPaise + sgstPaise + igstPaise;
        var lines = new List<JournalLineDraft> { new(expenseAccountCode, amountPaise - credit, 0) };
        if (cgstPaise > 0) lines.Add(new(AccountCodes.InputCgst, cgstPaise, 0));
        if (sgstPaise > 0) lines.Add(new(AccountCodes.InputSgst, sgstPaise, 0));
        if (igstPaise > 0) lines.Add(new(AccountCodes.InputIgst, igstPaise, 0));
        lines.Add(new(AccountCodes.ForExpensePayment(mode), 0, amountPaise));
        return Finish(lines);
    }

    /// <summary>Paying an expense that was entered as not paid yet.</summary>
    public static List<JournalLineDraft> ForExpenseSettlement(PaymentMode mode, long amountPaise) => Finish(new()
    {
        new(AccountCodes.ExpensesPayable, amountPaise, 0),
        new(AccountCodes.ForPayment(mode), 0, amountPaise),
    });

    /// <summary>Paying a supplier what we owe: debit payables, credit cash or bank.</summary>
    public static List<JournalLineDraft> ForSupplierPayment(PaymentMode mode, long amountPaise) => Finish(new()
    {
        new(AccountCodes.Payables, amountPaise, 0),
        new(AccountCodes.ForPayment(mode), 0, amountPaise),
    });

    /// <summary>Stock value in (opening stock): debit inventory, credit opening equity.</summary>
    public static List<JournalLineDraft> ForOpeningStock(long valuePaise) => Finish(new()
    {
        new(AccountCodes.Inventory, valuePaise, 0),
        new(AccountCodes.OpeningEquity, 0, valuePaise),
    });

    /// <summary>Stock loss: debit inventory loss, credit inventory. A negative value is a stock gain.</summary>
    public static List<JournalLineDraft> ForStockLoss(long lossPaise) => lossPaise >= 0
        ? Finish(new() { new(AccountCodes.InventoryLoss, lossPaise, 0), new(AccountCodes.Inventory, 0, lossPaise) })
        : Finish(new() { new(AccountCodes.Inventory, -lossPaise, 0), new(AccountCodes.InventoryLoss, 0, -lossPaise) });

    /// <summary>Cost variance from a receipt covering negative stock: positive = extra cost (expense).</summary>
    public static List<JournalLineDraft> ForCostVariance(long variancePaise) => variancePaise >= 0
        ? Finish(new() { new(AccountCodes.InventoryCostVariance, variancePaise, 0), new(AccountCodes.Inventory, 0, variancePaise) })
        : Finish(new() { new(AccountCodes.Inventory, -variancePaise, 0), new(AccountCodes.InventoryCostVariance, 0, -variancePaise) });

    /// <summary>Customer settlement of credit sales: debit cash/bank, credit receivables.</summary>
    public static List<JournalLineDraft> ForReceipt(PaymentMode mode, long amountPaise) => Finish(new()
    {
        new(AccountCodes.ForPayment(mode), amountPaise, 0),
        new(AccountCodes.Receivables, 0, amountPaise),
    });

    public static List<JournalLineDraft> Reverse(IEnumerable<JournalLineDraft> lines) =>
        Finish(lines.Select(l => new JournalLineDraft(l.AccountCode, l.CreditPaise, l.DebitPaise)).ToList());

    public static void EnsureBalanced(IEnumerable<JournalLineDraft> lines)
    {
        long d = 0, c = 0;
        foreach (var l in lines)
        {
            if (l.DebitPaise < 0 || l.CreditPaise < 0 || (l.DebitPaise > 0 && l.CreditPaise > 0))
                throw new UnbalancedJournalException(l.DebitPaise, l.CreditPaise);
            d += l.DebitPaise; c += l.CreditPaise;
        }
        if (d != c || d == 0) throw new UnbalancedJournalException(d, c);
    }

    private static List<JournalLineDraft> Finish(List<JournalLineDraft> lines)
    {
        var kept = lines.Where(l => l.DebitPaise != 0 || l.CreditPaise != 0).ToList();
        EnsureBalanced(kept);
        return kept;
    }
}
