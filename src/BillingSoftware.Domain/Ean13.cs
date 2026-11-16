namespace BillingSoftware.Domain;

/// <summary>
/// EAN-13 barcodes: validation, the check digit, shop-made codes and the bar pattern for printing labels.
/// Shop-made codes start with "29", a range reserved for in-store use, so they never clash with a manufacturer's code.
/// </summary>
public static class Ean13
{
    public const string InternalPrefix = "29";
    public const int Modules = 95;

    // Left-hand "L" patterns for digits 0-9. "G" is the mirror image of the right-hand pattern (the complement of L, reversed).
    private static readonly string[] L = { "0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011" };
    private static readonly string[] Parity = { "LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL" };

    private static string R(int digit) => new(L[digit].Select(c => c == '0' ? '1' : '0').ToArray());
    private static string G(int digit) => new(R(digit).Reverse().ToArray());

    public static bool IsDigits(string? code, int length) => code is { } c && c.Length == length && c.All(ch => ch is >= '0' and <= '9');

    public static int CheckDigit(string first12)
    {
        if (!IsDigits(first12, 12)) throw new ArgumentException("Twelve digits are needed.", nameof(first12));
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10;
    }

    /// <summary>True for 13 digits whose last digit is the correct check digit.</summary>
    public static bool IsValid(string? code) => IsDigits(code, 13) && CheckDigit(code![..12]) == code[12] - '0';

    /// <summary>The shop's own code for a running number: "29", ten digits of the number, and the check digit.</summary>
    public static string FromSequence(long sequence)
    {
        if (sequence < 1 || sequence > 9_999_999_999) throw new ArgumentOutOfRangeException(nameof(sequence));
        var body = InternalPrefix + sequence.ToString("D10");
        return body + CheckDigit(body);
    }

    /// <summary>The running number inside a shop-made code, or null when the code is not one.</summary>
    public static long? SequenceOf(string? code) =>
        IsValid(code) && code!.StartsWith(InternalPrefix, StringComparison.Ordinal) ? long.Parse(code.Substring(2, 10)) : null;

    /// <summary>The 95 modules (true = dark bar) of the symbol: guard, six left digits, centre guard, six right digits, guard.</summary>
    public static bool[] Pattern(string code)
    {
        if (!IsValid(code)) throw new ArgumentException("Not a valid EAN-13 code.", nameof(code));
        var parity = Parity[code[0] - '0'];
        var bits = "101";
        for (var i = 0; i < 6; i++)
        {
            var d = code[i + 1] - '0';
            bits += parity[i] == 'L' ? L[d] : G(d);
        }
        bits += "01010";
        for (var i = 7; i < 13; i++) bits += R(code[i] - '0');
        bits += "101";
        return bits.Select(c => c == '1').ToArray();
    }
}
