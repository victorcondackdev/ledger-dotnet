using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Ledger.Internal;
using Ledger.Monetary;

namespace Ledger;

/// <summary>
/// The canonical, unambiguous byte encoding behind request fingerprints and
/// the audit chain: fixed-width big-endian integers, length-prefixed UTF-8
/// strings and maps sorted byte-wise by key. It is byte-identical to the Go
/// implementation, which is what makes chain heads comparable across them.
/// </summary>
internal static class Canonical
{
    /// <summary>Prefixes every encoding so that the format can evolve without ambiguity.</summary>
    public const ulong EncodingVersion = 1;

    public static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>SHA-256(prev || canonical(t)) where the encoding covers every field except the hash itself.</summary>
    public static Sha256Hash Seal(Transaction t)
    {
        using var e = new HashWriter();
        e.Hash(t.PrevHash);
        e.U64(EncodingVersion);
        e.U64(unchecked((ulong)t.Seq));
        e.Str(t.Id);
        e.U64((ulong)t.Kind);
        e.Str(t.IdempotencyKey);
        e.Str(t.Ref);
        e.I64(UnixNanoseconds(t.Timestamp));
        e.I64(t.ExpiresAt is { } expires ? UnixNanoseconds(expires) : 0);
        e.Str(t.Description);
        e.Meta(t.Metadata);
        e.Postings(t.Postings);
        e.Hash(t.RequestHash);
        return e.Finish();
    }

    /// <summary>Fingerprints a post or hold request.</summary>
    public static Sha256Hash Fingerprint(string op, PostRequest r)
    {
        using var e = new HashWriter();
        e.U64(EncodingVersion);
        e.Str(op);
        e.Str(r.Description);
        e.Meta(r.Metadata);
        e.Postings(r.Postings ?? Array.Empty<Posting>());
        e.I64(unchecked(r.Timeout.Ticks * 100));
        return e.Finish();
    }

    /// <summary>Fingerprints a resolve or reverse request.</summary>
    public static Sha256Hash Fingerprint(string op, ResolveRequest r)
    {
        using var e = new HashWriter();
        e.U64(EncodingVersion);
        e.Str(op);
        e.Str(r.Id);
        e.Str(r.Description);
        e.Meta(r.Metadata);
        if (r.Amount is { } amount)
        {
            e.U64(1);
            e.Str(amount.Currency.Code);
            e.I64(amount.MinorUnits);
        }
        else
        {
            e.U64(0);
        }

        return e.Finish();
    }

    /// <summary>Nanoseconds since the Unix epoch, the unit the Go implementation hashes.</summary>
    public static long UnixNanoseconds(DateTimeOffset t) => unchecked((t.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);

    private sealed class HashWriter : IDisposable
    {
        private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void U64(ulong v)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(b, v);
            _sha.AppendData(b);
        }

        public void I64(long v) => U64(unchecked((ulong)v));

        public void Str(string? s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
            U64((ulong)bytes.Length);
            _sha.AppendData(bytes);
        }

        public void Hash(Sha256Hash h)
        {
            Span<byte> b = stackalloc byte[Sha256Hash.Size];
            h.CopyTo(b);
            _sha.AppendData(b);
        }

        public void Meta(IReadOnlyDictionary<string, string>? m)
        {
            if (m is null || m.Count == 0)
            {
                U64(0);
                return;
            }

            string[] keys = m.Keys.ToArray();
            Array.Sort(keys, Utf8Order.Comparer);
            U64((ulong)keys.Length);
            foreach (string k in keys)
            {
                Str(k);
                Str(m[k]);
            }
        }

        public void Postings(IReadOnlyList<Posting> ps)
        {
            U64((ulong)ps.Count);
            foreach (Posting p in ps)
            {
                Str(p.Account);
                U64((ulong)p.Direction);
                Str(p.Amount.Currency.Code);
                I64(p.Amount.MinorUnits);
            }
        }

        public Sha256Hash Finish() => Sha256Hash.FromBytes(_sha.GetHashAndReset());

        public void Dispose() => _sha.Dispose();
    }
}
