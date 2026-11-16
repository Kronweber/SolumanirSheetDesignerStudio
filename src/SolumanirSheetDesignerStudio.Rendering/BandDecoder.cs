using System.Runtime.InteropServices;
using SkiaSharp;
using SolumanirSheetDesignerStudio.Core.Export;
using SolumanirSheetDesignerStudio.Core.Layout;

namespace SolumanirSheetDesignerStudio.Rendering;

/// <summary>
/// Hands out horizontal bands of an image, optionally pre-reduced, without holding more of the source in memory
/// than a format allows:
/// <list type="bullet">
/// <item>JPEG is read scanline by scanline, forward only, using the codec's own DCT scaling.</item>
/// <item>PNG and WebP can only be decoded as "rows a to b", and every such request reads the file from its top
/// again. They are therefore decoded in large windows (<see cref="ExportPlanner.DecodeWindowBytes"/> of source
/// pixels): an ordinary image is one window and is decoded exactly once, a huge one costs a few extra passes
/// instead of gigabytes of memory.</item>
/// <item>Everything else (GIF, BMP, interlaced PNG, EXIF-rotated files) is decoded whole, once.</item>
/// </list>
/// Bands should be requested top to bottom; a band may overlap the previous one.
/// </summary>
internal sealed class BandDecoder : IDisposable
{
    private enum Mode { Scanline, Window, Full }

    /// <summary>
    /// Source pixels (as bytes) decoded per pass. SSDS_DECODE_WINDOW_MB overrides the default, which is how the
    /// multi-window path is tested with small images (the output must not depend on the window size).
    /// </summary>
    private static readonly long WindowBytes =
        int.TryParse(Environment.GetEnvironmentVariable("SSDS_DECODE_WINDOW_MB"), out int mb) && mb > 0 ? (long)mb << 20 : ExportPlanner.DecodeWindowBytes;

    private readonly Func<Stream> _open;
    private readonly SKEncodedImageFormat _format;
    private readonly SKEncodedOrigin _origin;
    private readonly SKSizeI _decode; // size the codec decodes at
    private readonly int _factor;     // integer box reduction applied after the codec's own scaling
    private Mode _mode;
    private SKCodec? _codec;
    private Stream? _stream;
    private int _nextRow;             // scanline mode: next undecoded reduced row
    private SKBitmap? _held;          // rows [_heldStart, _heldEnd) of the reduced image: the last band, the current window, or all of it
    private int _heldStart, _heldEnd;
    private IntPtr _scratch;
    private long _scratchBytes;

    /// <param name="open">Opens the encoded bytes (called again whenever decoding has to start over).</param>
    /// <param name="targetScale">Largest of the horizontal/vertical output-pixels-per-source-pixel ratios; values below 1 allow pre-reduction.</param>
    public BandDecoder(Func<Stream> open, double targetScale)
    {
        _open = open;
        OpenCodec();
        var info = _codec!.Info;
        _format = _codec.EncodedFormat;
        _origin = _codec.EncodedOrigin;

        // 1) Let the codec scale where it can do so natively (JPEG: 1/2, 1/4, 1/8; WebP: any).
        double scale = Math.Clamp(targetScale, 1e-4, 1);
        float codecScale = 1;
        if (_format is SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp)
            while (codecScale / 2 >= scale && codecScale > 0.125f) codecScale /= 2;
        _decode = codecScale < 1 ? _codec.GetScaledDimensions(codecScale) : info.Size;
        if (_decode.Width <= 0 || _decode.Height <= 0) _decode = info.Size;

        // 2) Whatever reduction is left is done with an exact integer box filter, leaving less than 2× for the resampler.
        double achieved = Math.Min(_decode.Width / (double)info.Width, _decode.Height / (double)info.Height);
        _factor = Math.Clamp((int)Math.Floor(achieved / scale + 1e-9), 1, 64);

        int rw = (_decode.Width + _factor - 1) / _factor, rh = (_decode.Height + _factor - 1) / _factor;
        bool swap = ImageOps.SwapsAxes(_origin);
        Width = swap ? rh : rw;
        Height = swap ? rw : rh;

        bool upright = _origin == SKEncodedOrigin.TopLeft || (int)_origin == 0;
        bool unscaled = _decode == info.Size;
        if (upright && _format == SKEncodedImageFormat.Jpeg && StartScanlines())
        {
            _mode = Mode.Scanline;
            return;
        }

        // Only the scanline reader keeps its place in the file; the others open it just while they read.
        // The interlace check opens the file itself. The codec is closed first so that a reader never holds two
        // streams of one image.
        CloseCodec();
        _mode = upright && unscaled && (_format == SKEncodedImageFormat.Webp || (_format == SKEncodedImageFormat.Png && !IsInterlacedPng())) ? Mode.Window : Mode.Full;
    }

    /// <summary>Size of the (possibly reduced, upright) image the bands are cut from.</summary>
    public int Width { get; }
    public int Height { get; }

    private void OpenCodec()
    {
        CloseCodec();
        var stream = _open();
        try
        {
            _codec = SKCodec.Create(stream) ?? throw new InvalidDataException("The image could not be decoded.");
            _stream = stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        _nextRow = 0;
    }

    private void CloseCodec()
    {
        _codec?.Dispose();
        _codec = null;
        _stream?.Dispose(); // releases the asset I/O gate even if the codec did not own the stream
        _stream = null;
    }

    private bool StartScanlines()
    {
        try
        {
            var result = _codec!.StartScanlineDecode(ImageOps.Info(_decode.Width, _decode.Height));
            return result == SKCodecResult.Success && _codec.ScanlineOrder == SKCodecScanlineOrder.TopDown;
        }
        catch (Exception)
        {
            return false; // this file cannot be read by scanline: it is decoded whole instead
        }
    }

    /// <summary>The interlace byte of the IHDR chunk. An interlaced PNG cannot be decoded as a row range.</summary>
    private bool IsInterlacedPng()
    {
        using var stream = _open();
        Span<byte> head = stackalloc byte[29];
        return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length || head[28] != 0;
    }

    /// <summary>
    /// An image holding at least rows [<paramref name="r0"/>, <paramref name="r1"/>) of the reduced image; its first
    /// row is row <c>RowOffset</c>. The caller owns (disposes) the image; its pixels stay valid after this decoder moves on.
    /// </summary>
    public (SKImage Image, int RowOffset) GetBand(int r0, int r1)
    {
        r0 = Math.Clamp(r0, 0, Math.Max(0, Height - 1));
        r1 = Math.Clamp(r1, r0 + 1, Math.Max(r0 + 1, Height));

        if (_mode == Mode.Scanline && !ReadScanlineBand(r0, r1)) _mode = Mode.Full;
        if (_mode == Mode.Window && (_held == null || r0 < _heldStart || r1 > _heldEnd) && !LoadWindow(r0, r1)) _mode = Mode.Full;
        if (_mode == Mode.Full && (_held == null || _heldStart != 0 || _heldEnd != Height)) Hold(DecodeFull(), 0, Height);
        return (SKImage.FromBitmap(_held!), _heldStart);
    }

    private void Hold(SKBitmap? bitmap, int start, int end)
    {
        // Images created from the previous bitmap keep its pixels alive; only our handle goes away.
        _held?.Dispose();
        _held = bitmap;
        _heldStart = start;
        _heldEnd = end;
    }

    // ---------- JPEG: forward-only scanlines ----------

    private unsafe bool ReadScanlineBand(int r0, int r1)
    {
        int available = _held != null ? _heldStart : _nextRow;
        if (r0 < available)
        {
            // Asked to go backwards: restart the decode from the top.
            Hold(null, 0, 0);
            OpenCodec();
            if (!StartScanlines()) return false;
        }

        var band = new SKBitmap(ImageOps.Info(Width, r1 - r0));
        byte* dst = (byte*)band.GetPixels();
        int stride = band.RowBytes;
        new Span<byte>(dst, checked(stride * (r1 - r0))).Clear();

        int row = r0;
        if (_held != null && r0 < _heldEnd)
        {
            // Rows shared with the previous band are copied instead of decoded again.
            int shared = Math.Min(_heldEnd, r1) - r0;
            byte* src = (byte*)_held.GetPixels() + (long)(r0 - _heldStart) * _held.RowBytes;
            Buffer.MemoryCopy(src, dst, (long)stride * (r1 - r0), (long)Math.Min(stride, _held.RowBytes) * shared);
            row = r0 + shared;
        }
        if (row > _nextRow)
        {
            int skipSource = Math.Min(_decode.Height - _nextRow * _factor, (row - _nextRow) * _factor);
            if (skipSource > 0) _codec!.SkipScanlines(skipSource);
            _nextRow = row;
        }
        if (row < r1) ReadScanlines(dst + (long)(row - r0) * stride, stride, row, r1);

        band.SetImmutable();
        Hold(band, r0, r1);
        return true;
    }

    /// <summary>Decodes reduced rows [from, to) at the codec's current position. A truncated file leaves the remaining rows transparent.</summary>
    private unsafe void ReadScanlines(byte* dst, int dstStride, int from, int to)
    {
        if (_factor == 1)
        {
            _codec!.GetScanlines((IntPtr)dst, to - from, dstStride);
            _nextRow = to;
            return;
        }

        int srcStride = _decode.Width * 4;
        EnsureScratch((long)srcStride * _factor);
        for (int r = from; r < to; r++)
        {
            int want = Math.Min(_factor, _decode.Height - r * _factor);
            if (want <= 0) break;
            int got = _codec!.GetScanlines(_scratch, want, srcStride);
            if (got <= 0) break;
            ImageOps.BoxReduceRow((byte*)_scratch, _decode.Width, got, srcStride, dst + (long)(r - from) * dstStride, Width, _factor);
        }
        _nextRow = to;
    }

    // ---------- PNG, WebP: row windows ----------

    /// <summary>Decodes a window that starts at <paramref name="r0"/> and covers at least up to <paramref name="r1"/>. False when this file cannot be decoded as a row range.</summary>
    private unsafe bool LoadWindow(int r0, int r1)
    {
        int srcStride = checked(_decode.Width * 4);
        int rowsPerPass = (int)Math.Clamp(WindowBytes / ((long)srcStride * _factor), 1, Height);
        int end = Math.Min(Height, Math.Max(r1, r0 + rowsPerPass));
        if (Height - end < rowsPerPass / 4) end = Height; // a short remainder is not worth another pass over the file

        var window = new SKBitmap(ImageOps.Info(Width, end - r0)); // zero-filled: rows a truncated file cannot supply stay transparent
        try
        {
            byte* dst = (byte*)window.GetPixels();
            int stride = window.RowBytes;
            if (_factor == 1)
            {
                if (!DecodeSourceRows(r0, end, (IntPtr)dst, stride)) { window.Dispose(); return false; }
            }
            else
            {
                for (int c0 = r0; c0 < end; c0 += rowsPerPass)
                {
                    int c1 = Math.Min(end, c0 + rowsPerPass);
                    int n0 = c0 * _factor, n1 = Math.Min(_decode.Height, c1 * _factor);
                    EnsureScratch((long)srcStride * (n1 - n0));
                    new Span<byte>((void*)_scratch, checked((int)Math.Min(int.MaxValue, (long)srcStride * (n1 - n0)))).Clear(); // a truncated file leaves the rest transparent
                    if (!DecodeSourceRows(n0, n1, _scratch, srcStride)) { window.Dispose(); return false; }
                    for (int r = c0; r < c1; r++)
                    {
                        int rows = Math.Min(_factor, n1 - r * _factor);
                        if (rows <= 0) break;
                        ImageOps.BoxReduceRow((byte*)_scratch + (long)(r * _factor - n0) * srcStride, _decode.Width, rows, srcStride,
                            dst + (long)(r - r0) * stride, Width, _factor);
                    }
                }
            }
        }
        catch
        {
            window.Dispose();
            throw;
        }
        finally
        {
            // The source rows of a window can be hundreds of megabytes; only the reduced window is kept.
            FreeScratch();
        }
        window.SetImmutable();
        Hold(window, r0, end);
        return true;
    }

    /// <summary>Decodes source rows [n0, n1) at full width into <paramref name="dst"/>. Each call reads the file from its top.</summary>
    private bool DecodeSourceRows(int n0, int n1, IntPtr dst, int rowBytes)
    {
        OpenCodec();
        try
        {
            var rows = new SKCodecOptions(new SKRectI(0, n0, _decode.Width, n1));
            SKCodecResult result;
            if (_format == SKEncodedImageFormat.Png)
            {
                result = _codec!.StartIncrementalDecode(ImageOps.Info(_decode.Width, _decode.Height), dst, rowBytes, rows);
                if (result == SKCodecResult.Success) result = _codec.IncrementalDecode(out _);
            }
            else
            {
                result = _codec!.GetPixels(ImageOps.Info(_decode.Width, n1 - n0), dst, rowBytes, rows);
            }
            return result is SKCodecResult.Success or SKCodecResult.IncompleteInput;
        }
        finally
        {
            CloseCodec();
        }
    }

    // ---------- everything else: one full decode ----------

    private unsafe SKBitmap DecodeFull()
    {
        OpenCodec();
        SKBitmap bitmap;
        try
        {
            bitmap = new SKBitmap(ImageOps.Info(_decode.Width, _decode.Height));
            var result = _codec!.GetPixels(bitmap.Info, bitmap.GetPixels());
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            {
                bitmap.Dispose();
                throw new InvalidDataException("The image could not be decoded (" + result + ").");
            }
        }
        finally
        {
            CloseCodec();
        }

        if (_factor > 1)
        {
            int rw = (_decode.Width + _factor - 1) / _factor, rh = (_decode.Height + _factor - 1) / _factor;
            var reduced = new SKBitmap(ImageOps.Info(rw, rh));
            byte* src = (byte*)bitmap.GetPixels();
            byte* dst = (byte*)reduced.GetPixels();
            for (int y = 0; y < rh; y++)
            {
                int rows = Math.Min(_factor, _decode.Height - y * _factor);
                ImageOps.BoxReduceRow(src + (long)y * _factor * bitmap.RowBytes, _decode.Width, rows, bitmap.RowBytes, dst + (long)y * reduced.RowBytes, rw, _factor);
            }
            bitmap.Dispose();
            bitmap = reduced;
        }
        bitmap = ImageOps.ApplyOrigin(bitmap, _origin);
        bitmap.SetImmutable();
        return bitmap;
    }

    private void EnsureScratch(long bytes)
    {
        if (_scratchBytes >= bytes) return;
        FreeScratch();
        _scratch = Marshal.AllocHGlobal((IntPtr)bytes);
        _scratchBytes = bytes;
    }

    private void FreeScratch()
    {
        if (_scratch != IntPtr.Zero) Marshal.FreeHGlobal(_scratch);
        _scratch = IntPtr.Zero;
        _scratchBytes = 0;
    }

    public void Dispose()
    {
        Hold(null, 0, 0);
        CloseCodec();
        FreeScratch();
    }

    /// <summary>Decodes a whole image so that its larger side is about <paramref name="maxDim"/> pixels (never upscaled).</summary>
    public static SKImage DecodePreview(Func<Stream> open, int fileWidth, int fileHeight, int maxDim, SKFilterQuality quality)
    {
        int longest = Math.Max(1, Math.Max(fileWidth, fileHeight));
        double scale = Math.Min(1.0, maxDim / (double)longest);
        using var decoder = new BandDecoder(open, scale);
        var (band, _) = decoder.GetBand(0, decoder.Height);
        int tw = Math.Max(1, PixelGrid.Round(fileWidth * scale)), th = Math.Max(1, PixelGrid.Round(fileHeight * scale));
        if (band.Width <= tw * 1.15 && band.Height <= th * 1.15) return band;

        // Finish the reduction with Skia's resampler so the cache holds exactly what the screen needs.
        using (band)
        {
            var target = new SKBitmap(ImageOps.Info(tw, th));
            using (var canvas = new SKCanvas(target))
            using (var paint = new SKPaint { BlendMode = SKBlendMode.Src, FilterQuality = quality == SKFilterQuality.None ? SKFilterQuality.Low : quality })
            {
                canvas.DrawImage(band, SKRect.Create(band.Width, band.Height), SKRect.Create(tw, th), paint);
            }
            target.SetImmutable();
            var image = SKImage.FromBitmap(target);
            target.Dispose();
            return image;
        }
    }
}
