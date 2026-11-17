using System.Buffers.Binary;
using System.Text;

namespace SolumanirSheetDesignerStudio.Core.IO;

public sealed record ZipEntryInfo(string Name, ushort Method, long CompressedSize, long UncompressedSize, long LocalHeaderOffset)
{
    /// <summary>Absolute file offset of the entry's data (after the local header), or -1 when not resolved.</summary>
    public long DataOffset { get; init; } = -1;
    public bool IsStored => Method == 0;
}

/// <summary>
/// Minimal ZIP/ZIP64 central-directory reader. <see cref="System.IO.Compression.ZipArchive"/> does not expose where an
/// entry's bytes sit in the file; we need that to hand out lazy, seekable handles to Stored image entries.
/// </summary>
public static class ZipIndex
{
    private const uint EocdSig = 0x06054b50, Zip64LocatorSig = 0x07064b50, Zip64EocdSig = 0x06064b50;
    private const uint CentralSig = 0x02014b50, LocalSig = 0x04034b50;

    public static List<ZipEntryInfo> Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Read(fs);
    }

    public static List<ZipEntryInfo> Read(Stream s)
    {
        long len = s.Length;
        if (len < 22) throw new InvalidDataException("Not a ZIP archive.");

        // End-of-central-directory record: scan backwards over the (optional) archive comment.
        int tailLen = (int)Math.Min(len, 22 + 65535);
        var tail = new byte[tailLen];
        s.Position = len - tailLen;
        s.ReadExactly(tail);
        int eocd = -1;
        for (int i = tailLen - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != EocdSig) continue;
            int commentLen = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20));
            if (i + 22 + commentLen == tailLen) { eocd = i; break; }
        }
        if (eocd < 0) throw new InvalidDataException("ZIP end-of-central-directory record not found.");

        long eocdPos = len - tailLen + eocd;
        long entryCount = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10));
        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));

        if (entryCount == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF)
        {
            if (eocdPos < 20) throw new InvalidDataException("ZIP64 locator missing.");
            Span<byte> loc = stackalloc byte[20];
            s.Position = eocdPos - 20;
            s.ReadExactly(loc);
            if (BinaryPrimitives.ReadUInt32LittleEndian(loc) != Zip64LocatorSig) throw new InvalidDataException("ZIP64 locator missing.");
            long z64Pos = (long)BinaryPrimitives.ReadUInt64LittleEndian(loc[8..]);
            if (z64Pos < 0 || z64Pos > len - 56) throw new InvalidDataException("ZIP64 end record is out of range.");
            Span<byte> z64 = stackalloc byte[56];
            s.Position = z64Pos;
            s.ReadExactly(z64);
            if (BinaryPrimitives.ReadUInt32LittleEndian(z64) != Zip64EocdSig) throw new InvalidDataException("ZIP64 end record is corrupt.");
            entryCount = (long)BinaryPrimitives.ReadUInt64LittleEndian(z64[32..]);
            cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(z64[40..]);
            cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(z64[48..]);
        }

        if (cdOffset < 0 || cdSize < 0 || cdSize > int.MaxValue || cdOffset > len - cdSize)
            throw new InvalidDataException("ZIP central directory is out of range.");

        var cd = new byte[cdSize];
        s.Position = cdOffset;
        s.ReadExactly(cd);

        var entries = new List<ZipEntryInfo>((int)Math.Min(entryCount, 1 << 20));
        int p = 0;
        while (p + 46 <= cd.Length && BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p)) == CentralSig)
        {
            var h = cd.AsSpan(p);
            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]);
            long comp = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
            long uncomp = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
            int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(h[30..]);
            int commentLen = BinaryPrimitives.ReadUInt16LittleEndian(h[32..]);
            long lho = BinaryPrimitives.ReadUInt32LittleEndian(h[42..]);
            if (p + 46 + nameLen + extraLen + commentLen > cd.Length) throw new InvalidDataException("ZIP central directory is truncated.");

            string name = Encoding.UTF8.GetString(cd, p + 46, nameLen);

            // ZIP64 extended information: present only for the fields that overflowed, in this fixed order.
            var extra = cd.AsSpan(p + 46 + nameLen, extraLen);
            int e = 0;
            while (e + 4 <= extra.Length)
            {
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(extra[e..]);
                int size = BinaryPrimitives.ReadUInt16LittleEndian(extra[(e + 2)..]);
                if (e + 4 + size > extra.Length) break;
                if (id == 0x0001)
                {
                    var z = extra.Slice(e + 4, size);
                    int q = 0;
                    if (uncomp == 0xFFFFFFFF && q + 8 <= z.Length) { uncomp = (long)BinaryPrimitives.ReadUInt64LittleEndian(z[q..]); q += 8; }
                    if (comp == 0xFFFFFFFF && q + 8 <= z.Length) { comp = (long)BinaryPrimitives.ReadUInt64LittleEndian(z[q..]); q += 8; }
                    if (lho == 0xFFFFFFFF && q + 8 <= z.Length) { lho = (long)BinaryPrimitives.ReadUInt64LittleEndian(z[q..]); }
                }
                e += 4 + size;
            }

            if (comp < 0 || uncomp < 0) throw new InvalidDataException("ZIP entry size is out of range.");
            entries.Add(new ZipEntryInfo(name, method, comp, uncomp, lho));
            p += 46 + nameLen + extraLen + commentLen;
        }

        // Resolve data offsets from the local headers (their name/extra lengths can differ from the central copy).
        Span<byte> local = stackalloc byte[30];
        for (int i = 0; i < entries.Count; i++)
        {
            var en = entries[i];
            if (en.LocalHeaderOffset < 0 || en.LocalHeaderOffset > len - 30) continue;
            s.Position = en.LocalHeaderOffset;
            s.ReadExactly(local);
            if (BinaryPrimitives.ReadUInt32LittleEndian(local) != LocalSig) continue;
            int n = BinaryPrimitives.ReadUInt16LittleEndian(local[26..]);
            int m = BinaryPrimitives.ReadUInt16LittleEndian(local[28..]);
            long data = en.LocalHeaderOffset + 30 + n + m;
            if (data <= len - en.CompressedSize) entries[i] = en with { DataOffset = data };
        }
        return entries;
    }
}
