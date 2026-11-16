namespace BillingSoftware.Domain;

public readonly record struct LineTax(
    long GrossPaise,
    long DiscountPaise,
    long TaxablePaise,
    long CgstPaise,
    long SgstPaise,
    long IgstPaise,
    long TotalPaise)
{
    public long TaxPaise => CgstPaise + SgstPaise + IgstPaise;
}

/// <summary>
/// GST calculation per line (decision D7): tax is computed per line, half-up to paise.
/// Intra-state supplies split into CGST and SGST (SGST takes any odd paisa); inter-state use IGST.
/// Prices may be tax-inclusive (MRP style) or tax-exclusive.
/// </summary>
public static class TaxCalculator
{
    public static LineTax Calculate(long unitPricePaise, long qtyMilli, long discountPaise, int gstRateBp, bool interState, bool priceInclusive)
    {
        if (qtyMilli <= 0) throw new ArgumentOutOfRangeException(nameof(qtyMilli), "Quantity must be greater than zero.");
        if (unitPricePaise < 0) throw new ArgumentOutOfRangeException(nameof(unitPricePaise), "Price cannot be negative.");
        if (gstRateBp < 0 || gstRateBp > 10000) throw new ArgumentOutOfRangeException(nameof(gstRateBp), "GST rate must be between 0 and 100 percent.");

        var gross = Money.RoundHalfUp((decimal)unitPricePaise * qtyMilli / Qty.Unit);
        if (discountPaise < 0 || discountPaise > gross)
            throw new ArgumentOutOfRangeException(nameof(discountPaise), "Discount must be between zero and the line amount.");

        var net = gross - discountPaise;
        long taxable, tax;
        if (gstRateBp == 0)
        {
            taxable = net; tax = 0;
        }
        else if (priceInclusive)
        {
            taxable = Money.RoundHalfUp(net * 10000m / (10000 + gstRateBp));
            tax = net - taxable;
        }
        else
        {
            taxable = net;
            tax = Money.RoundHalfUp(net * gstRateBp / 10000m);
        }

        long cgst = 0, sgst = 0, igst = 0;
        if (interState) igst = tax;
        else { cgst = tax / 2; sgst = tax - cgst; }

        return new LineTax(gross, discountPaise, taxable, cgst, sgst, igst, taxable + tax);
    }
}

public static class InvoiceRounding
{
    /// <summary>Rounds an invoice total to the nearest rupee; the difference goes to the Round-off account.</summary>
    public static (long Total, long RoundOff) ToNearestRupee(long exactTotalPaise)
    {
        var rupees = Math.Round(exactTotalPaise / 100m, 0, MidpointRounding.AwayFromZero);
        var total = (long)rupees * 100;
        return (total, total - exactTotalPaise);
    }
}

public static class FinancialYears
{
    /// <summary>Indian financial year runs 1 April to 31 March. Label is "26-27" for April 2026 to March 2027.</summary>
    public static (DateTime Start, DateTime End, string Label) For(DateTime date)
    {
        var startYear = date.Month >= 4 ? date.Year : date.Year - 1;
        var start = new DateTime(startYear, 4, 1);
        var end = new DateTime(startYear + 1, 3, 31);
        return (start, end, $"{startYear % 100:D2}-{(startYear + 1) % 100:D2}");
    }
}

/// <summary>
/// Splits a document line proportionally for partial returns. Cumulative rounding means that returning a line in
/// several parts always adds up to exactly the original amount, to the paisa.
/// </summary>
public static class ReturnMath
{
    public static long Share(long originalPaise, long originalQtyMilli, long qtyMilli) =>
        originalQtyMilli <= 0 ? 0 : Money.RoundHalfUp((decimal)originalPaise * qtyMilli / originalQtyMilli);

    public static long Portion(long originalPaise, long originalQtyMilli, long alreadyReturnedMilli, long returnMilli) =>
        Share(originalPaise, originalQtyMilli, alreadyReturnedMilli + returnMilli) - Share(originalPaise, originalQtyMilli, alreadyReturnedMilli);
}
