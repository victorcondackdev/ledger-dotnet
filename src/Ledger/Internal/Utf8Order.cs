namespace Ledger.Internal;

/// <summary>
/// Orders strings by Unicode code point, which is the byte order of their
/// UTF-8 encodings. Ordinal UTF-16 comparison differs from it for characters
/// above U+E000 versus surrogate pairs; the canonical encoding sorts metadata
/// keys this way so that hashes match implementations that compare bytes.
/// </summary>
internal sealed class Utf8Order : IComparer<string>
{
    public static readonly Utf8Order Comparer = new();

    private Utf8Order()
    {
    }

    public static int CompareStrings(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int n = Math.Min(x.Length, y.Length);
        for (int i = 0; i < n; i++)
        {
            char a = x[i];
            char b = y[i];
            if (a != b)
            {
                return Weight(a) - Weight(b);
            }
        }

        return x.Length - y.Length;
    }

    public int Compare(string? x, string? y) => CompareStrings(x, y);

    // Surrogates (U+D800..U+DFFF) encode code points above U+FFFF, so they
    // must sort after U+E000..U+FFFF.
    private static int Weight(char c) => c < 0xD800 ? c : c < 0xE000 ? c + 0x2000 : c - 0x800;
}
