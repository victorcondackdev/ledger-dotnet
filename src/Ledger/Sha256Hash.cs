using System.Buffers.Binary;

namespace Ledger;

/// <summary>A SHA-256 digest with value semantics, used for request fingerprints and the audit chain.</summary>
public readonly struct Sha256Hash : IEquatable<Sha256Hash>
{
    /// <summary>The digest length in bytes.</summary>
    public const int Size = 32;

    private readonly ulong _a;
    private readonly ulong _b;
    private readonly ulong _c;
    private readonly ulong _d;

    private Sha256Hash(ulong a, ulong b, ulong c, ulong d)
    {
        _a = a;
        _b = b;
        _c = c;
        _d = d;
    }

    /// <summary>The all-zero digest: the previous hash of the first transaction.</summary>
    public static Sha256Hash Zero => default;

    /// <summary>Whether every byte is zero.</summary>
    public bool IsZero => (_a | _b | _c | _d) == 0;

    /// <summary>Creates a digest from its 32 bytes.</summary>
    /// <param name="bytes">Exactly 32 bytes.</param>
    /// <returns>The digest.</returns>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is not 32 bytes long.</exception>
    public static Sha256Hash FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException("A SHA-256 digest is 32 bytes.", nameof(bytes));
        }

        return new Sha256Hash(
            BinaryPrimitives.ReadUInt64BigEndian(bytes),
            BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8)),
            BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(16)),
            BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(24)));
    }

    /// <summary>Parses 64 hexadecimal digits, in either case.</summary>
    /// <param name="hex">The hexadecimal text.</param>
    /// <returns>The digest.</returns>
    /// <exception cref="FormatException"><paramref name="hex"/> is not 64 hexadecimal digits.</exception>
    public static Sha256Hash Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length != Size * 2)
        {
            throw new FormatException("A SHA-256 digest is 64 hexadecimal digits.");
        }

        return FromBytes(Convert.FromHexString(hex));
    }

    /// <summary>Writes the 32 bytes to <paramref name="destination"/>.</summary>
    /// <param name="destination">A buffer of at least 32 bytes.</param>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException("The destination is shorter than 32 bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, _a);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(8), _b);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(16), _c);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(24), _d);
    }

    /// <summary>Returns the 32 bytes in a new array.</summary>
    /// <returns>The digest bytes.</returns>
    public byte[] ToArray()
    {
        var bytes = new byte[Size];
        CopyTo(bytes);
        return bytes;
    }

    /// <summary>Returns the digest as 64 lower-case hexadecimal digits.</summary>
    /// <returns>The hexadecimal text.</returns>
    public override string ToString() => Internal.Hex.Lower(ToArray());

    /// <summary>Compares the digests byte by byte.</summary>
    /// <param name="other">The digest to compare with.</param>
    /// <returns>Whether they are equal.</returns>
    public bool Equals(Sha256Hash other) => _a == other._a && _b == other._b && _c == other._c && _d == other._d;

    /// <summary>Compares the digests byte by byte.</summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns>Whether <paramref name="obj"/> is an equal digest.</returns>
    public override bool Equals(object? obj) => obj is Sha256Hash other && Equals(other);

    /// <summary>Returns a hash code derived from the digest.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => HashCode.Combine(_a, _b, _c, _d);

    /// <summary>Whether two digests are equal.</summary>
    /// <param name="left">The first digest.</param>
    /// <param name="right">The second digest.</param>
    /// <returns>Whether they are equal.</returns>
    public static bool operator ==(Sha256Hash left, Sha256Hash right) => left.Equals(right);

    /// <summary>Whether two digests differ.</summary>
    /// <param name="left">The first digest.</param>
    /// <param name="right">The second digest.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(Sha256Hash left, Sha256Hash right) => !left.Equals(right);
}
