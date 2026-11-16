using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

internal static class Numbering
{
    /// <summary>Next gapless document number for a series within a financial year, for example CN/26-27/000001.</summary>
    public static async Task<string> NextAsync(AppDbContext db, string series, FinancialYear fy)
    {
        var seq = await db.DocumentSequences.FirstOrDefaultAsync(s => s.SeriesCode == series && s.FinancialYearId == fy.Id);
        if (seq == null) { seq = new DocumentSequence { SeriesCode = series, FinancialYearId = fy.Id, NextNumber = 1 }; db.DocumentSequences.Add(seq); }
        var number = $"{series}/{fy.Label}/{seq.NextNumber:D6}";
        seq.NextNumber++;
        return number;
    }
}

// =====================================================================================================
// Sales returns (credit notes)
// =====================================================================================================
internal sealed class SalesReturnService(DbFactory factory, ICurrentUser current, ApprovalLedger ledger)
    : ServiceBase(factory, current), ISalesReturnService
{
    private sealed record Calc(Sale Sale, List<SaleReturnLine> Lines, long Taxable, long Cgst, long Sgst, long Igst,
        long Exact, long Total, long RoundOff, long CogsReversal);

    public async Task<IReadOnlyList<ReturnableLineDto>> GetReturnableAsync(long saleId)
    {
        RequireUser();
        await using var db = Factory.Create();
        var sale = await db.Sales.AsNoTracking().Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == saleId)
                   ?? throw new ValidationException("Bill not found.");
        var ids = sale.Lines.Select(l => l.Id).ToList();
        var returned = await db.SaleReturnLines.AsNoTracking().Where(r => ids.Contains(r.SaleLineId))
            .GroupBy(r => r.SaleLineId).Select(g => new { Id = g.Key, Qty = g.Sum(x => x.QtyMilli) }).ToDictionaryAsync(x => x.Id, x => x.Qty);
        return sale.Lines.Select(l => new ReturnableLineDto(l.Id, l.ProductName, l.Unit, l.QtyMilli, returned.GetValueOrDefault(l.Id),
            Money.RoundHalfUp(l.LineTotalPaise * 1000m / l.QtyMilli))).ToList();
    }

    public async Task<ReturnPreview> PreviewAsync(SaleReturnCommand command)
    {
        RequireUser();
        await using var db = Factory.Create();
        var c = await CalcAsync(db, command);
        return new ReturnPreview(c.Taxable, c.Cgst + c.Sgst + c.Igst, c.RoundOff, c.Total);
    }

    private static async Task<Calc> CalcAsync(AppDbContext db, SaleReturnCommand cmd)
    {
        if (cmd.Lines.Count == 0) throw new ValidationException("Choose at least one item to return.");
        if (cmd.Lines.Any(l => l.QtyMilli <= 0)) throw new ValidationException("Return quantity must be greater than zero.");
        if (cmd.Lines.GroupBy(l => l.LineId).Any(g => g.Count() > 1)) throw new ValidationException("Each item can appear only once.");

        var sale = await db.Sales.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == cmd.SaleId)
                   ?? throw new ValidationException("Bill not found.");
        if (sale.Status != SaleStatus.Posted) throw new ValidationException("A voided bill cannot have returns.");

        var ids = sale.Lines.Select(l => l.Id).ToList();
        var returned = await db.SaleReturnLines.Where(r => ids.Contains(r.SaleLineId))
            .GroupBy(r => r.SaleLineId).Select(g => new { Id = g.Key, Qty = g.Sum(x => x.QtyMilli) }).ToDictionaryAsync(x => x.Id, x => x.Qty);

        var lines = new List<SaleReturnLine>();
        long taxable = 0, cgst = 0, sgst = 0, igst = 0, cogs = 0;
        foreach (var input in cmd.Lines)
        {
            var sl = sale.Lines.FirstOrDefault(x => x.Id == input.LineId) ?? throw new ValidationException("An item is not on this bill.");
            var already = returned.GetValueOrDefault(sl.Id);
            if (input.QtyMilli > sl.QtyMilli - already)
                throw new ValidationException($"Only {Qty.Format(sl.QtyMilli - already)} of {sl.ProductName} can still be returned.");

            var t = ReturnMath.Portion(sl.TaxablePaise, sl.QtyMilli, already, input.QtyMilli);
            var cg = ReturnMath.Portion(sl.CgstPaise, sl.QtyMilli, already, input.QtyMilli);
            var sg = ReturnMath.Portion(sl.SgstPaise, sl.QtyMilli, already, input.QtyMilli);
            var ig = ReturnMath.Portion(sl.IgstPaise, sl.QtyMilli, already, input.QtyMilli);
            lines.Add(new SaleReturnLine
            {
                SaleLineId = sl.Id, ProductId = sl.ProductId, QtyMilli = input.QtyMilli, Restock = input.Restock,
                TaxablePaise = t, CgstPaise = cg, SgstPaise = sg, IgstPaise = ig, LineTotalPaise = t + cg + sg + ig, UnitCostPaise = sl.UnitCostPaise,
            });
            taxable += t; cgst += cg; sgst += sg; igst += ig;
            if (input.Restock) cogs += Costing.Value(input.QtyMilli, sl.UnitCostPaise);
        }

        var exact = taxable + cgst + sgst + igst;
        var (total, roundOff) = InvoiceRounding.ToNearestRupee(exact);
        if (total <= 0) throw new ValidationException("The value of this return is too small to refund.");
        return new Calc(sale, lines, taxable, cgst, sgst, igst, exact, total, roundOff, cogs);
    }

    public async Task<ReturnResult> ReturnAsync(SaleReturnCommand cmd)
    {
        var user = RequireUser();
        if (string.IsNullOrWhiteSpace(cmd.Reason) || cmd.Reason.Trim().Length < 3) throw new ValidationException("A reason is required for a return.");

        await using var db = Factory.Create();
        var existing = await db.SaleReturns.AsNoTracking().FirstOrDefaultAsync(r => r.CommandId == cmd.CommandId);
        if (existing != null) return new ReturnResult(existing.Id, existing.ReturnNumber, existing.TotalPaise, true);

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var calc = await CalcAsync(db, cmd);

            long? approver = null;
            if (!user.Role.Equals(UserRole.Admin))
            {
                if (cmd.ApprovedByUserId is not { } adminId || !ledger.IsValid(adminId)
                    || !await db.Users.AnyAsync(u => u.Id == adminId && u.Role == UserRole.Admin && u.IsActive))
                    throw new ApprovalRequiredException(ApprovalReason.SaleReturn, "A return needs Admin approval.");
                approver = adminId;
            }

            PaymentModeRules.EnsureMoney(cmd.RefundMode, allowCredit: true);
            if (cmd.RefundMode == PaymentMode.Credit)
            {
                if (calc.Sale.PartyId is not { } partyId) throw new ValidationException("Only a bill made to a named customer can be refunded to their account.");
                var owed = await PartyService.OutstandingAsync(db, partyId);
                if (owed < calc.Total)
                    throw new ValidationException($"The customer owes only {Money.Format(Math.Max(owed, 0))}, less than the refund. Refund in cash, UPI or card instead.");
            }

            var now = DateTime.Now;
            await Posting.EnsurePeriodOpenAsync(db, now);
            var fy = await Posting.EnsureFinancialYearAsync(db, now);

            var ret = new SaleReturn
            {
                CommandId = cmd.CommandId, FinancialYearId = fy.Id, Date = now, SaleId = calc.Sale.Id, PartyId = calc.Sale.PartyId,
                Reason = cmd.Reason.Trim(), RefundMode = cmd.RefundMode, TaxablePaise = calc.Taxable, CgstPaise = calc.Cgst, SgstPaise = calc.Sgst,
                IgstPaise = calc.Igst, RoundOffPaise = calc.RoundOff, TotalPaise = calc.Total, CogsReversalPaise = calc.CogsReversal,
                UserId = user.Id, ApprovedByUserId = approver, Lines = calc.Lines,
            };
            ret.ReturnNumber = await Numbering.NextAsync(db, "CN", fy);
            db.SaleReturns.Add(ret);
            await db.SaveChangesAsync();

            var ids = calc.Lines.Where(l => l.Restock).Select(l => l.ProductId).Distinct().ToList();
            var products = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
            foreach (var l in calc.Lines.Where(l => l.Restock))
            {
                var p = products[l.ProductId];
                (p.QtyOnHandMilli, p.AvgCostPaise) = Costing.Receive(p.QtyOnHandMilli, p.AvgCostPaise, l.QtyMilli, l.UnitCostPaise);
                db.StockMovements.Add(new StockMovement
                {
                    ProductId = p.Id, Date = now, QtyDeltaMilli = l.QtyMilli, UnitCostPaise = l.UnitCostPaise,
                    Type = StockMovementType.SaleReturn, RefType = "SaleReturn", RefId = ret.Id, Note = cmd.Reason.Trim(),
                });
            }

            var drafts = PostingBuilder.ForSaleReturn(calc.Taxable, calc.Cgst, calc.Sgst, calc.Igst, calc.RoundOff, calc.CogsReversal,
                new[] { (cmd.RefundMode, calc.Total) });
            await Posting.PostAsync(db, now, $"Credit note {ret.ReturnNumber} against {calc.Sale.InvoiceNumber}", "SaleReturn", ret.Id, user.Id, drafts);
            await AuditWriter.AppendAsync(db, user, "sale.returned", "SaleReturn", ret.Id,
                $"{ret.ReturnNumber} against {calc.Sale.InvoiceNumber}, refund {calc.Total} via {cmd.RefundMode}: {cmd.Reason.Trim()}"
                + (approver != null ? $"; approved by admin #{approver}" : ""));
            await tx.CommitAsync();
            return new ReturnResult(ret.Id, ret.ReturnNumber, ret.TotalPaise, false);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync();
            await using var db2 = Factory.Create();
            var again = await db2.SaleReturns.AsNoTracking().FirstOrDefaultAsync(r => r.CommandId == cmd.CommandId);
            if (again != null) return new ReturnResult(again.Id, again.ReturnNumber, again.TotalPaise, true);
            throw;
        }
    }

    public async Task<ReturnDetailDto?> GetAsync(long returnId)
    {
        RequireUser();
        await using var db = Factory.Create();
        var r = await db.SaleReturns.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == returnId);
        if (r == null) return null;
        var sale = await db.Sales.AsNoTracking().Include(s => s.Lines).FirstAsync(s => s.Id == r.SaleId);
        var lines = r.Lines.Select(l =>
        {
            var sl = sale.Lines.First(x => x.Id == l.SaleLineId);
            return new ReturnLineDto(sl.ProductName, sl.HsnCode, sl.Unit, l.QtyMilli, l.TaxablePaise, l.CgstPaise + l.SgstPaise + l.IgstPaise, l.LineTotalPaise);
        }).ToList();
        return new ReturnDetailDto(r.Id, r.ReturnNumber, r.Date, sale.InvoiceNumber, string.IsNullOrWhiteSpace(sale.CustomerName) ? "Walk-in" : sale.CustomerName!,
            "Credit note", r.TaxablePaise, r.CgstPaise, r.SgstPaise, r.IgstPaise, r.RoundOffPaise, r.TotalPaise, r.RefundMode, r.Reason, lines);
    }

    public async Task<IReadOnlyList<ReturnListItem>> ListAsync(DateOnly date)
    {
        var user = RequireUser();
        var start = date.ToDateTime(TimeOnly.MinValue);
        var end = start.AddDays(1);
        await using var db = Factory.Create();
        var q = db.SaleReturns.AsNoTracking().Where(r => r.Date >= start && r.Date < end);
        if (user.Role != UserRole.Admin) q = q.Where(r => r.UserId == user.Id);
        return await (from r in q join s in db.Sales on r.SaleId equals s.Id orderby r.Id descending
                      select new ReturnListItem(r.Id, r.ReturnNumber, r.Date, s.InvoiceNumber, r.TotalPaise)).Take(500).ToListAsync();
    }
}

// =====================================================================================================
// Suppliers
// =====================================================================================================
internal sealed class SupplierService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), ISupplierService
{
    public async Task<IReadOnlyList<SupplierDto>> SearchAsync(string query, int take = 30)
    {
        RequireAdmin();
        query = (query ?? "").Trim();
        await using var db = Factory.Create();
        var q = db.Suppliers.AsNoTracking().Where(s => s.IsActive);
        if (query.Length > 0)
        {
            var like = Like.Contains(query);
            q = q.Where(s => EF.Functions.Like(s.Name, like, Like.Escape) || (s.Phone != null && EF.Functions.Like(s.Phone, like, Like.Escape)));
        }
        var rows = await q.OrderBy(s => s.Name).Take(Math.Clamp(take, 1, 200)).ToListAsync();
        var result = new List<SupplierDto>();
        foreach (var s in rows) result.Add(new SupplierDto(s.Id, s.Name, s.Phone, s.Gstin, s.StateCode, await PayableAsync(db, s.Id)));
        return result;
    }

    public async Task<SupplierDto> SaveAsync(SupplierDto dto)
    {
        var admin = RequireAdmin();
        var name = (dto.Name ?? "").Trim();
        if (name.Length == 0) throw new ValidationException("Supplier name is required.");
        if (!string.IsNullOrWhiteSpace(dto.Gstin) && !GstValidation.IsValidGstin(dto.Gstin)) throw new ValidationException("The GSTIN format is not valid.");
        var gstin = string.IsNullOrWhiteSpace(dto.Gstin) ? null : dto.Gstin.Trim().ToUpperInvariant();
        var state = string.IsNullOrWhiteSpace(dto.StateCode) ? gstin?[..2] : dto.StateCode.Trim();
        if (state != null && (state.Length != 2 || !state.All(char.IsDigit))) throw new ValidationException("State code must be 2 digits.");
        if (gstin != null && state != null && !gstin.StartsWith(state, StringComparison.Ordinal)) throw new ValidationException("The first two digits of the GSTIN must match the state.");

        await using var db = Factory.Create();
        Supplier entity;
        if (dto.Id == 0) { entity = new Supplier(); db.Suppliers.Add(entity); }
        else entity = await db.Suppliers.FindAsync(dto.Id) ?? throw new ValidationException("Supplier not found.");
        entity.Name = name; entity.Phone = dto.Phone?.Trim(); entity.Gstin = gstin; entity.StateCode = state;
        await db.SaveChangesAsync();
        await AuditWriter.AppendAsync(db, admin, dto.Id == 0 ? "supplier.created" : "supplier.updated", "Supplier", entity.Id, $"Supplier #{entity.Id}");
        return new SupplierDto(entity.Id, entity.Name, entity.Phone, entity.Gstin, entity.StateCode, await PayableAsync(db, entity.Id));
    }

    public async Task PayAsync(long supplierId, long amountPaise, PaymentMode mode)
    {
        var admin = RequireAdmin();
        if (amountPaise <= 0) throw new ValidationException("Amount must be greater than zero.");
        PaymentModeRules.EnsureMoney(mode);

        await using var db = Factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync();
        var now = DateTime.Now;
        await Posting.EnsurePeriodOpenAsync(db, now);
        var supplier = await db.Suppliers.FindAsync(supplierId) ?? throw new ValidationException("Supplier not found.");
        var owed = await PayableAsync(db, supplierId);
        if (amountPaise > owed) throw new ValidationException($"Amount is more than what you owe this supplier ({Money.Format(Math.Max(owed, 0))}).");

        var pay = new SupplierPayment { SupplierId = supplierId, Date = now, Mode = mode, AmountPaise = amountPaise, UserId = admin.Id };
        db.SupplierPayments.Add(pay);
        await db.SaveChangesAsync();
        await Posting.PostAsync(db, now, $"Payment to {supplier.Name}", "SupplierPayment", pay.Id, admin.Id, PostingBuilder.ForSupplierPayment(mode, amountPaise));
        await AuditWriter.AppendAsync(db, admin, "supplier.paid", "SupplierPayment", pay.Id, $"supplier {supplierId}, {amountPaise} paise, {mode}");
        await tx.CommitAsync();
    }

    /// <summary>What we owe a supplier: credit purchases, less payments made, less returns credited to the account.</summary>
    internal static async Task<long> PayableAsync(AppDbContext db, long supplierId)
    {
        var credit = await (from p in db.Purchases
                            where p.SupplierId == supplierId
                            from pay in p.Payments
                            where pay.Mode == PaymentMode.Credit
                            select (long?)pay.AmountPaise).SumAsync() ?? 0;
        var paid = await db.SupplierPayments.Where(x => x.SupplierId == supplierId).SumAsync(x => (long?)x.AmountPaise) ?? 0;
        var credited = await db.PurchaseReturns.Where(r => r.SupplierId == supplierId && r.RefundMode == PaymentMode.Credit).SumAsync(r => (long?)r.TotalPaise) ?? 0;
        return credit - paid - credited + await OpeningDues.SupplierAsync(db, supplierId);
    }
}

// =====================================================================================================
// Purchases and returns to suppliers (debit notes)
// =====================================================================================================
internal sealed class PurchaseService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), IPurchaseService
{
    private const int MaxLines = 300;

    public async Task<PurchaseResult> CompleteAsync(CompletePurchaseCommand cmd)
    {
        var admin = RequireAdmin();
        if (cmd.Lines.Count == 0) throw new ValidationException("Add at least one item to the purchase.");
        if (cmd.Lines.Count > MaxLines) throw new ValidationException($"A purchase can have at most {MaxLines} lines.");
        if (cmd.Lines.Any(l => l.QtyMilli <= 0)) throw new ValidationException("Quantity must be greater than zero on every line.");
        if (cmd.Lines.Any(l => l.UnitCostPaise < 0 || l.DiscountPaise < 0)) throw new ValidationException("Cost and discount cannot be negative.");
        if (cmd.Lines.Any(l => l.GstRateBp is < 0 or > 5000)) throw new ValidationException("GST rate must be between 0% and 50%.");
        if (cmd.Payments.Any(p => p.AmountPaise <= 0)) throw new ValidationException("Payment amounts must be greater than zero.");
        var invoiceNo = (cmd.SupplierInvoiceNo ?? "").Trim();
        if (invoiceNo.Length == 0) throw new ValidationException("Enter the supplier's invoice number.");
        if (invoiceNo.Length > 40) throw new ValidationException("The supplier invoice number is too long.");
        if (cmd.SupplierInvoiceDate is { } d && d.Date > DateTime.Today) throw new ValidationException("The supplier invoice date cannot be in the future.");

        await using var db = Factory.Create();
        var existing = await db.Purchases.AsNoTracking().FirstOrDefaultAsync(p => p.CommandId == cmd.CommandId);
        if (existing != null) return new PurchaseResult(existing.Id, existing.PurchaseNumber, existing.TotalPaise, true);

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var company = await db.Companies.FirstOrDefaultAsync() ?? throw new BusinessRuleException("Company setup is not complete.");
            var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == cmd.SupplierId && s.IsActive) ?? throw new ValidationException("Choose a supplier.");
            if (await db.Purchases.AnyAsync(p => p.SupplierId == supplier.Id && p.SupplierInvoiceNo == invoiceNo))
                throw new ValidationException("This supplier invoice number has already been entered.");

            var now = DateTime.Now;
            await Posting.EnsurePeriodOpenAsync(db, now);
            var fy = await Posting.EnsureFinancialYearAsync(db, now);
            var interState = !string.IsNullOrWhiteSpace(supplier.StateCode) && !string.IsNullOrWhiteSpace(company.StateCode)
                             && supplier.StateCode != company.StateCode;

            var ids = cmd.Lines.Select(l => l.ProductId).Distinct().ToList();
            var products = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
            foreach (var id in ids)
                if (!products.TryGetValue(id, out var pr) || !pr.IsActive) throw new ValidationException("An item on the purchase is no longer available.");

            var purchase = new Purchase
            {
                CommandId = cmd.CommandId, FinancialYearId = fy.Id, PurchaseDate = now, SupplierId = supplier.Id, SupplierInvoiceNo = invoiceNo,
                SupplierInvoiceDate = cmd.SupplierInvoiceDate?.Date, IsInterState = interState, CreatedByUserId = admin.Id,
            };
            long exact = 0, inventoryCost = 0, inCgst = 0, inSgst = 0, inIgst = 0;
            foreach (var l in cmd.Lines)
            {
                var p = products[l.ProductId];
                var rate = l.GstRateBp ?? p.GstRateBp;
                LineTax t;
                try { t = TaxCalculator.Calculate(l.UnitCostPaise, l.QtyMilli, l.DiscountPaise, rate, interState, priceInclusive: false); }
                catch (ArgumentOutOfRangeException ex) { throw new ValidationException(ex.Message.Split('\n')[0]); }

                var eligible = l.ItcEligible && company.CollectsTax;        // composition dealers cannot claim input credit
                var invCost = t.TaxablePaise + (eligible ? 0 : t.TaxPaise);
                purchase.Lines.Add(new PurchaseLine
                {
                    ProductId = p.Id, ProductName = p.Name, HsnCode = p.HsnCode, Unit = p.Unit, QtyMilli = l.QtyMilli, UnitCostPaise = l.UnitCostPaise,
                    DiscountPaise = l.DiscountPaise, GstRateBp = rate, TaxablePaise = t.TaxablePaise, CgstPaise = t.CgstPaise, SgstPaise = t.SgstPaise,
                    IgstPaise = t.IgstPaise, LineTotalPaise = t.TotalPaise, ItcEligible = eligible, InventoryCostPaise = invCost,
                });
                exact += t.TotalPaise; inventoryCost += invCost;
                if (eligible) { inCgst += t.CgstPaise; inSgst += t.SgstPaise; inIgst += t.IgstPaise; }
            }

            purchase.TaxablePaise = purchase.Lines.Sum(x => x.TaxablePaise);
            purchase.CgstPaise = purchase.Lines.Sum(x => x.CgstPaise);
            purchase.SgstPaise = purchase.Lines.Sum(x => x.SgstPaise);
            purchase.IgstPaise = purchase.Lines.Sum(x => x.IgstPaise);
            (purchase.TotalPaise, purchase.RoundOffPaise) = InvoiceRounding.ToNearestRupee(exact);

            foreach (var p in cmd.Payments) PaymentModeRules.EnsureMoney(p.Mode, allowCredit: true);
            var paid = cmd.Payments.Sum(p => p.AmountPaise);
            if (paid != purchase.TotalPaise)
                throw new ValidationException($"Payments ({Money.Format(paid)}) must equal the purchase total ({Money.Format(purchase.TotalPaise)}).");
            foreach (var p in cmd.Payments) purchase.Payments.Add(new PurchasePayment { Mode = p.Mode, AmountPaise = p.AmountPaise });

            purchase.PurchaseNumber = await Numbering.NextAsync(db, "PUR", fy);
            db.Purchases.Add(purchase);
            await db.SaveChangesAsync();

            long variance = 0;
            foreach (var line in purchase.Lines)
            {
                var p = products[line.ProductId];
                var unitCost = Money.RoundHalfUp(line.InventoryCostPaise * 1000m / line.QtyMilli);
                variance += Costing.NegativeStockVariance(p.QtyOnHandMilli, p.AvgCostPaise, line.QtyMilli, unitCost);
                (p.QtyOnHandMilli, p.AvgCostPaise) = Costing.Receive(p.QtyOnHandMilli, p.AvgCostPaise, line.QtyMilli, unitCost);
                db.StockMovements.Add(new StockMovement
                {
                    ProductId = p.Id, Date = now, QtyDeltaMilli = line.QtyMilli, UnitCostPaise = unitCost,
                    Type = StockMovementType.Purchase, RefType = "Purchase", RefId = purchase.Id,
                });
            }

            var drafts = PostingBuilder.ForPurchase(inventoryCost, inCgst, inSgst, inIgst, purchase.RoundOffPaise, purchase.Payments.Select(p => (p.Mode, p.AmountPaise)));
            await Posting.PostAsync(db, now, $"Purchase {purchase.PurchaseNumber} from {supplier.Name}", "Purchase", purchase.Id, admin.Id, drafts);
            if (variance != 0)
                await Posting.PostAsync(db, now, $"Cost variance on negative stock ({purchase.PurchaseNumber})", "PurchaseVariance", purchase.Id, admin.Id, PostingBuilder.ForCostVariance(variance));
            await AuditWriter.AppendAsync(db, admin, "purchase.created", "Purchase", purchase.Id,
                $"{purchase.PurchaseNumber}, supplier invoice {invoiceNo}, total {purchase.TotalPaise}");
            await tx.CommitAsync();
            return new PurchaseResult(purchase.Id, purchase.PurchaseNumber, purchase.TotalPaise, false);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync();
            await using var db2 = Factory.Create();
            var again = await db2.Purchases.AsNoTracking().FirstOrDefaultAsync(p => p.CommandId == cmd.CommandId);
            if (again != null) return new PurchaseResult(again.Id, again.PurchaseNumber, again.TotalPaise, true);
            throw;
        }
    }

    public async Task<PurchaseDetailDto?> GetAsync(long purchaseId)
    {
        RequireAdmin();
        await using var db = Factory.Create();
        var p = await db.Purchases.AsNoTracking().Include(x => x.Lines).Include(x => x.Payments).FirstOrDefaultAsync(x => x.Id == purchaseId);
        if (p == null) return null;
        var supplier = await db.Suppliers.AsNoTracking().Where(s => s.Id == p.SupplierId).Select(s => s.Name).FirstOrDefaultAsync() ?? "";
        return new PurchaseDetailDto(p.Id, p.PurchaseNumber, p.PurchaseDate, supplier, p.SupplierInvoiceNo, p.SupplierInvoiceDate, p.IsInterState,
            p.TaxablePaise, p.CgstPaise, p.SgstPaise, p.IgstPaise, p.RoundOffPaise, p.TotalPaise,
            p.Lines.Select(l => new PurchaseLineDto(l.ProductName, l.HsnCode, l.Unit, l.QtyMilli, l.UnitCostPaise, l.DiscountPaise, l.GstRateBp,
                l.TaxablePaise, l.CgstPaise + l.SgstPaise + l.IgstPaise, l.LineTotalPaise, l.ItcEligible)).ToList(),
            p.Payments.Select(x => new PaymentInput(x.Mode, x.AmountPaise)).ToList());
    }

    public async Task<IReadOnlyList<PurchaseListItem>> ListAsync(DateOnly from, DateOnly to)
    {
        RequireAdmin();
        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = to.ToDateTime(TimeOnly.MinValue).AddDays(1);
        await using var db = Factory.Create();
        return await (from p in db.Purchases.AsNoTracking()
                      where p.PurchaseDate >= start && p.PurchaseDate < end
                      join s in db.Suppliers on p.SupplierId equals s.Id
                      orderby p.Id descending
                      select new PurchaseListItem(p.Id, p.PurchaseNumber, p.PurchaseDate, s.Name, p.SupplierInvoiceNo, p.TotalPaise)).Take(1000).ToListAsync();
    }

    // ---------- returns to suppliers ----------
    private sealed record RCalc(Purchase Purchase, List<PurchaseReturnLine> Lines, long Taxable, long Cgst, long Sgst, long Igst,
        long InventoryCost, long InCgst, long InSgst, long InIgst, long Total, long RoundOff);

    public async Task<IReadOnlyList<ReturnableLineDto>> GetReturnableAsync(long purchaseId)
    {
        RequireAdmin();
        await using var db = Factory.Create();
        var p = await db.Purchases.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == purchaseId)
                ?? throw new ValidationException("Purchase not found.");
        var ids = p.Lines.Select(l => l.Id).ToList();
        var returned = await db.PurchaseReturnLines.AsNoTracking().Where(r => ids.Contains(r.PurchaseLineId))
            .GroupBy(r => r.PurchaseLineId).Select(g => new { Id = g.Key, Qty = g.Sum(x => x.QtyMilli) }).ToDictionaryAsync(x => x.Id, x => x.Qty);
        return p.Lines.Select(l => new ReturnableLineDto(l.Id, l.ProductName, l.Unit, l.QtyMilli, returned.GetValueOrDefault(l.Id),
            Money.RoundHalfUp(l.LineTotalPaise * 1000m / l.QtyMilli))).ToList();
    }

    public async Task<ReturnPreview> PreviewReturnAsync(PurchaseReturnCommand command)
    {
        RequireAdmin();
        await using var db = Factory.Create();
        var c = await CalcReturnAsync(db, command);
        return new ReturnPreview(c.Taxable, c.Cgst + c.Sgst + c.Igst, c.RoundOff, c.Total);
    }

    private static async Task<RCalc> CalcReturnAsync(AppDbContext db, PurchaseReturnCommand cmd)
    {
        if (cmd.Lines.Count == 0) throw new ValidationException("Choose at least one item to send back.");
        if (cmd.Lines.Any(l => l.QtyMilli <= 0)) throw new ValidationException("Return quantity must be greater than zero.");
        if (cmd.Lines.GroupBy(l => l.LineId).Any(g => g.Count() > 1)) throw new ValidationException("Each item can appear only once.");

        var purchase = await db.Purchases.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == cmd.PurchaseId)
                       ?? throw new ValidationException("Purchase not found.");
        var ids = purchase.Lines.Select(l => l.Id).ToList();
        var returned = await db.PurchaseReturnLines.Where(r => ids.Contains(r.PurchaseLineId))
            .GroupBy(r => r.PurchaseLineId).Select(g => new { Id = g.Key, Qty = g.Sum(x => x.QtyMilli) }).ToDictionaryAsync(x => x.Id, x => x.Qty);
        var productIds = purchase.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        var lines = new List<PurchaseReturnLine>();
        long taxable = 0, cgst = 0, sgst = 0, igst = 0, invCost = 0, inCgst = 0, inSgst = 0, inIgst = 0;
        foreach (var input in cmd.Lines)
        {
            var pl = purchase.Lines.FirstOrDefault(x => x.Id == input.LineId) ?? throw new ValidationException("An item is not on this purchase.");
            var already = returned.GetValueOrDefault(pl.Id);
            if (input.QtyMilli > pl.QtyMilli - already)
                throw new ValidationException($"Only {Qty.Format(pl.QtyMilli - already)} of {pl.ProductName} can still be sent back.");
            var onHand = products[pl.ProductId].QtyOnHandMilli;
            if (input.QtyMilli > onHand)
                throw new ValidationException($"Not enough stock of {pl.ProductName} to send back (in stock: {Qty.Format(onHand)}).");

            var t = ReturnMath.Portion(pl.TaxablePaise, pl.QtyMilli, already, input.QtyMilli);
            var cg = ReturnMath.Portion(pl.CgstPaise, pl.QtyMilli, already, input.QtyMilli);
            var sg = ReturnMath.Portion(pl.SgstPaise, pl.QtyMilli, already, input.QtyMilli);
            var ig = ReturnMath.Portion(pl.IgstPaise, pl.QtyMilli, already, input.QtyMilli);
            var tax = cg + sg + ig;
            var inv = t + (pl.ItcEligible ? 0 : tax);
            lines.Add(new PurchaseReturnLine
            {
                PurchaseLineId = pl.Id, ProductId = pl.ProductId, QtyMilli = input.QtyMilli, TaxablePaise = t, CgstPaise = cg, SgstPaise = sg,
                IgstPaise = ig, LineTotalPaise = t + tax, InventoryCostPaise = inv,
                UnitCostPaise = Money.RoundHalfUp(pl.InventoryCostPaise * 1000m / pl.QtyMilli),
            });
            taxable += t; cgst += cg; sgst += sg; igst += ig; invCost += inv;
            if (pl.ItcEligible) { inCgst += cg; inSgst += sg; inIgst += ig; }
        }

        var exact = taxable + cgst + sgst + igst;
        var (total, roundOff) = InvoiceRounding.ToNearestRupee(exact);
        if (total <= 0) throw new ValidationException("The value of this return is too small.");
        return new RCalc(purchase, lines, taxable, cgst, sgst, igst, invCost, inCgst, inSgst, inIgst, total, roundOff);
    }

    public async Task<ReturnResult> ReturnAsync(PurchaseReturnCommand cmd)
    {
        var admin = RequireAdmin();
        if (string.IsNullOrWhiteSpace(cmd.Reason) || cmd.Reason.Trim().Length < 3) throw new ValidationException("A reason is required.");

        await using var db = Factory.Create();
        var existing = await db.PurchaseReturns.AsNoTracking().FirstOrDefaultAsync(r => r.CommandId == cmd.CommandId);
        if (existing != null) return new ReturnResult(existing.Id, existing.ReturnNumber, existing.TotalPaise, true);

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var calc = await CalcReturnAsync(db, cmd);
            PaymentModeRules.EnsureMoney(cmd.RefundMode, allowCredit: true);
            if (cmd.RefundMode == PaymentMode.Credit)
            {
                var owed = await SupplierService.PayableAsync(db, calc.Purchase.SupplierId);
                if (owed < calc.Total)
                    throw new ValidationException($"You owe this supplier only {Money.Format(Math.Max(owed, 0))}, less than the return. Ask for a cash, UPI or card refund instead.");
            }

            var now = DateTime.Now;
            await Posting.EnsurePeriodOpenAsync(db, now);
            var fy = await Posting.EnsureFinancialYearAsync(db, now);

            var ret = new PurchaseReturn
            {
                CommandId = cmd.CommandId, FinancialYearId = fy.Id, Date = now, PurchaseId = calc.Purchase.Id, SupplierId = calc.Purchase.SupplierId,
                Reason = cmd.Reason.Trim(), RefundMode = cmd.RefundMode, TaxablePaise = calc.Taxable, CgstPaise = calc.Cgst, SgstPaise = calc.Sgst,
                IgstPaise = calc.Igst, RoundOffPaise = calc.RoundOff, TotalPaise = calc.Total, UserId = admin.Id, Lines = calc.Lines,
            };
            ret.ReturnNumber = await Numbering.NextAsync(db, "DN", fy);
            db.PurchaseReturns.Add(ret);
            await db.SaveChangesAsync();

            var ids = calc.Lines.Select(l => l.ProductId).Distinct().ToList();
            var products = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
            foreach (var l in calc.Lines)
            {
                var p = products[l.ProductId];
                (p.QtyOnHandMilli, p.AvgCostPaise) = Costing.Remove(p.QtyOnHandMilli, p.AvgCostPaise, l.QtyMilli, l.UnitCostPaise);
                db.StockMovements.Add(new StockMovement
                {
                    ProductId = p.Id, Date = now, QtyDeltaMilli = -l.QtyMilli, UnitCostPaise = l.UnitCostPaise,
                    Type = StockMovementType.PurchaseReturn, RefType = "PurchaseReturn", RefId = ret.Id, Note = cmd.Reason.Trim(),
                });
            }

            var drafts = PostingBuilder.ForPurchaseReturn(calc.InventoryCost, calc.InCgst, calc.InSgst, calc.InIgst, calc.RoundOff, new[] { (cmd.RefundMode, calc.Total) });
            await Posting.PostAsync(db, now, $"Debit note {ret.ReturnNumber} against {calc.Purchase.PurchaseNumber}", "PurchaseReturn", ret.Id, admin.Id, drafts);
            await AuditWriter.AppendAsync(db, admin, "purchase.returned", "PurchaseReturn", ret.Id,
                $"{ret.ReturnNumber} against {calc.Purchase.PurchaseNumber}, {calc.Total} via {cmd.RefundMode}: {cmd.Reason.Trim()}");
            await tx.CommitAsync();
            return new ReturnResult(ret.Id, ret.ReturnNumber, ret.TotalPaise, false);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync();
            await using var db2 = Factory.Create();
            var again = await db2.PurchaseReturns.AsNoTracking().FirstOrDefaultAsync(r => r.CommandId == cmd.CommandId);
            if (again != null) return new ReturnResult(again.Id, again.ReturnNumber, again.TotalPaise, true);
            throw;
        }
    }

    public async Task<ReturnDetailDto?> GetReturnAsync(long returnId)
    {
        RequireAdmin();
        await using var db = Factory.Create();
        var r = await db.PurchaseReturns.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == returnId);
        if (r == null) return null;
        var purchase = await db.Purchases.AsNoTracking().Include(p => p.Lines).FirstAsync(p => p.Id == r.PurchaseId);
        var supplier = await db.Suppliers.AsNoTracking().Where(s => s.Id == r.SupplierId).Select(s => s.Name).FirstOrDefaultAsync() ?? "";
        var lines = r.Lines.Select(l =>
        {
            var pl = purchase.Lines.First(x => x.Id == l.PurchaseLineId);
            return new ReturnLineDto(pl.ProductName, pl.HsnCode, pl.Unit, l.QtyMilli, l.TaxablePaise, l.CgstPaise + l.SgstPaise + l.IgstPaise, l.LineTotalPaise);
        }).ToList();
        return new ReturnDetailDto(r.Id, r.ReturnNumber, r.Date, purchase.PurchaseNumber, supplier, "Debit note",
            r.TaxablePaise, r.CgstPaise, r.SgstPaise, r.IgstPaise, r.RoundOffPaise, r.TotalPaise, r.RefundMode, r.Reason, lines);
    }

    public async Task<IReadOnlyList<ReturnListItem>> ListReturnsAsync(DateOnly from, DateOnly to)
    {
        RequireAdmin();
        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = to.ToDateTime(TimeOnly.MinValue).AddDays(1);
        await using var db = Factory.Create();
        return await (from r in db.PurchaseReturns.AsNoTracking()
                      where r.Date >= start && r.Date < end
                      join p in db.Purchases on r.PurchaseId equals p.Id
                      orderby r.Id descending
                      select new ReturnListItem(r.Id, r.ReturnNumber, r.Date, p.PurchaseNumber, r.TotalPaise)).Take(500).ToListAsync();
    }
}
