using BillingSoftware.Domain;
using Xunit;

namespace BillingSoftware.Tests;

public class TaxCalculatorTests
{
    [Fact]
    public void Inclusive_price_intra_state_splits_into_cgst_and_sgst()
    {
        // MRP 118.00 inclusive of 18% GST => taxable 100.00, tax 18.00 (9 + 9)
        var t = TaxCalculator.Calculate(11800, 1000, 0, 1800, interState: false, priceInclusive: true);
        Assert.Equal(10000, t.TaxablePaise);
        Assert.Equal(900, t.CgstPaise);
        Assert.Equal(900, t.SgstPaise);
        Assert.Equal(0, t.IgstPaise);
        Assert.Equal(11800, t.TotalPaise);
    }

    [Fact]
    public void Exclusive_price_adds_tax_on_top()
    {
        var t = TaxCalculator.Calculate(10000, 2000, 0, 1200, interState: false, priceInclusive: false);
        Assert.Equal(20000, t.TaxablePaise);
        Assert.Equal(2400, t.TaxPaise);
        Assert.Equal(22400, t.TotalPaise);
    }

    [Fact]
    public void Inter_state_uses_igst_only()
    {
        var t = TaxCalculator.Calculate(11800, 1000, 0, 1800, interState: true, priceInclusive: true);
        Assert.Equal(1800, t.IgstPaise);
        Assert.Equal(0, t.CgstPaise + t.SgstPaise);
    }

    [Fact]
    public void Odd_paisa_of_tax_goes_to_sgst_so_the_split_always_adds_up()
    {
        // 5% on 0.30 = 1.5 paise -> rounds to 2 paise; cgst 1, sgst 1. Try an odd total: 5% on 0.70 = 3.5 -> 4? use explicit
        var t = TaxCalculator.Calculate(1010, 1000, 0, 500, interState: false, priceInclusive: false); // tax 50.5 -> 51
        Assert.Equal(51, t.TaxPaise);
        Assert.Equal(25, t.CgstPaise);
        Assert.Equal(26, t.SgstPaise);
        Assert.Equal(t.TaxPaise, t.CgstPaise + t.SgstPaise);
    }

    [Fact]
    public void Zero_rate_has_no_tax()
    {
        var t = TaxCalculator.Calculate(5000, 3000, 0, 0, false, true);
        Assert.Equal(15000, t.TotalPaise);
        Assert.Equal(0, t.TaxPaise);
    }

    [Fact]
    public void Discount_reduces_taxable_value_before_tax()
    {
        // 118.00 inclusive, 18.00 discount => net 100.00 inclusive => taxable 84.75 (rounded), tax 15.25
        var t = TaxCalculator.Calculate(11800, 1000, 1800, 1800, false, true);
        Assert.Equal(10000, t.TotalPaise);
        Assert.Equal(8475, t.TaxablePaise);
        Assert.Equal(1525, t.TaxPaise);
    }

    [Fact]
    public void Fractional_quantity_is_supported()
    {
        // 0.250 kg at 400.00/kg, 5% exclusive
        var t = TaxCalculator.Calculate(40000, 250, 0, 500, false, false);
        Assert.Equal(10000, t.TaxablePaise);
        Assert.Equal(500, t.TaxPaise);
    }

    [Theory]
    [InlineData(0L, 0L)]            // zero quantity
    [InlineData(-1000L, 0L)]        // negative quantity
    public void Invalid_quantity_is_rejected(long qty, long discount) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TaxCalculator.Calculate(100, qty, discount, 1800, false, true));

    [Fact]
    public void Discount_larger_than_line_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TaxCalculator.Calculate(100, 1000, 101, 1800, false, true));

    [Fact]
    public void Negative_price_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TaxCalculator.Calculate(-1, 1000, 0, 1800, false, true));

    [Theory]
    [InlineData(104950L, 105000L, 50L)]    // 1049.50 -> 1050 (half rounds up)
    [InlineData(104949L, 104900L, -49L)]
    [InlineData(100000L, 100000L, 0L)]
    [InlineData(100050L, 100100L, 50L)]
    public void Invoice_rounds_to_nearest_rupee(long exact, long total, long roundOff)
    {
        var (t, r) = InvoiceRounding.ToNearestRupee(exact);
        Assert.Equal(total, t);
        Assert.Equal(roundOff, r);
        Assert.Equal(exact + r, t);
    }

    [Theory]
    [InlineData("2026-04-01", "26-27")]
    [InlineData("2027-03-31", "26-27")]
    [InlineData("2026-03-31", "25-26")]
    [InlineData("2026-11-16", "26-27")]
    [InlineData("2099-12-31", "99-00")]
    public void Financial_year_label(string date, string label) =>
        Assert.Equal(label, FinancialYears.For(DateTime.Parse(date)).Label);

    [Fact]
    public void Money_formats_with_indian_grouping_when_available()
    {
        var s = Money.Format(12345678);
        Assert.StartsWith("₹", s);
        Assert.EndsWith(".78", s);
    }
}

public class PostingTests
{
    private static long Dr(IEnumerable<JournalLineDraft> l) => l.Sum(x => x.DebitPaise);
    private static long Cr(IEnumerable<JournalLineDraft> l) => l.Sum(x => x.CreditPaise);

    [Theory]
    [InlineData(10000L, 900L, 900L, 0L, 0L, 6000L)]
    [InlineData(10000L, 0L, 0L, 1800L, 0L, 6000L)]
    [InlineData(8475L, 763L, 762L, 0L, 50L, 5000L)]     // positive round-off
    [InlineData(8475L, 763L, 762L, 0L, -49L, 5000L)]    // negative round-off
    [InlineData(5000L, 0L, 0L, 0L, 0L, 0L)]             // no cost, no tax
    public void Sale_journal_always_balances(long taxable, long cgst, long sgst, long igst, long roundOff, long cogs)
    {
        var total = taxable + cgst + sgst + igst + roundOff;
        var lines = PostingBuilder.ForSale(taxable, cgst, sgst, igst, roundOff, cogs,
            new[] { (PaymentMode.Cash, total / 2), (PaymentMode.Upi, total - total / 2) });
        Assert.Equal(Dr(lines), Cr(lines));
        Assert.All(lines, l => Assert.True(l.DebitPaise == 0 || l.CreditPaise == 0));
    }

    [Fact]
    public void Credit_sale_debits_receivables()
    {
        var lines = PostingBuilder.ForSale(10000, 900, 900, 0, 0, 6000, new[] { (PaymentMode.Credit, 11800L) });
        Assert.Contains(lines, l => l.AccountCode == AccountCodes.Receivables && l.DebitPaise == 11800);
    }

    [Fact]
    public void Reverse_swaps_sides_and_still_balances()
    {
        var sale = PostingBuilder.ForSale(10000, 900, 900, 0, 0, 6000, new[] { (PaymentMode.Cash, 11800L) });
        var rev = PostingBuilder.Reverse(sale);
        Assert.Equal(Dr(sale), Cr(rev));
        Assert.Equal(Cr(sale), Dr(rev));
    }

    [Fact]
    public void Unbalanced_journal_is_rejected() =>
        Assert.Throws<UnbalancedJournalException>(() =>
            PostingBuilder.EnsureBalanced(new[] { new JournalLineDraft("1000", 100, 0), new JournalLineDraft("4000", 0, 99) }));

    [Fact]
    public void A_line_with_both_sides_is_rejected() =>
        Assert.Throws<UnbalancedJournalException>(() =>
            PostingBuilder.EnsureBalanced(new[] { new JournalLineDraft("1000", 100, 100) }));

    [Fact]
    public void Stock_loss_and_gain_balance()
    {
        Assert.Equal(Dr(PostingBuilder.ForStockLoss(500)), Cr(PostingBuilder.ForStockLoss(500)));
        Assert.Equal(Dr(PostingBuilder.ForStockLoss(-500)), Cr(PostingBuilder.ForStockLoss(-500)));
    }
}

public class CostingTests
{
    [Fact]
    public void Weighted_average_blends_cost()
    {
        // 10 units at 60.00, receive 10 at 80.00 => 20 units at 70.00
        var (q, a) = Costing.Receive(10000, 6000, 10000, 8000);
        Assert.Equal(20000, q);
        Assert.Equal(7000, a);
    }

    [Fact]
    public void First_receipt_sets_cost()
    {
        var (q, a) = Costing.Receive(0, 0, 5000, 4200);
        Assert.Equal(5000, q);
        Assert.Equal(4200, a);
    }

    [Fact]
    public void Negative_stock_is_not_blended_and_variance_is_computed()
    {
        // Sold 3 units with only 1 in stock (qty -2) at last cost 60.00; receive 5 units at 70.00.
        var variance = Costing.NegativeStockVariance(-2000, 6000, 5000, 7000);
        Assert.Equal(2000, variance); // 2 units x 10.00 extra
        var (q, a) = Costing.Receive(-2000, 6000, 5000, 7000);
        Assert.Equal(3000, q);
        Assert.Equal(7000, a);
    }

    [Fact]
    public void No_variance_when_stock_was_not_negative() =>
        Assert.Equal(0, Costing.NegativeStockVariance(1000, 6000, 5000, 7000));
}
