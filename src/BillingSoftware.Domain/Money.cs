using System.Globalization;

namespace BillingSoftware.Domain;

/// <summary>
/// Money is always held as integer paise (long) and quantities as integer thousandths (long)
/// so no binary floating point is ever used for financial values.
/// </summary>
public static class Money
{
    private static readonly CultureInfo Indian = CreateIndian();

    private static CultureInfo CreateIndian()
    {
        try { return new CultureInfo("en-IN"); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    public static long ToPaise(decimal rupees) => RoundHalfUp(rupees * 100m);

    public static decimal ToRupees(long paise) => paise / 100m;

    /// <summary>Round half away from zero (the agreed rounding rule, decision D7).</summary>
    public static long RoundHalfUp(decimal value) => (long)Math.Round(value, 0, MidpointRounding.AwayFromZero);

    public static string Format(long paise) =>
        "₹" + ToRupees(paise).ToString("N2", Indian);

    public static string FormatPlain(long paise) => ToRupees(paise).ToString("N2", Indian);
}

public static class Qty
{
    public const long Unit = 1000;

    public static long ToMilli(decimal quantity) => Money.RoundHalfUp(quantity * Unit);

    public static decimal FromMilli(long milli) => milli / (decimal)Unit;

    public static string Format(long milli)
    {
        var d = FromMilli(milli);
        return d == Math.Floor(d) ? d.ToString("0", CultureInfo.InvariantCulture) : d.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
