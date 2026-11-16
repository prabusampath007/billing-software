namespace BillingSoftware.Application;

// ---------- GSTR-1 style tables (all money in paise) ----------
public sealed record B2BRow(string Gstin, string PartyName, string DocNo, DateTime Date, long DocValuePaise, string PlaceOfSupply,
    int RateBp, long TaxablePaise, long IgstPaise, long CgstPaise, long SgstPaise);

public sealed record B2ClRow(string DocNo, DateTime Date, long DocValuePaise, string PlaceOfSupply, int RateBp, long TaxablePaise, long IgstPaise);

/// <summary>Small consumer sales, added up by place of supply and rate (credit notes already taken off).</summary>
public sealed record B2CsRow(string PlaceOfSupply, int RateBp, long TaxablePaise, long IgstPaise, long CgstPaise, long SgstPaise);

/// <summary>Credit notes against registered customers (kind "Registered") or against large inter-state bills (kind "Unregistered").</summary>
public sealed record NoteRow(string Kind, string Gstin, string PartyName, string NoteNo, DateTime NoteDate, string OriginalNo, DateTime OriginalDate,
    long NoteValuePaise, string PlaceOfSupply, int RateBp, long TaxablePaise, long IgstPaise, long CgstPaise, long SgstPaise);

/// <summary>Sales and returns by HSN, unit and rate. Returns are shown beside sales so the accountant can choose how to report them.</summary>
public sealed record HsnRow(string Hsn, string Unit, string Uqc, int RateBp,
    long SoldQtyMilli, long SoldTaxablePaise, long SoldTaxPaise, long SoldValuePaise,
    long ReturnQtyMilli, long ReturnTaxablePaise, long ReturnTaxPaise, long ReturnValuePaise);

public sealed record DocSummaryRow(string Description, string From, string To, int Total, int Cancelled);

public sealed record NilRow(string Description, long TaxablePaise);

public sealed record PosRow(string PlaceOfSupply, long TaxablePaise, long IgstPaise);

// ---------- GSTR-3B style summary ----------
public sealed record Gstr3b(
    long OutTaxablePaise, long OutIgstPaise, long OutCgstPaise, long OutSgstPaise,       // 3.1(a) outward taxable supplies, after credit notes
    long NilRatedTaxablePaise,                                                           // 3.1(c) supplies at 0%
    IReadOnlyList<PosRow> InterStateToUnregistered,                                      // 3.2
    long ItcIgstPaise, long ItcCgstPaise, long ItcSgstPaise,                             // 4(A)(5) input credit claimed, after debit notes
    long IneligibleIgstPaise, long IneligibleCgstPaise, long IneligibleSgstPaise,        // tax on purchases where credit was not claimed
    long InwardNilIntraPaise, long InwardNilInterPaise,                                  // 5 purchases at 0%
    long NetIgstPaise, long NetCgstPaise, long NetSgstPaise)                             // output minus input credit; negative = credit left over
{
    public long OutTaxPaise => OutIgstPaise + OutCgstPaise + OutSgstPaise;
    public long ItcPaise => ItcIgstPaise + ItcCgstPaise + ItcSgstPaise;
}

// ---------- Registers ----------
public sealed record SalesRegisterRow(string DocNo, DateTime Date, string Customer, string? Gstin, string Status, string PlaceOfSupply,
    long TaxablePaise, long CgstPaise, long SgstPaise, long IgstPaise, long RoundOffPaise, long TotalPaise);

public sealed record PurchaseRegisterRow(string PurchaseNo, DateTime Date, string Supplier, string? SupplierGstin, string SupplierInvoiceNo, DateTime? SupplierInvoiceDate,
    long TaxablePaise, long CgstPaise, long SgstPaise, long IgstPaise, long ItcClaimedPaise, long ItcNotClaimedPaise, long TotalPaise);

public sealed record NoteRegisterRow(string Kind, string NoteNo, DateTime Date, string Against, string Party, string? Gstin,
    long TaxablePaise, long CgstPaise, long SgstPaise, long IgstPaise, long TotalPaise);

public sealed record GstReport(
    int Year, int Month, string PeriodLabel, string CompanyName, string? CompanyGstin, string StateCode, bool IsComposition, long B2ClThresholdPaise,
    IReadOnlyList<B2BRow> B2B, IReadOnlyList<B2ClRow> B2Cl, IReadOnlyList<B2CsRow> B2Cs, IReadOnlyList<NoteRow> Notes,
    IReadOnlyList<HsnRow> Hsn, IReadOnlyList<DocSummaryRow> Documents, IReadOnlyList<NilRow> NilRated,
    Gstr3b Gstr3b,
    IReadOnlyList<SalesRegisterRow> SalesRegister, IReadOnlyList<PurchaseRegisterRow> PurchaseRegister, IReadOnlyList<NoteRegisterRow> NoteRegister,
    IReadOnlyList<string> Warnings, long CompositionTurnoverPaise);

public interface IGstReportService
{
    /// <summary>Prepares the worksheets for one month. These are for the accountant to review; nothing is filed anywhere.</summary>
    Task<GstReport> BuildAsync(int year, int month);
}
