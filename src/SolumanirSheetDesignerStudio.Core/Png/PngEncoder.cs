using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using SolumanirSheetDesignerStudio.Core.Export;

namespace SolumanirSheetDesignerStudio.Core.Png;

public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[8 * 256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        for (int n = 0; n < 256; n++)
            for (int k = 1; k < 8; k++)
                t[k * 256 + n] = (t[(k - 1) * 256 + n] >> 8) ^ t[t[(k - 1) * 256 + n] & 0xFF];
        return t;
    }

    /// <summary>Feeds bytes into a running CRC. Start with 0xFFFFFFFF and invert the final value.</summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        var t = Table;
        int i = 0;
        for (; i + 8 <= data.Length; i += 8)
        {
            uint one = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) ^ crc;
            uint two = BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
            crc = t[7 * 256 + (int)(one & 0xFF)] ^ t[6 * 256 + (int)((one >> 8) & 0xFF)] ^
                  t[5 * 256 + (int)((one >> 16) & 0xFF)] ^ t[4 * 256 + (int)(one >> 24)] ^
                  t[3 * 256 + (int)(two & 0xFF)] ^ t[2 * 256 + (int)((two >> 8) & 0xFF)] ^
                  t[1 * 256 + (int)((two >> 16) & 0xFF)] ^ t[(int)(two >> 24)];
        }
        for (; i < data.Length; i++) crc = t[(int)((crc ^ data[i]) & 0xFF)] ^ (crc >> 8);
        return crc;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => ~Append(0xFFFFFFFFu, data);
}

public static class Adler32
{
    private const uint Mod = 65521;

    public static uint Compute(ReadOnlySpan<byte> data, uint adler = 1)
    {
        uint a = adler & 0xFFFF, b = adler >> 16;
        int i = 0;
        while (i < data.Length)
        {
            int end = Math.Min(data.Length, i + 5552); // largest block that cannot overflow 32 bits
            for (; i < end; i++)
            {
                a += data[i];
                b += a;
            }
            a %= Mod;
            b %= Mod;
        }
        return (b << 16) | a;
    }

    /// <summary>Adler-32 of A‖B from adler(A), adler(B) and len(B) — lets strips be checksummed in parallel.</summary>
    public static uint Combine(uint adler1, uint adler2, long len2)
    {
        uint rem = (uint)(len2 % Mod);
        uint sum1 = adler1 & 0xFFFF;
        uint sum2 = (rem * sum1) % Mod;
        sum1 += (adler2 & 0xFFFF) + Mod - 1;
        sum2 += ((adler1 >> 16) & 0xFFFF) + ((adler2 >> 16) & 0xFFFF) + Mod - rem;
        if (sum1 >= Mod) sum1 -= Mod;
        if (sum1 >= Mod) sum1 -= Mod;
        if (sum2 >= Mod << 1) sum2 -= Mod << 1;
        if (sum2 >= Mod) sum2 -= Mod;
        return sum1 | (sum2 << 16);
    }
}

/// <summary>One horizontal strip of the image, already filtered and deflated.</summary>
public sealed class PngStrip
{
    public required byte[] Data { get; init; }
    public required int Length { get; init; }
    /// <summary>Adler-32 of this strip's filtered (pre-deflate) bytes.</summary>
    public required uint Adler { get; init; }
    public required long RawLength { get; init; }
}

/// <summary>
/// Filters and deflates strips independently so they can be compressed on several threads. Every strip but the last
/// ends with a sync flush (byte-aligned, not final), so the raw deflate outputs concatenate into one valid stream.
/// </summary>
public static class PngStripEncoder
{
    /// <param name="rgba">8-bit RGBA rows (not premultiplied), <paramref name="stride"/> bytes apart.</param>
    public static PngStrip Encode(ReadOnlySpan<byte> rgba, int width, int rows, int stride, PngCompression level, bool isLast)
    {
        int rowBytes = checked(width * 4);
        int filteredLength = checked(rows * (rowBytes + 1));
        byte[] filtered = ArrayPool<byte>.Shared.Rent(filteredLength);
        try
        {
            Filter(rgba, rowBytes, rows, stride, filtered, level);
            uint adler = Adler32.Compute(filtered.AsSpan(0, filteredLength));

            var ms = new MemoryStream(Math.Max(4096, filteredLength / 6));
            var deflate = new DeflateStream(ms, ToLevel(level), leaveOpen: true);
            deflate.Write(filtered, 0, filteredLength);
            int length;
            if (isLast)
            {
                deflate.Dispose(); // writes the final block
                length = (int)ms.Length;
            }
            else
            {
                deflate.Flush(); // Z_SYNC_FLUSH: everything so far, byte-aligned, stream left open
                length = (int)ms.Length;
                deflate.Dispose(); // the terminator it appends lands after `length` and is ignored
            }
            return new PngStrip { Data = ms.GetBuffer(), Length = length, Adler = adler, RawLength = filteredLength };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(filtered);
        }
    }

    private static CompressionLevel ToLevel(PngCompression level) => level switch
    {
        PngCompression.Fast => CompressionLevel.Fastest,
        PngCompression.Small => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal,
    };

    private static void Filter(ReadOnlySpan<byte> src, int rowBytes, int rows, int stride, Span<byte> dst, PngCompression level)
    {
        int o = 0;
        if (level == PngCompression.Fast)
        {
            // Fixed "Sub" filter: one cheap pass, no dependency on the previous row.
            for (int r = 0; r < rows; r++)
            {
                dst[o++] = 1;
                Sub(src.Slice(r * stride, rowBytes), dst.Slice(o, rowBytes));
                o += rowBytes;
            }
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(rowBytes * 3);
        try
        {
            Span<byte> sub = rented.AsSpan(0, rowBytes), up = rented.AsSpan(rowBytes, rowBytes), paeth = rented.AsSpan(rowBytes * 2, rowBytes);
            for (int r = 0; r < rows; r++)
            {
                var cur = src.Slice(r * stride, rowBytes);
                long best = Cost(cur);
                byte type = 0;
                ReadOnlySpan<byte> chosen = cur;

                Sub(cur, sub);
                long c = Cost(sub);
                if (c < best) { best = c; type = 1; chosen = sub; }

                // The first row of a strip may only use filters that ignore the previous row: the strip above is
                // encoded on another thread and is not available here.
                if (r > 0)
                {
                    var prev = src.Slice((r - 1) * stride, rowBytes);
                    Up(cur, prev, up);
                    c = Cost(up);
                    if (c < best) { best = c; type = 2; chosen = up; }
                    Paeth(cur, prev, paeth);
                    c = Cost(paeth);
                    if (c < best) { type = 4; chosen = paeth; }
                }

                dst[o++] = type;
                chosen.CopyTo(dst.Slice(o, rowBytes));
                o += rowBytes;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static long Cost(ReadOnlySpan<byte> row)
    {
        long sum = 0;
        for (int i = 0; i < row.Length; i++)
        {
            int v = row[i];
            sum += v < 128 ? v : 256 - v;
        }
        return sum;
    }

    private static void Sub(ReadOnlySpan<byte> cur, Span<byte> dst)
    {
        int n = cur.Length;
        for (int i = 0; i < 4 && i < n; i++) dst[i] = cur[i];
        for (int i = 4; i < n; i++) dst[i] = (byte)(cur[i] - cur[i - 4]);
    }

    private static void Up(ReadOnlySpan<byte> cur, ReadOnlySpan<byte> prev, Span<byte> dst)
    {
        for (int i = 0; i < cur.Length; i++) dst[i] = (byte)(cur[i] - prev[i]);
    }

    private static void Paeth(ReadOnlySpan<byte> cur, ReadOnlySpan<byte> prev, Span<byte> dst)
    {
        int n = cur.Length;
        for (int i = 0; i < 4 && i < n; i++) dst[i] = (byte)(cur[i] - prev[i]);
        for (int i = 4; i < n; i++)
        {
            int a = cur[i - 4], b = prev[i], c = prev[i - 4];
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            int pred = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
            dst[i] = (byte)(cur[i] - pred);
        }
    }
}

/// <summary>
/// Streaming PNG writer (8-bit RGBA). Strips arrive in order, already compressed; the full bitmap is never held.
/// </summary>
public sealed class PngStreamWriter
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private const int MaxIdat = 1 << 20;

    private readonly Stream _out;
    private uint _adler = 1;
    private bool _finished;

    public PngStreamWriter(Stream output, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "PNG dimensions must be positive.");
        _out = output;
        Width = width;
        Height = height;

        _out.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        WriteChunk("IHDR", ihdr);
        WriteChunk("sRGB", stackalloc byte[] { 0 });
        WriteChunk("IDAT", stackalloc byte[] { 0x78, 0x9C }); // zlib header; the deflate data follows in later IDATs
    }

    public int Width { get; }
    public int Height { get; }
    public long BytesWritten { get; private set; }

    public void WriteStrip(PngStrip strip)
    {
        if (_finished) throw new InvalidOperationException("The PNG is already finished.");
        _adler = Adler32.Combine(_adler, strip.Adler, strip.RawLength);
        for (int o = 0; o < strip.Length; o += MaxIdat)
            WriteChunk("IDAT", strip.Data.AsSpan(o, Math.Min(MaxIdat, strip.Length - o)));
    }

    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        Span<byte> trailer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(trailer, _adler);
        WriteChunk("IDAT", trailer);
        WriteChunk("IEND", ReadOnlySpan<byte>.Empty);
        _out.Flush();
    }

    private void WriteChunk(string type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)data.Length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        _out.Write(head);
        _out.Write(data);
        uint crc = Crc32.Append(Crc32.Append(0xFFFFFFFFu, head[4..]), data);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, ~crc);
        _out.Write(tail);
        BytesWritten += 12 + data.Length;
    }
}
