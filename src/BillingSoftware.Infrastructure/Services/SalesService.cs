using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

internal sealed class SalesService(DbFactory factory, ICurrentUser current, ApprovalLedger ledger) : ServiceBase(factory, current), ISalesService
{
    private const int MaxLines = 300;

    public async Task<SaleResult> CompleteSaleAsync(CompleteSaleCommand cmd)
    {
        var user = RequireUser();
        if (cmd.Lines.Count == 0) throw new ValidationException("Add at least one item to the bill.");
        if (cmd.Lines.Count > MaxLines) throw new ValidationException($"A bill can have at most {MaxLines} lines.");
        if (cmd.Lines.Any(l => l.QtyMilli <= 0)) throw new ValidationException("Quantity must be greater than zero on every line.");
        if (cmd.Payments.Any(p => p.AmountPaise <= 0)) throw new ValidationException("Payment amounts must be greater than zero.");
        if (!string.IsNullOrWhiteSpace(cmd.CustomerGstin) && !GstValidation.IsValidGstin(cmd.CustomerGstin))
            throw new ValidationException("The customer GSTIN format is not valid.");

        await using var db = Factory.Create();

        // Idempotency: the same command id (for example a double click or retry) returns the original bill.
        var existing = await db.Sales.AsNoTracking().FirstOrDefaultAsync(s => s.CommandId == cmd.CommandId);
        if (existing != null) return new SaleResult(existing.Id, existing.InvoiceNumber, existing.TotalPaise, existing.RoundOffPaise, true);

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var company = await db.Companies.FirstOrDefaultAsync() ?? throw new BusinessRuleException("Company setup is not complete.");
            var now = DateTime.Now;
            await Posting.EnsurePeriodOpenAsync(db, now);
            var fy = await Posting.EnsureFinancialYearAsync(db, now);

            var ids = cmd.Lines.Select(l => l.ProductId).Distinct().ToList();
            var products = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
            foreach (var id in ids)
                if (!products.TryGetValue(id, out var pr) || !pr.IsActive) throw new ValidationException("An item on the bill is no longer available.");

            var maxDiscountBp = int.TryParse(await db.Settings.Where(s => s.Key == SettingKeys.CashierMaxDiscountBp).Select(s => s.Value).FirstOrDefaultAsync(), out var m) ? m : 0;
            // Place of supply: the customer's state, or the first two digits of their GSTIN when no state was set.
            var buyerState = !string.IsNullOrWhiteSpace(cmd.BuyerStateCode) ? cmd.BuyerStateCode.Trim()
                           : GstValidation.IsValidGstin(cmd.CustomerGstin) ? cmd.CustomerGstin!.Trim()[..2] : null;
            var interState = buyerState != null && !string.IsNullOrWhiteSpace(company.StateCode)
                             && !string.Equals(buyerState, company.StateCode, StringComparison.Ordinal);

            // ---- price and tax per line ----
            var sale = new Sale
            {
                CommandId = cmd.CommandId, FinancialYearId = fy.Id, SaleDate = now, CashierId = user.Id,
                PartyId = cmd.PartyId, CustomerName = cmd.CustomerName?.Trim(), CustomerGstin = cmd.CustomerGstin?.Trim().ToUpperInvariant(),
                IsInterState = interState, PlaceOfSupply = interState ? buyerState : company.StateCode,
                SeriesCode = company.CollectsTax ? "INV" : "BOS",
            };
            ApprovalReason? needsApproval = null;
            string approvalWhy = "";
            long exact = 0, cogs = 0;

            foreach (var l in cmd.Lines)
            {
                var p = products[l.ProductId];
                var unitPrice = l.UnitPriceOverridePaise ?? p.SellingPricePaise;
                if (unitPrice < 0) throw new ValidationException("Price cannot be negative.");
                if (l.UnitPriceOverridePaise.HasValue && l.UnitPriceOverridePaise.Value != p.SellingPricePaise)
                {
                    needsApproval ??= ApprovalReason.PriceOverride;
                    approvalWhy = $"Price override on {p.Name}";
                }

                var rate = company.CollectsTax ? p.GstRateBp : 0;
                LineTax t;
                try { t = TaxCalculator.Calculate(unitPrice, l.QtyMilli, l.DiscountPaise, rate, interState, p.PriceInclusive); }
                catch (ArgumentOutOfRangeException ex) { throw new ValidationException(ex.Message.Split('\n')[0]); }

                if (l.DiscountPaise > 0 && t.GrossPaise > 0 && l.DiscountPaise * 10000m / t.GrossPaise > maxDiscountBp)
                {
                    needsApproval ??= ApprovalReason.DiscountAboveLimit;
                    approvalWhy = $"Discount on {p.Name} is above the allowed limit";
                }

                var lineCost = Costing.Value(l.QtyMilli, p.AvgCostPaise);
                cogs += lineCost;
                exact += t.TotalPaise;
                sale.Lines.Add(new SaleLine
                {
                    ProductId = p.Id, ProductName = p.Name, TamilName = p.TamilName, HsnCode = p.HsnCode, Unit = p.Unit, QtyMilli = l.QtyMilli,
                    UnitPricePaise = unitPrice, PriceInclusive = p.PriceInclusive, DiscountPaise = l.DiscountPaise, GstRateBp = rate,
                    TaxablePaise = t.TaxablePaise, CgstPaise = t.CgstPaise, SgstPaise = t.SgstPaise, IgstPaise = t.IgstPaise,
                    LineTotalPaise = t.TotalPaise, UnitCostPaise = p.AvgCostPaise,
                });
            }

            // ---- stock availability (negative stock needs approval, decision D5) ----
            foreach (var g in cmd.Lines.GroupBy(l => l.ProductId))
            {
                var p = products[g.Key];
                if (g.Sum(l => l.QtyMilli) > p.QtyOnHandMilli)
                {
                    needsApproval ??= ApprovalReason.NegativeStock;
                    approvalWhy = $"Not enough stock of {p.Name} (available {Qty.Format(p.QtyOnHandMilli)})";
                }
            }

            // ---- credit limit: a credit sale that takes the customer over their limit needs the Admin ----
            var creditRequested = cmd.Payments.Where(p => p.Mode == PaymentMode.Credit).Sum(p => p.AmountPaise);
            if (creditRequested > 0 && cmd.PartyId is { } limitPartyId)
            {
                var limitParty = await db.Parties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == limitPartyId && p.IsActive);
                if (limitParty is { CreditLimitPaise: > 0 })
                {
                    var owedNow = await PartyService.OutstandingAsync(db, limitPartyId);
                    if (owedNow + creditRequested > limitParty.CreditLimitPaise)
                    {
                        needsApproval ??= ApprovalReason.CreditLimit;
                        approvalWhy = $"{limitParty.Name} owes {Money.Format(Math.Max(owedNow, 0))} and this bill would take it to {Money.Format(owedNow + creditRequested)}, above the credit limit of {Money.Format(limitParty.CreditLimitPaise)}";
                    }
                }
            }

            // ---- approval (restricted actions) ----
            long? approver = null;
            if (needsApproval != null && !user.Role.Equals(UserRole.Admin))
            {
                if (cmd.ApprovedByUserId is not { } adminId || !ledger.IsValid(adminId)
                    || !await db.Users.AnyAsync(u => u.Id == adminId && u.Role == UserRole.Admin && u.IsActive))
                    throw new ApprovalRequiredException(needsApproval.Value, approvalWhy + ". Admin approval is required.");
                approver = adminId;
            }

            // ---- totals, rounding, payments ----
            sale.TaxablePaise = sale.Lines.Sum(x => x.TaxablePaise);
            sale.CgstPaise = sale.Lines.Sum(x => x.CgstPaise);
            sale.SgstPaise = sale.Lines.Sum(x => x.SgstPaise);
            sale.IgstPaise = sale.Lines.Sum(x => x.IgstPaise);
            sale.DiscountPaise = sale.Lines.Sum(x => x.DiscountPaise);
            (sale.TotalPaise, sale.RoundOffPaise) = InvoiceRounding.ToNearestRupee(exact);
            sale.CogsPaise = cogs;
            sale.ApprovedByUserId = approver;
            sale.ApprovalNote = approver == null ? null : (cmd.ApprovalNote ?? approvalWhy);

            foreach (var p in cmd.Payments) PaymentModeRules.EnsureMoney(p.Mode, allowCredit: true);
            var paid = cmd.Payments.Sum(p => p.AmountPaise);
            if (paid != sale.TotalPaise)
                throw new ValidationException($"Payments ({Money.Format(paid)}) must equal the bill total ({Money.Format(sale.TotalPaise)}).");
            var creditAmount = cmd.Payments.Where(p => p.Mode == PaymentMode.Credit).Sum(p => p.AmountPaise);
            if (creditAmount > 0)
            {
                if (cmd.PartyId is not { } partyId || !await db.Parties.AnyAsync(p => p.Id == partyId && p.IsActive))
                    throw new ValidationException("Select a customer for a credit sale.");
            }
            foreach (var p in cmd.Payments) sale.Payments.Add(new SalePayment { Mode = p.Mode, AmountPaise = p.AmountPaise });

            // ---- invoice number (gapless per series and financial year, decision D7) ----
            var seq = await db.DocumentSequences.FirstOrDefaultAsync(s => s.SeriesCode == sale.SeriesCode && s.FinancialYearId == fy.Id);
            if (seq == null) { seq = new DocumentSequence { SeriesCode = sale.SeriesCode, FinancialYearId = fy.Id, NextNumber = 1 }; db.DocumentSequences.Add(seq); }
            sale.InvoiceNumber = $"{sale.SeriesCode}/{fy.Label}/{seq.NextNumber:D6}";
            seq.NextNumber++;

            db.Sales.Add(sale);
            await db.SaveChangesAsync();

            // ---- stock ----
            foreach (var line in sale.Lines)
            {
                var p = products[line.ProductId];
                p.QtyOnHandMilli -= line.QtyMilli;
                db.StockMovements.Add(new StockMovement
                {
                    ProductId = p.Id, Date = now, QtyDeltaMilli = -line.QtyMilli, UnitCostPaise = line.UnitCostPaise,
                    Type = StockMovementType.Sale, RefType = "Sale", RefId = sale.Id,
                });
            }

            // ---- accounting: balanced journal in the same transaction ----
            var drafts = PostingBuilder.ForSale(sale.TaxablePaise, sale.CgstPaise, sale.SgstPaise, sale.IgstPaise,
                sale.RoundOffPaise, sale.CogsPaise, sale.Payments.Select(p => (p.Mode, p.AmountPaise)));
            await Posting.PostAsync(db, now, $"Sale {sale.InvoiceNumber}", "Sale", sale.Id, user.Id, drafts);

            await AuditWriter.AppendAsync(db, user, "sale.completed", "Sale", sale.Id,
                $"{sale.InvoiceNumber} total {sale.TotalPaise}" + (approver != null ? $"; approved by admin #{approver}: {sale.ApprovalNote}" : ""));
            await tx.CommitAsync();
            return new SaleResult(sale.Id, sale.InvoiceNumber, sale.TotalPaise, sale.RoundOffPaise, false);
        }
        catch (DbUpdateException)
        {
            // A concurrent retry with the same command id may have won the race: return that bill instead of failing.
            await tx.RollbackAsync();
            await using var db2 = Factory.Create();
            var again = await db2.Sales.AsNoTracking().FirstOrDefaultAsync(s => s.CommandId == cmd.CommandId);
            if (again != null) return new SaleResult(again.Id, again.InvoiceNumber, again.TotalPaise, again.RoundOffPaise, true);
            throw;
        }
    }

    public async Task VoidSaleAsync(long saleId, string reason, long? approvedByUserId)
    {
        var user = RequireUser();
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3) throw new ValidationException("A reason is required to void a bill.");

        await using var db = Factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync();
        var sale = await db.Sales.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == saleId) ?? throw new ValidationException("Bill not found.");
        if (sale.Status == SaleStatus.Voided) throw new ValidationException("This bill is already voided.");
        if (await db.SaleReturns.AnyAsync(r => r.SaleId == sale.Id))
            throw new ValidationException("This bill already has returns, so it cannot be voided.");

        long? approver = null;
        if (!user.Role.Equals(UserRole.Admin))
        {
            if (approvedByUserId is not { } adminId || !ledger.IsValid(adminId)
                || !await db.Users.AnyAsync(u => u.Id == adminId && u.Role == UserRole.Admin && u.IsActive))
                throw new ApprovalRequiredException(ApprovalReason.VoidSale, "Voiding a bill needs Admin approval.");
            approver = adminId;
        }

        var now = DateTime.Now;
        await Posting.EnsurePeriodOpenAsync(db, now);
        await Posting.EnsurePeriodOpenAsync(db, sale.SaleDate);

        // Stock returns at the cost it was sold at.
        var ids = sale.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        foreach (var line in sale.Lines)
        {
            var p = products[line.ProductId];
            (p.QtyOnHandMilli, p.AvgCostPaise) = Costing.Receive(p.QtyOnHandMilli, p.AvgCostPaise, line.QtyMilli, line.UnitCostPaise);
            db.StockMovements.Add(new StockMovement
            {
                ProductId = p.Id, Date = now, QtyDeltaMilli = line.QtyMilli, UnitCostPaise = line.UnitCostPaise,
                Type = StockMovementType.SaleVoid, RefType = "Sale", RefId = sale.Id, Note = reason.Trim(),
            });
        }

        // Reverse the original journal; the original is never edited.
        var original = await db.Journals.Include(j => j.Lines).FirstAsync(j => j.SourceType == "Sale" && j.SourceId == sale.Id);
        var codes = await db.Accounts.ToDictionaryAsync(a => a.Id, a => a.Code);
        var reversal = PostingBuilder.Reverse(original.Lines.Select(l => new JournalLineDraft(codes[l.AccountId], l.DebitPaise, l.CreditPaise)));
        await Posting.PostAsync(db, now, $"Void of {sale.InvoiceNumber}", "SaleVoid", sale.Id, user.Id, reversal);

        sale.Status = SaleStatus.Voided;
        sale.VoidedAt = now;
        sale.VoidReason = reason.Trim();
        sale.VoidedByUserId = user.Id;
        await db.SaveChangesAsync();
        await AuditWriter.AppendAsync(db, user, "sale.voided", "Sale", sale.Id,
            $"{sale.InvoiceNumber} voided: {reason.Trim()}" + (approver != null ? $"; approved by admin #{approver}" : ""));
        await tx.CommitAsync();
    }

    public async Task<SaleDetailDto?> GetAsync(long saleId)
    {
        RequireUser();
        await using var db = Factory.Create();
        return await LoadDetailAsync(db, saleId);
    }

    public async Task<SaleDetailDto?> GetForReprintAsync(long saleId)
    {
        var user = RequireUser();
        await using var db = Factory.Create();
        var sale = await db.Sales.FirstOrDefaultAsync(s => s.Id == saleId);
        if (sale == null) return null;
        sale.ReprintCount++;
        await db.SaveChangesAsync();
        await AuditWriter.AppendAsync(db, user, "sale.reprinted", "Sale", sale.Id, $"{sale.InvoiceNumber} reprint #{sale.ReprintCount}");
        return await LoadDetailAsync(db, saleId);
    }

    public async Task<IReadOnlyList<SaleListItem>> ListAsync(DateOnly date)
    {
        RequireUser();
        var start = date.ToDateTime(TimeOnly.MinValue);
        var end = start.AddDays(1);
        await using var db = Factory.Create();
        var rows = await (from s in db.Sales.AsNoTracking()
                          where s.SaleDate >= start && s.SaleDate < end
                          join u in db.Users on s.CashierId equals u.Id
                          orderby s.Id descending
                          select new SaleListItem(s.Id, s.InvoiceNumber, s.SaleDate, u.DisplayName, s.CustomerName, s.TotalPaise, s.Status))
            .Take(1000).ToListAsync();
        // Cashiers only see their own bills; the Admin sees all.
        if (!Current.IsAdmin)
        {
            var me = Current.User!.DisplayName;
            rows = rows.Where(r => r.CashierName == me).ToList();
        }
        return rows;
    }

    private static async Task<SaleDetailDto?> LoadDetailAsync(AppDbContext db, long saleId)
    {
        var s = await db.Sales.AsNoTracking().Include(x => x.Lines).Include(x => x.Payments).FirstOrDefaultAsync(x => x.Id == saleId);
        if (s == null) return null;
        var cashier = await db.Users.Where(u => u.Id == s.CashierId).Select(u => u.DisplayName).FirstOrDefaultAsync() ?? "";
        return new SaleDetailDto(s.Id, s.InvoiceNumber, s.SaleDate, cashier, s.CustomerName, s.CustomerGstin, s.IsInterState,
            s.TaxablePaise, s.CgstPaise, s.SgstPaise, s.IgstPaise, s.DiscountPaise, s.RoundOffPaise, s.TotalPaise, s.Status, s.ReprintCount,
            s.Lines.Select(l => new SaleLineDto(l.ProductName, l.HsnCode, l.Unit, l.QtyMilli, l.UnitPricePaise, l.DiscountPaise, l.GstRateBp,
                l.TaxablePaise, l.CgstPaise + l.SgstPaise + l.IgstPaise, l.LineTotalPaise, l.TamilName)).ToList(),
            s.Payments.Select(p => new PaymentInput(p.Mode, p.AmountPaise)).ToList());
    }
}
