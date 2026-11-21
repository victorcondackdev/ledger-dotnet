namespace Ledger.Monetary;

/// <summary>
/// Overflow-detecting 64-bit arithmetic that reports failure instead of
/// throwing, for hot paths that turn overflow into a domain error.
/// </summary>
internal static class CheckedMath
{
    public static bool TryAdd(long a, long b, out long result)
    {
        long s = unchecked(a + b);
        if (((a ^ s) & (b ^ s)) < 0)
        {
            result = 0;
            return false;
        }

        result = s;
        return true;
    }

    public static bool TrySubtract(long a, long b, out long result)
    {
        long s = unchecked(a - b);
        if (((a ^ b) & (a ^ s)) < 0)
        {
            result = 0;
            return false;
        }

        result = s;
        return true;
    }

    public static bool TryMultiply(long a, long b, out long result)
    {
        long high = Math.BigMul(a, b, out long low);
        if ((high != 0 || low < 0) && (high != -1 || low >= 0))
        {
            result = 0;
            return false;
        }

        result = low;
        return true;
    }
}
