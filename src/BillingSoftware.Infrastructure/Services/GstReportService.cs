using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

internal sealed class GstReportService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), IGstReportService
{
    /// <summary>Default limit for the "large" inter-state consumer invoice table (a setting; confirm the current limit with the accountant).</summary>
    public const long DefaultB2ClThresholdPaise = 100_000_00;

    public static string Uqc(string unit) => (unit ?? "").Trim().ToLowerInvariant() switch
    {
        "pcs" or "pc" or "nos" => "PCS", "kg" => "KGS", "g" or "gm" => "GMS", "ltr" or "l" => "LTR", "ml" => "MLT",
        "box" => "BOX", "pack" => "PAC", "dozen" => "DOZ", "mtr" or "m" => "MTR", "pair" => "PRS", _ => "OTH",
    };

    public async Task<GstReport> BuildAsync(int year, int month)
    {
        RequireAdmin();
        if (year < 2017 || year > 2100 || month < 1 || month > 12) throw new ValidationException("Choose a valid month.");
        var from = new DateTime(year, month, 1);
        var to = from.AddMonths(1);

        await using var db = Factory.Create();
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync() ?? throw new BusinessRuleException("Company setup is not complete.");
        if (!company.GstEnabled) throw new BusinessRuleException("GST is turned off for this business, so there are no GST worksheets. Turn it on in Settings to prepare them.");
        var thresholdText = await db.Settings.AsNoTracking().Where(s => s.Key == SettingKeys.B2ClThresholdPaise).Select(s => s.Value).FirstOrDefaultAsync();
        var threshold = long.TryParse(thresholdText, out var th) && th > 0 ? th : DefaultB2ClThresholdPaise;

        // ---------------- load ----------------
        var sales = await db.Sales.AsNoTracking().Include(s => s.Lines).Where(s => s.SaleDate >= from && s.SaleDate < to).OrderBy(s => s.Id).ToListAsync();
        var returns = await db.SaleReturns.AsNoTracking().Include(r => r.Lines).Where(r => r.Date >= from && r.Date < to).OrderBy(r => r.Id).ToListAsync();
        var originalIds = returns.Select(r => r.SaleId).Distinct().ToList();
        var originals = await db.Sales.AsNoTracking().Include(s => s.Lines).Where(s => originalIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        var partyIds = sales.Concat(originals.Values).Where(s => s.PartyId != null).Select(s => s.PartyId!.Value).Distinct().ToList();
        var partyState = await db.Parties.AsNoTracking().Where(p => partyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.StateCode);

        var purchases = await db.Purchases.AsNoTracking().Include(p => p.Lines).Where(p => p.PurchaseDate >= from && p.PurchaseDate < to).OrderBy(p => p.Id).ToListAsync();
        var purchaseReturns = await db.PurchaseReturns.AsNoTracking().Include(r => r.Lines).Where(r => r.Date >= from && r.Date < to).OrderBy(r => r.Id).ToListAsync();
        var pOrigIds = purchaseReturns.Select(r => r.PurchaseId).Distinct().ToList();
        var pOriginals = await db.Purchases.AsNoTracking().Include(p => p.Lines).Where(p => pOrigIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var supplierIds = purchases.Select(p => p.SupplierId).Concat(purchaseReturns.Select(r => r.SupplierId)).Distinct().ToList();
        var suppliers = await db.Suppliers.AsNoTracking().Where(s => supplierIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);

        string Pos(Sale s)
        {
            if (!string.IsNullOrWhiteSpace(s.PlaceOfSupply)) return s.PlaceOfSupply!;
            if (!s.IsInterState) return company.StateCode;
            if (s.PartyId is { } pid && partyState.TryGetValue(pid, out var st) && !string.IsNullOrWhiteSpace(st)) return st!;
            return s.CustomerGstin is { Length: >= 2 } g ? g[..2] : "";
        }
        bool Registered(Sale s) => !string.IsNullOrWhiteSpace(s.CustomerGstin);
        bool IsB2Cl(Sale s) => !Registered(s) && s.IsInterState && s.TotalPaise > threshold;
        string DocKind(Sale s) => s.SeriesCode == "BOS" ? "Bill of supply" : "Tax invoice";

        var live = sales.Where(s => s.Status == SaleStatus.Posted).ToList();
        var warnings = new List<string>();

        // ---------------- outward: B2B, B2CL, B2CS, nil ----------------
        var b2b = new List<B2BRow>();
        var b2cl = new List<B2ClRow>();
        var b2csAgg = new Dictionary<(string Pos, int Rate), long[]>();     // taxable, igst, cgst, sgst
        var nilAgg = new Dictionary<string, long>();
        string NilKey(bool registered, bool inter) => $"{(inter ? "Inter-state" : "Intra-state")} supplies to {(registered ? "registered" : "unregistered")} customers";
        void AddB2cs(string pos, int rate, long t, long ig, long cg, long sg, int sign)
        {
            var k = (pos, rate);
            if (!b2csAgg.TryGetValue(k, out var a)) b2csAgg[k] = a = new long[4];
            a[0] += sign * t; a[1] += sign * ig; a[2] += sign * cg; a[3] += sign * sg;
        }
        void AddNil(string key, long taxable, int sign) { nilAgg[key] = nilAgg.GetValueOrDefault(key) + sign * taxable; }

        foreach (var s in live)
        {
            var pos = Pos(s);
            var registered = Registered(s);
            foreach (var g in s.Lines.GroupBy(l => l.GstRateBp))
            {
                var t = g.Sum(l => l.TaxablePaise); var ig = g.Sum(l => l.IgstPaise); var cg = g.Sum(l => l.CgstPaise); var sg = g.Sum(l => l.SgstPaise);
                if (g.Key == 0) { AddNil(NilKey(registered, s.IsInterState), t, +1); continue; }
                if (registered) b2b.Add(new B2BRow(s.CustomerGstin!, s.CustomerName ?? "", s.InvoiceNumber, s.SaleDate, s.TotalPaise, pos, g.Key, t, ig, cg, sg));
                else if (IsB2Cl(s)) b2cl.Add(new B2ClRow(s.InvoiceNumber, s.SaleDate, s.TotalPaise, pos, g.Key, t, ig));
                else AddB2cs(pos, g.Key, t, ig, cg, sg, +1);
            }
        }

        // ---------------- credit notes ----------------
        var notes = new List<NoteRow>();
        var noteRegister = new List<NoteRegisterRow>();
        var hsnAgg = new Dictionary<(string Hsn, string Unit, int Rate), long[]>();   // sold qty, taxable, tax, value, ret qty, ret taxable, ret tax, ret value
        long[] Hsn(string hsn, string unit, int rate)
        {
            var k = (hsn, unit, rate);
            if (!hsnAgg.TryGetValue(k, out var a)) hsnAgg[k] = a = new long[8];
            return a;
        }
        foreach (var s in live)
            foreach (var l in s.Lines)
            {
                var a = Hsn(l.HsnCode ?? "", l.Unit, l.GstRateBp);
                a[0] += l.QtyMilli; a[1] += l.TaxablePaise; a[2] += l.CgstPaise + l.SgstPaise + l.IgstPaise; a[3] += l.LineTotalPaise;
            }

        foreach (var r in returns)
        {
            var orig = originals[r.SaleId];
            var pos = Pos(orig);
            var registered = Registered(orig);
            var byLine = orig.Lines.ToDictionary(l => l.Id);
            noteRegister.Add(new NoteRegisterRow("Credit note", r.ReturnNumber, r.Date, orig.InvoiceNumber, orig.CustomerName ?? "Walk-in", orig.CustomerGstin,
                r.TaxablePaise, r.CgstPaise, r.SgstPaise, r.IgstPaise, r.TotalPaise));
            foreach (var g in r.Lines.GroupBy(l => byLine[l.SaleLineId].GstRateBp))
            {
                var t = g.Sum(l => l.TaxablePaise); var ig = g.Sum(l => l.IgstPaise); var cg = g.Sum(l => l.CgstPaise); var sg = g.Sum(l => l.SgstPaise);
                if (g.Key == 0) { AddNil(NilKey(registered, orig.IsInterState), t, -1); continue; }
                if (registered || IsB2Cl(orig))
                    notes.Add(new NoteRow(registered ? "Registered" : "Unregistered", orig.CustomerGstin ?? "", orig.CustomerName ?? "", r.ReturnNumber, r.Date,
                        orig.InvoiceNumber, orig.SaleDate, r.TotalPaise, pos, g.Key, t, ig, cg, sg));
                else AddB2cs(pos, g.Key, t, ig, cg, sg, -1);
            }
            foreach (var l in r.Lines)
            {
                var sl = byLine[l.SaleLineId];
                var a = Hsn(sl.HsnCode ?? "", sl.Unit, sl.GstRateBp);
                a[4] += l.QtyMilli; a[5] += l.TaxablePaise; a[6] += l.CgstPaise + l.SgstPaise + l.IgstPaise; a[7] += l.LineTotalPaise;
            }
        }

        var b2cs = b2csAgg.Where(kv => kv.Value.Any(v => v != 0)).OrderBy(kv => kv.Key.Pos).ThenBy(kv => kv.Key.Rate)
            .Select(kv => new B2CsRow(kv.Key.Pos, kv.Key.Rate, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3])).ToList();
        var hsn = hsnAgg.OrderBy(kv => kv.Key.Hsn).ThenBy(kv => kv.Key.Rate)
            .Select(kv => new HsnRow(kv.Key.Hsn, kv.Key.Unit, Uqc(kv.Key.Unit), kv.Key.Rate, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3], kv.Value[4], kv.Value[5], kv.Value[6], kv.Value[7])).ToList();
        var nil = nilAgg.Where(kv => kv.Value != 0).OrderBy(kv => kv.Key).Select(kv => new NilRow(kv.Key, kv.Value)).ToList();

        // ---------------- document summary (Table 13) ----------------
        var docs = new List<DocSummaryRow>();
        foreach (var g in sales.GroupBy(s => s.SeriesCode).OrderBy(g => g.Key))
        {
            var numbers = g.Select(s => s.InvoiceNumber).OrderBy(n => n, StringComparer.Ordinal).ToList();
            docs.Add(new DocSummaryRow(g.Key == "BOS" ? "Bills of supply" : "Invoices for outward supply", numbers.First(), numbers.Last(), g.Count(), g.Count(s => s.Status == SaleStatus.Voided)));
        }
        if (returns.Count > 0)
        {
            var numbers = returns.Select(r => r.ReturnNumber).OrderBy(n => n, StringComparer.Ordinal).ToList();
            docs.Add(new DocSummaryRow("Credit notes", numbers.First(), numbers.Last(), returns.Count, 0));
        }

        var salesRegister = sales.Select(s => new SalesRegisterRow(s.InvoiceNumber, s.SaleDate, string.IsNullOrWhiteSpace(s.CustomerName) ? "Walk-in" : s.CustomerName!, s.CustomerGstin,
            s.Status == SaleStatus.Voided ? "Cancelled" : DocKind(s), Pos(s), s.TaxablePaise, s.CgstPaise, s.SgstPaise, s.IgstPaise, s.RoundOffPaise, s.TotalPaise)).ToList();

        // ---------------- inward: purchases and debit notes ----------------
        long itcI = 0, itcC = 0, itcS = 0, inelI = 0, inelC = 0, inelS = 0, inwardNilIntra = 0, inwardNilInter = 0;
        var purchaseRegister = new List<PurchaseRegisterRow>();
        foreach (var p in purchases)
        {
            suppliers.TryGetValue(p.SupplierId, out var sup);
            long claimed = 0, notClaimed = 0;
            foreach (var l in p.Lines)
            {
                var tax = l.CgstPaise + l.SgstPaise + l.IgstPaise;
                if (l.GstRateBp == 0) { if (p.IsInterState) inwardNilInter += l.TaxablePaise; else inwardNilIntra += l.TaxablePaise; continue; }
                if (l.ItcEligible) { itcI += l.IgstPaise; itcC += l.CgstPaise; itcS += l.SgstPaise; claimed += tax; }
                else { inelI += l.IgstPaise; inelC += l.CgstPaise; inelS += l.SgstPaise; notClaimed += tax; }
            }
            purchaseRegister.Add(new PurchaseRegisterRow(p.PurchaseNumber, p.PurchaseDate, sup?.Name ?? "", sup?.Gstin, p.SupplierInvoiceNo, p.SupplierInvoiceDate,
                p.TaxablePaise, p.CgstPaise, p.SgstPaise, p.IgstPaise, claimed, notClaimed, p.TotalPaise));
            if (claimed > 0 && string.IsNullOrWhiteSpace(sup?.Gstin))
                warnings.Add($"Purchase {p.PurchaseNumber} claims input credit but the supplier ({sup?.Name}) has no GSTIN. Credit is only valid on a registered supplier's invoice.");
        }
        // Expenses with GST charged on the bill: credit claimed on a tax invoice joins the input credit; the rest is shown as not claimed.
        var monthExpenses = await db.Expenses.AsNoTracking().Where(e => e.Date >= from && e.Date < to && e.Status == ExpenseStatus.Posted && e.PaymentMode != PaymentMode.NonCash).OrderBy(e => e.Id).ToListAsync();
        foreach (var e in monthExpenses.Where(e => e.GstPaise > 0))
        {
            itcI += e.IgstPaise; itcC += e.CgstPaise; itcS += e.SgstPaise;
            purchaseRegister.Add(new PurchaseRegisterRow(e.ExpenseNumber, e.Date, string.IsNullOrWhiteSpace(e.PayTo) ? "(expense)" : e.PayTo, e.SupplierGstin, e.Reference ?? "", null,
                e.AmountPaise - e.GstPaise, e.CgstPaise, e.SgstPaise, e.IgstPaise, e.ItcPaise, e.GstPaise - e.ItcPaise, e.AmountPaise));
        }

        foreach (var r in purchaseReturns)
        {
            var orig = pOriginals[r.PurchaseId];
            var byLine = orig.Lines.ToDictionary(l => l.Id);
            suppliers.TryGetValue(r.SupplierId, out var sup);
            noteRegister.Add(new NoteRegisterRow("Debit note (to supplier)", r.ReturnNumber, r.Date, orig.PurchaseNumber, sup?.Name ?? "", sup?.Gstin,
                r.TaxablePaise, r.CgstPaise, r.SgstPaise, r.IgstPaise, r.TotalPaise));
            foreach (var l in r.Lines)
            {
                var pl = byLine[l.PurchaseLineId];
                if (pl.GstRateBp == 0) { if (orig.IsInterState) inwardNilInter -= l.TaxablePaise; else inwardNilIntra -= l.TaxablePaise; continue; }
                if (pl.ItcEligible) { itcI -= l.IgstPaise; itcC -= l.CgstPaise; itcS -= l.SgstPaise; }
                else { inelI -= l.IgstPaise; inelC -= l.CgstPaise; inelS -= l.SgstPaise; }
            }
        }

        // ---------------- GSTR-3B ----------------
        long outT = 0, outI = 0, outC = 0, outS = 0;
        var interUnreg = new Dictionary<string, long[]>();
        void Out(string pos, bool registered, bool inter, long t, long ig, long cg, long sg, int sign)
        {
            outT += sign * t; outI += sign * ig; outC += sign * cg; outS += sign * sg;
            if (!registered && inter)
            {
                if (!interUnreg.TryGetValue(pos, out var a)) interUnreg[pos] = a = new long[2];
                a[0] += sign * t; a[1] += sign * ig;
            }
        }
        foreach (var s in live)
            foreach (var l in s.Lines.Where(l => l.GstRateBp > 0))
                Out(Pos(s), Registered(s), s.IsInterState, l.TaxablePaise, l.IgstPaise, l.CgstPaise, l.SgstPaise, +1);
        foreach (var r in returns)
        {
            var orig = originals[r.SaleId];
            var byLine = orig.Lines.ToDictionary(l => l.Id);
            foreach (var l in r.Lines.Where(l => byLine[l.SaleLineId].GstRateBp > 0))
                Out(Pos(orig), Registered(orig), orig.IsInterState, l.TaxablePaise, l.IgstPaise, l.CgstPaise, l.SgstPaise, -1);
        }
        var nilOut = nil.Sum(n => n.TaxablePaise);
        var gstr3b = new Gstr3b(outT, outI, outC, outS, nilOut,
            interUnreg.Where(kv => kv.Value[0] != 0 || kv.Value[1] != 0).OrderBy(kv => kv.Key).Select(kv => new PosRow(kv.Key, kv.Value[0], kv.Value[1])).ToList(),
            itcI, itcC, itcS, inelI, inelC, inelS, inwardNilIntra, inwardNilInter,
            outI - itcI, outC - itcC, outS - itcS);

        // ---------------- checks for the accountant ----------------
        if (string.IsNullOrWhiteSpace(company.Gstin) && !company.IsComposition)
            warnings.Add("The business GSTIN is not set. Add it in Settings before using these figures for a return.");
        var unclaimed = monthExpenses.Where(e => e.ItcPaise == 0).ToList();
        if (unclaimed.Count > 0)
            warnings.Add($"{unclaimed.Count} business expense(s) totalling {Money.Format(unclaimed.Sum(e => e.AmountPaise))} were recorded this month and input credit was not claimed on them. "
                         + "If GST was charged on a tax invoice from a registered supplier, cancel the expense and enter it again with the GST amount, or let the accountant add it by hand.");
        var noHsn = live.SelectMany(s => s.Lines).Where(l => string.IsNullOrWhiteSpace(l.HsnCode)).Select(l => l.ProductName).Distinct().ToList();
        if (noHsn.Count > 0)
            warnings.Add($"{noHsn.Count} item(s) sold this month have no HSN code ({string.Join(", ", noHsn.Take(5))}{(noHsn.Count > 5 ? ", ..." : "")}). The HSN summary needs it.");
        var voided = sales.Count(s => s.Status == SaleStatus.Voided);
        if (voided > 0) warnings.Add($"{voided} bill(s) were cancelled this month. They are left out of the values and shown as cancelled in the document summary.");
        if (b2cl.Count > 0)
            warnings.Add($"{b2cl.Count} line(s) are listed as large inter-state consumer invoices (over {Money.Format(threshold)}). Check this limit with your accountant; it can be changed in Settings.");
        if (live.Any(s => s.PlaceOfSupply == null && s.IsInterState))
            warnings.Add("Some older inter-state bills do not store the buyer's state, so it was taken from the customer record or GSTIN.");
        warnings.Add("Reverse-charge purchases, exports, supplies through e-commerce operators and input-credit reversals are not recorded by this software. Add them by hand if they apply.");
        warnings.Add("Net tax is shown per tax head only. Rules for setting off one head against another are not applied. The accountant decides the final payment.");
        if (company.IsComposition)
            warnings.Add("This business is on the composition scheme: GSTR-1 and GSTR-3B do not apply. Use the composition turnover below for the composition return.");

        var compTurnover = company.IsComposition ? live.Sum(s => s.TaxablePaise) - returns.Sum(r => r.TaxablePaise) : 0;

        return new GstReport(year, month, from.ToString("MMMM yyyy"), company.Name, company.Gstin, company.StateCode, company.IsComposition, threshold,
            b2b, b2cl, b2cs, notes, hsn, docs, nil, gstr3b, salesRegister, purchaseRegister.ToList(), noteRegister, warnings, compTurnover);
    }
}
