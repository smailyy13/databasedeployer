using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace SchemaDiff.Core.Hashing;

/// <summary>
/// XxHash128 — kriptografik değil, ama şema değişikliği tespiti için fazlasıyla yeterli
/// ve SHA-256'dan bir mertebe hızlı. 100k obje için çakışma olasılığı ~1e-29.
/// </summary>
public static class Hash
{
    public static UInt128 Of(string text) =>
        XxHash128.HashToUInt128(Encoding.UTF8.GetBytes(text));

    /// <summary>Alt-parça hash'lerini birleştirir. Sıra anlamlıdır — çağıran deterministik sıra vermeli.</summary>
    public static UInt128 Combine(IEnumerable<UInt128> parts)
    {
        var hasher = new XxHash128();
        Span<byte> buffer = stackalloc byte[16];
        foreach (var part in parts)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, (ulong)part);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer[8..], (ulong)(part >> 64));
            hasher.Append(buffer);
        }
        return FromBytes(hasher.GetCurrentHash());
    }

    public static string ToHex(UInt128 value) => value.ToString("x32");

    private static UInt128 FromBytes(byte[] bytes)
    {
        var lower = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        var upper = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8));
        return new UInt128(upper, lower);
    }
}
