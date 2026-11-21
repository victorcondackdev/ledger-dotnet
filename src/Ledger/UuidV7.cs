using System.Security.Cryptography;

namespace Ledger;

/// <summary>
/// RFC 9562 version 7 UUIDs: a 48-bit Unix millisecond timestamp followed by
/// random bits, so identifiers sort by creation time. This is the default
/// transaction identifier generator (<c>Guid.CreateVersion7</c> needs .NET 9).
/// </summary>
public static class UuidV7
{
    private const string HexDigits = "0123456789abcdef";

    /// <summary>Returns a new UUIDv7 in canonical lower-case form for the current time.</summary>
    /// <returns>A string such as <c>"019a6f3e-8c2b-7d41-9a3f-5e6b7c8d9e0f"</c>.</returns>
    public static string NewString() => NewString(DateTimeOffset.UtcNow);

    /// <summary>Returns a new UUIDv7 in canonical lower-case form for <paramref name="timestamp"/>.</summary>
    /// <param name="timestamp">The time encoded in the first 48 bits.</param>
    /// <returns>The identifier.</returns>
    public static string NewString(DateTimeOffset timestamp)
    {
        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b);
        ulong ms = unchecked((ulong)timestamp.ToUnixTimeMilliseconds());
        for (int i = 0; i < 6; i++)
        {
            b[i] = unchecked((byte)(ms >> (8 * (5 - i))));
        }

        b[6] = (byte)(0x70 | (b[6] & 0x0F));
        b[8] = (byte)(0x80 | (b[8] & 0x3F));

        Span<char> s = stackalloc char[36];
        int j = 0;
        for (int i = 0; i < 16; i++)
        {
            if (i == 4 || i == 6 || i == 8 || i == 10)
            {
                s[j++] = '-';
            }

            s[j++] = HexDigits[b[i] >> 4];
            s[j++] = HexDigits[b[i] & 0x0F];
        }

        return new string(s);
    }
}
