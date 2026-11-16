namespace BillingSoftware.Domain;

/// <summary>An amount written out in words the Indian way (thousand, lakh, crore), as printed on invoices.</summary>
public static class AmountInWords
{
    private static readonly string[] Small =
    {
        "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen",
        "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen",
    };
    private static readonly string[] Tens = { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };

    private static string Below100(long n) => n < 20 ? Small[n] : Tens[n / 10] + (n % 10 == 0 ? "" : " " + Small[n % 10]);

    private static string Below1000(long n)
    {
        if (n < 100) return Below100(n);
        return Small[n / 100] + " Hundred" + (n % 100 == 0 ? "" : " " + Below100(n % 100));
    }

    /// <summary>A whole number in words: "Two Lakh Fifty Thousand".</summary>
    public static string Words(long n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        if (n == 0) return Small[0];
        var parts = new List<string>();
        void Part(long value, string name) { if (value > 0) parts.Add(Below1000(value) + (name.Length > 0 ? " " + name : "")); }
        Part(n / 10_000_000 % 100_000, "Crore");     // crores above 99 are written as hundreds of crore
        Part(n / 100_000 % 100, "Lakh");
        Part(n / 1_000 % 100, "Thousand");
        Part(n % 1_000, "");
        return string.Join(" ", parts);
    }

    /// <summary>"Rupees One Thousand Two Hundred and Fifty Paise Only".</summary>
    public static string Rupees(long paise)
    {
        var negative = paise < 0;
        var abs = Math.Abs(paise);
        var rupees = abs / 100;
        var paisa = abs % 100;
        string text;
        if (rupees == 0 && paisa > 0) text = Below100(paisa) + " Paise";
        else text = "Rupees " + Words(rupees) + (paisa > 0 ? " and " + Below100(paisa) + " Paise" : "");
        return (negative ? "Minus " : "") + text + " Only";
    }
}
