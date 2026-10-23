// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Zero-dependency, <see cref="ArrayPool{T}"/>-backed QR Code (Model 2, V1–V10)
/// and 1D Code-128B scanner operating directly on BGRA32 pixel spans.
/// </summary>
internal static class QrAndBarcodeScanner
{
    private const ushort Code128StopPattern = 0x18EB;

    internal sealed record ScanResult(string Format, string Text, Uri HttpUri);

    // Standard 11-module bit patterns for Code-128 values 0..105 (and 13-bit Stop = 0x18EB).
    private static readonly ushort[] Code128Patterns =
    {
        0x6CC, 0x66C, 0x666, 0x498, 0x48C, 0x44C, 0x4C8, 0x4C4, 0x464, 0x648,
        0x644, 0x624, 0x59C, 0x4DC, 0x4CE, 0x5CC, 0x4EC, 0x4E6, 0x672, 0x65C,
        0x64E, 0x6E4, 0x674, 0x76E, 0x74C, 0x72C, 0x726, 0x764, 0x734, 0x732,
        0x6D8, 0x6C6, 0x636, 0x518, 0x458, 0x446, 0x588, 0x468, 0x462, 0x688,
        0x628, 0x622, 0x5B8, 0x58E, 0x46E, 0x5D8, 0x5C6, 0x476, 0x776, 0x68E,
        0x62E, 0x6E8, 0x6E2, 0x6EE, 0x758, 0x746, 0x716, 0x768, 0x762, 0x71A,
        0x77A, 0x642, 0x78A, 0x530, 0x50C, 0x4B0, 0x486, 0x42C, 0x426, 0x590,
        0x584, 0x4D0, 0x4C2, 0x434, 0x432, 0x612, 0x650, 0x7BA, 0x614, 0x47A,
        0x53C, 0x4BC, 0x49E, 0x5E4, 0x4F4, 0x4F2, 0x7A4, 0x794, 0x792, 0x6DE,
        0x6F6, 0x7B6, 0x578, 0x51E, 0x45E, 0x5E8, 0x5E2, 0x7A8, 0x7A2, 0x5DE,
        0x5EE, 0x75E, 0x7AE, 0x684, 0x690, 0x69C,
    };

    private static readonly char[] AlphanumericTable =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:".ToCharArray();

    // Total data codewords for QR Version 1..10 and EC level index (0=M, 1=L, 2=H, 3=Q).
    private static readonly int[,] DataCodewordTable =
    {
        { 16, 19, 9, 13 },     // V1
        { 28, 34, 16, 22 },    // V2
        { 44, 55, 26, 34 },    // V3
        { 64, 80, 36, 48 },    // V4
        { 86, 108, 46, 62 },   // V5
        { 108, 136, 60, 76 },  // V6
        { 124, 156, 66, 88 },  // V7
        { 154, 194, 86, 110 }, // V8
        { 182, 232, 100, 132 }, // V9
        { 216, 274, 122, 154 }, // V10
    };

    /// <summary>
    /// Scans a WPF <see cref="BitmapSource"/> for QR codes and 1D barcodes.
    /// </summary>
    public static IReadOnlyList<ScanResult> Scan(BitmapSource bitmap)
    {
        if (bitmap == null || bitmap.PixelWidth < 21 || bitmap.PixelHeight < 8)
        {
            return Array.Empty<ScanResult>();
        }

        BitmapSource src = bitmap.Format == PixelFormats.Bgra32 || bitmap.Format == PixelFormats.Pbgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);

        int w = src.PixelWidth;
        int h = src.PixelHeight;
        int stride = w * 4;
        int byteLen = stride * h;
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteLen);
        try
        {
            src.CopyPixels(rented, stride, 0);
            return ScanBgra(rented.AsSpan(0, byteLen), w, h, stride);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Scans a raw BGRA32 pixel buffer for QR codes and 1D Code-128B barcodes.
    /// </summary>
    public static IReadOnlyList<ScanResult> ScanBgra(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (bgra.IsEmpty || width < 21 || height < 8 || stride < width * 4 || bgra.Length < stride * height)
        {
            return Array.Empty<ScanResult>();
        }

        int pixelCount = width * height;
        byte[] binary = ArrayPool<byte>.Shared.Rent(pixelCount);
        try
        {
            BinarizeBgra(bgra, width, height, stride, binary.AsSpan(0, pixelCount));
            var results = new List<ScanResult>(2);

            if (width >= 21 && height >= 21
                && TryDecodeQrFromBinary(binary.AsSpan(0, pixelCount), width, height, out string qrText)
                && !string.IsNullOrEmpty(qrText))
            {
                TryNormalizeHttpUrl(qrText, out Uri uri);
                results.Add(new ScanResult("QR", qrText, uri));
            }

            if (TryDecodeCode128FromBinary(binary.AsSpan(0, pixelCount), width, height, out string barcodeText)
                && !string.IsNullOrEmpty(barcodeText))
            {
                TryNormalizeHttpUrl(barcodeText, out Uri uri);
                results.Add(new ScanResult("CODE-128", barcodeText, uri));
            }

            return results;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(binary);
        }
    }

    /// <summary>
    /// Checks whether <paramref name="text"/> is a valid <c>http://</c> or <c>https://</c> URL.
    /// </summary>
    public static bool TryNormalizeHttpUrl(string text, out Uri uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri parsed)
            && (string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            uri = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Renders a clean Version 1 or Version 2 QR Code (Byte mode, Mask 0, EC Level L) into a BGRA32 buffer.
    /// Used by unit tests and synthetic verification.
    /// </summary>
    internal static byte[] RenderQrBgra(string payload, int modulePixels, int quietZoneModules, out int width, out int height, out int stride)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(payload ?? string.Empty);
        int version = utf8.Length <= 17 ? 1 : 2;
        int dim = 17 + (4 * version);
        bool[,] isFunc = new bool[dim, dim];
        bool[,] modules = new bool[dim, dim];

        PlaceFinderPattern(modules, isFunc, 0, 0, dim);
        PlaceFinderPattern(modules, isFunc, dim - 7, 0, dim);
        PlaceFinderPattern(modules, isFunc, 0, dim - 7, dim);

        for (int i = 8; i < dim - 8; i++)
        {
            isFunc[6, i] = true;
            modules[6, i] = (i & 1) == 0;
            isFunc[i, 6] = true;
            modules[i, 6] = (i & 1) == 0;
        }

        if (version == 2)
        {
            int ax = dim - 7;
            int ay = dim - 7;
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    isFunc[ay + dy, ax + dx] = true;
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    modules[ay + dy, ax + dx] = dist != 1;
                }
            }
        }

        // Reserve format info & dark module.
        for (int i = 0; i < 9; i++)
        {
            if (i < dim)
            {
                isFunc[8, i] = true;
                isFunc[i, 8] = true;
            }
        }

        for (int i = 0; i < 8; i++)
        {
            isFunc[8, dim - 1 - i] = true;
            isFunc[dim - 1 - i, 8] = true;
        }

        isFunc[dim - 8, 8] = true;
        modules[dim - 8, 8] = true;

        // Write Format Info for EC Level L (01), Mask 0 (000) -> data bits 01000 (8), XOR 0x5412.
        int formatBits = (0b01_000 << 10) ^ 0x5412;
        WriteFormatBits(modules, dim, formatBits);

        // Encode byte-mode bitstream into data codewords.
        int totalCodewords = version == 1 ? 26 : 44;
        byte[] codewords = new byte[totalCodewords];
        int bitPos = 0;
        WriteBits(codewords, ref bitPos, 0b0100, 4);
        WriteBits(codewords, ref bitPos, utf8.Length, 8);
        for (int i = 0; i < utf8.Length; i++)
        {
            WriteBits(codewords, ref bitPos, utf8[i], 8);
        }

        WriteBits(codewords, ref bitPos, 0, Math.Min(4, (totalCodewords * 8) - bitPos));
        while ((bitPos & 7) != 0)
        {
            WriteBits(codewords, ref bitPos, 0, 1);
        }

        byte pad = 0xEC;
        while (bitPos < totalCodewords * 8)
        {
            WriteBits(codewords, ref bitPos, pad, 8);
            pad = pad == 0xEC ? (byte)0x11 : (byte)0xEC;
        }

        // Place data bits in zig-zag order with Mask 0: ((r + c) & 1) == 0.
        int cwBitIdx = 0;
        bool upward = true;
        for (int right = dim - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5;
            }

            for (int v = 0; v < dim; v++)
            {
                int r = upward ? (dim - 1 - v) : v;
                for (int j = 0; j < 2; j++)
                {
                    int c = right - j;
                    if (isFunc[r, c])
                    {
                        continue;
                    }

                    bool bit = false;
                    if (cwBitIdx < totalCodewords * 8)
                    {
                        bit = ((codewords[cwBitIdx >> 3] >> (7 - (cwBitIdx & 7))) & 1) != 0;
                        cwBitIdx++;
                    }

                    if (((r + c) & 1) == 0)
                    {
                        bit = !bit;
                    }

                    modules[r, c] = bit;
                }
            }

            upward = !upward;
        }

        int totalModules = dim + (2 * quietZoneModules);
        width = totalModules * modulePixels;
        height = totalModules * modulePixels;
        stride = width * 4;
        byte[] bgra = new byte[stride * height];
        bgra.AsSpan().Fill(255); // White background

        for (int mr = 0; mr < dim; mr++)
        {
            for (int mc = 0; mc < dim; mc++)
            {
                if (!modules[mr, mc])
                {
                    continue;
                }

                int py0 = (mr + quietZoneModules) * modulePixels;
                int px0 = (mc + quietZoneModules) * modulePixels;
                for (int dy = 0; dy < modulePixels; dy++)
                {
                    int rowOff = (py0 + dy) * stride;
                    for (int dx = 0; dx < modulePixels; dx++)
                    {
                        int off = rowOff + ((px0 + dx) * 4);
                        bgra[off + 0] = 0;
                        bgra[off + 1] = 0;
                        bgra[off + 2] = 0;
                        bgra[off + 3] = 255;
                    }
                }
            }
        }

        return bgra;
    }

    private static void BinarizeBgra(ReadOnlySpan<byte> bgra, int width, int height, int stride, Span<byte> binary)
    {
        // Single luminance pass: stash luma into `binary` while summing; the
        // threshold pass then reads bytes instead of recomputing 3-channel luma.
        long sum = 0;
        for (int y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * stride, width * 4);
            var bRow = binary.Slice(y * width, width);
            for (int x = 0; x < width; x++)
            {
                int i = x * 4;
                byte lum = (byte)(((row[i + 2] * 77) + (row[i + 1] * 150) + (row[i] * 29)) >> 8);
                bRow[x] = lum;
                sum += lum;
            }
        }

        int threshold = (int)(sum / Math.Max(1, (long)width * height));
        threshold = Math.Clamp(threshold, 32, 224);

        for (int i = 0; i < binary.Length; i++)
        {
            binary[i] = binary[i] < threshold ? (byte)1 : (byte)0;
        }
    }

    private static bool TryDecodeQrFromBinary(ReadOnlySpan<byte> binary, int width, int height, out string decoded)
    {
        decoded = null;

        // Find bounding box of dark modules and estimate module size from top-left finder pattern.
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
        {
            var row = binary.Slice(y * width, width);
            for (int x = 0; x < width; x++)
            {
                if (row[x] != 0)
                {
                    if (x < minX)
                    {
                        minX = x;
                    }

                    if (x > maxX)
                    {
                        maxX = x;
                    }

                    if (y < minY)
                    {
                        minY = y;
                    }

                    if (y > maxY)
                    {
                        maxY = y;
                    }
                }
            }
        }

        if (maxX <= minX + 14 || maxY <= minY + 14)
        {
            return false;
        }

        int boxW = maxX - minX + 1;
        int boxH = maxY - minY + 1;

        // Estimate module size by measuring the top-left finder top border run length (7 modules wide).
        int topRun = 0;
        var firstRow = binary.Slice(minY * width, width);
        for (int x = minX; x <= maxX && firstRow[x] != 0; x++)
        {
            topRun++;
        }

        if (topRun < 7)
        {
            return false;
        }

        double moduleSize = topRun / 7.0;
        int approxDim = (int)Math.Round(((boxW + boxH) * 0.5) / moduleSize);
        int version = Math.Clamp((int)Math.Round((approxDim - 17) / 4.0), 1, 10);
        int dim = 17 + (4 * version);

        double stepX = (double)boxW / dim;
        double stepY = (double)boxH / dim;

        bool[,] grid = new bool[dim, dim];
        for (int r = 0; r < dim; r++)
        {
            int sy = Math.Clamp(minY + (int)((r + 0.5) * stepY), 0, height - 1);
            for (int c = 0; c < dim; c++)
            {
                int sx = Math.Clamp(minX + (int)((c + 0.5) * stepX), 0, width - 1);
                grid[r, c] = binary[(sy * width) + sx] != 0;
            }
        }

        // Verify top-left, top-right, bottom-left finder centers are dark.
        if (!grid[3, 3] || !grid[3, dim - 4] || !grid[dim - 4, 3])
        {
            return false;
        }

        int rawFormat = ReadFormatBits(grid);
        int unmaskedFormat = rawFormat ^ 0x5412;
        int formatData = (unmaskedFormat >> 10) & 0x1F;
        int ecLevel = (formatData >> 3) & 0x03;
        int maskPattern = formatData & 0x07;

        bool[,] isFunc = BuildFunctionPatternMap(dim, version);
        int maxCodewords = DataCodewordTable[version - 1, ecLevel];
        byte[] codewords = new byte[maxCodewords];
        int bitIdx = 0;
        bool upward = true;

        for (int right = dim - 1; right >= 1 && bitIdx < maxCodewords * 8; right -= 2)
        {
            if (right == 6)
            {
                right = 5;
            }

            for (int v = 0; v < dim && bitIdx < maxCodewords * 8; v++)
            {
                int r = upward ? (dim - 1 - v) : v;
                for (int j = 0; j < 2 && bitIdx < maxCodewords * 8; j++)
                {
                    int c = right - j;
                    if (isFunc[r, c])
                    {
                        continue;
                    }

                    bool bit = grid[r, c] ^ EvaluateMask(maskPattern, r, c);
                    if (bit)
                    {
                        codewords[bitIdx >> 3] |= (byte)(1 << (7 - (bitIdx & 7)));
                    }

                    bitIdx++;
                }
            }

            upward = !upward;
        }

        return TryDecodeQrBitstream(codewords, version, out decoded);
    }

    private static bool TryDecodeQrBitstream(ReadOnlySpan<byte> codewords, int version, out string text)
    {
        text = null;
        int bitPos = 0;
        int totalBits = codewords.Length * 8;
        var sb = new StringBuilder();

        while (bitPos + 4 <= totalBits)
        {
            int mode = ReadBits(codewords, ref bitPos, 4);
            if (mode == 0)
            {
                break; // Terminator
            }

            if (mode == 0b0100)
            {
                // Byte mode
                int countBits = version <= 9 ? 8 : 16;
                if (bitPos + countBits > totalBits)
                {
                    return false;
                }

                int count = ReadBits(codewords, ref bitPos, countBits);
                if (count < 0 || bitPos + (count * 8) > totalBits)
                {
                    return false;
                }

                byte[] bytes = ArrayPool<byte>.Shared.Rent(count);
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        bytes[i] = (byte)ReadBits(codewords, ref bitPos, 8);
                    }

                    sb.Append(Encoding.UTF8.GetString(bytes, 0, count));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(bytes);
                }
            }
            else if (mode == 0b0010)
            {
                // Alphanumeric mode
                int countBits = version <= 9 ? 9 : 11;
                if (bitPos + countBits > totalBits)
                {
                    return false;
                }

                int count = ReadBits(codewords, ref bitPos, countBits);
                while (count >= 2 && bitPos + 11 <= totalBits)
                {
                    int v = ReadBits(codewords, ref bitPos, 11);
                    sb.Append(AlphanumericTable[v / 45]);
                    sb.Append(AlphanumericTable[v % 45]);
                    count -= 2;
                }

                if (count == 1 && bitPos + 6 <= totalBits)
                {
                    int v = ReadBits(codewords, ref bitPos, 6);
                    if (v < AlphanumericTable.Length)
                    {
                        sb.Append(AlphanumericTable[v]);
                    }
                }
            }
            else if (mode == 0b0001)
            {
                // Numeric mode
                int countBits = version <= 9 ? 10 : 12;
                if (bitPos + countBits > totalBits)
                {
                    return false;
                }

                int count = ReadBits(codewords, ref bitPos, countBits);
                while (count >= 3 && bitPos + 10 <= totalBits)
                {
                    int v = ReadBits(codewords, ref bitPos, 10);
                    sb.Append(v.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
                    count -= 3;
                }

                if (count == 2 && bitPos + 7 <= totalBits)
                {
                    int v = ReadBits(codewords, ref bitPos, 7);
                    sb.Append(v.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
                }
                else if (count == 1 && bitPos + 4 <= totalBits)
                {
                    int v = ReadBits(codewords, ref bitPos, 4);
                    sb.Append(v.ToString("D1", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            else
            {
                break;
            }
        }

        if (sb.Length == 0)
        {
            return false;
        }

        text = sb.ToString();
        return true;
    }

    internal static byte[] RenderCode128Bgra(string payload, int moduleWidth, int barHeight, int quietZoneModules, out int width, out int height, out int stride)
    {
        var values = new List<int>(payload.Length + 3) { 104 }; // Start Code B
        int checksum = 104;
        for (int i = 0; i < payload.Length; i++)
        {
            int v = Math.Clamp(payload[i] - 32, 0, 94);
            values.Add(v);
            checksum += (i + 1) * v;
        }

        values.Add(checksum % 103);

        int totalModules = (quietZoneModules * 2) + (values.Count * 11) + 13;
        width = totalModules * moduleWidth;
        height = barHeight;
        stride = width * 4;
        byte[] bgra = new byte[stride * height];
        bgra.AsSpan().Fill(255);

        int modIdx = quietZoneModules;
        for (int s = 0; s < values.Count; s++)
        {
            ushort pat = Code128Patterns[values[s]];
            for (int b = 10; b >= 0; b--)
            {
                if (((pat >> b) & 1) != 0)
                {
                    FillBarModule(bgra, stride, height, modIdx, moduleWidth);
                }

                modIdx++;
            }
        }

        for (int b = 12; b >= 0; b--)
        {
            if (((Code128StopPattern >> b) & 1) != 0)
            {
                FillBarModule(bgra, stride, height, modIdx, moduleWidth);
            }

            modIdx++;
        }

        return bgra;
    }

    private static void FillBarModule(byte[] bgra, int stride, int height, int modIdx, int moduleWidth)
    {
        int x0 = modIdx * moduleWidth;
        for (int y = 0; y < height; y++)
        {
            int rowOff = y * stride;
            for (int dx = 0; dx < moduleWidth; dx++)
            {
                int off = rowOff + ((x0 + dx) * 4);
                bgra[off + 0] = 0;
                bgra[off + 1] = 0;
                bgra[off + 2] = 0;
                bgra[off + 3] = 255;
            }
        }
    }

    private static bool TryDecodeCode128FromBinary(ReadOnlySpan<byte> binary, int width, int height, out string text)
    {
        text = null;
        int midY = height / 2;
        var row = binary.Slice(midY * width, width);

        int firstBar = -1, lastBar = -1;
        for (int x = 0; x < width; x++)
        {
            if (row[x] != 0)
            {
                if (firstBar < 0)
                {
                    firstBar = x;
                }

                lastBar = x;
            }
        }

        if (firstBar < 0 || lastBar - firstBar < 35)
        {
            return false;
        }

        // First bar of Start-B is 2 modules wide.
        int firstRun = 0;
        for (int x = firstBar; x <= lastBar && row[x] != 0; x++)
        {
            firstRun++;
        }

        if (firstRun < 2)
        {
            return false;
        }

        double modW = firstRun / 2.0;
        int totalMods = (int)Math.Round((lastBar - firstBar + 1) / modW);
        if (totalMods < 35 || (totalMods - 13) % 11 != 0)
        {
            return false;
        }

        int symbolCount = (totalMods - 13) / 11;
        if (symbolCount < 3)
        {
            return false;
        }

        int startPat = ReadCode128Pattern(row, width, firstBar, modW, 0, 11);
        if (startPat != Code128Patterns[104])
        {
            return false;
        }

        int stopPat = ReadCode128Pattern(row, width, firstBar, modW, symbolCount * 11, 13);
        if (stopPat != Code128StopPattern)
        {
            return false;
        }

        var sb = new StringBuilder(symbolCount - 2);
        int checksum = 104;
        for (int s = 1; s < symbolCount; s++)
        {
            int pat = ReadCode128Pattern(row, width, firstBar, modW, s * 11, 11);
            int val = Array.IndexOf(Code128Patterns, (ushort)pat);
            if (val < 0)
            {
                return false;
            }

            if (s == symbolCount - 1)
            {
                if ((checksum % 103) != val)
                {
                    return false;
                }
            }
            else if (val <= 94)
            {
                sb.Append((char)(val + 32));
                checksum += s * val;
            }
        }

        if (sb.Length == 0)
        {
            return false;
        }

        text = sb.ToString();
        return true;
    }

    private static int ReadCode128Pattern(ReadOnlySpan<byte> row, int width, int firstBar, double modW, int modStart, int bitCount)
    {
        int pat = 0;
        for (int i = 0; i < bitCount; i++)
        {
            int sx = Math.Clamp(firstBar + (int)(((modStart + i) + 0.5) * modW), 0, width - 1);
            pat = (pat << 1) | (row[sx] != 0 ? 1 : 0);
        }

        return pat;
    }

    private static bool[,] BuildFunctionPatternMap(int dim, int version)
    {
        bool[,] isFunc = new bool[dim, dim];
        MarkRect(isFunc, 0, 0, 9, 9, dim);
        MarkRect(isFunc, dim - 8, 0, 8, 9, dim);
        MarkRect(isFunc, 0, dim - 8, 9, 8, dim);

        for (int i = 0; i < dim; i++)
        {
            isFunc[6, i] = true;
            isFunc[i, 6] = true;
        }

        if (version >= 2)
        {
            int ax = dim - 7;
            int ay = dim - 7;
            MarkRect(isFunc, ax - 2, ay - 2, 5, 5, dim);
        }

        return isFunc;
    }

    private static void MarkRect(bool[,] map, int x0, int y0, int w, int h, int dim)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(dim, y0 + h); y++)
        {
            for (int x = Math.Max(0, x0); x < Math.Min(dim, x0 + w); x++)
            {
                map[y, x] = true;
            }
        }
    }

    private static void PlaceFinderPattern(bool[,] modules, bool[,] isFunc, int x0, int y0, int dim)
    {
        for (int dy = -1; dy <= 7; dy++)
        {
            for (int dx = -1; dx <= 7; dx++)
            {
                int x = x0 + dx;
                int y = y0 + dy;
                if (x < 0 || x >= dim || y < 0 || y >= dim)
                {
                    continue;
                }

                isFunc[y, x] = true;
                if (dx >= 0 && dx <= 6 && dy >= 0 && dy <= 6)
                {
                    int dist = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3));
                    modules[y, x] = dist != 2;
                }
            }
        }
    }

    private static void WriteFormatBits(bool[,] modules, int dim, int formatBits)
    {
        int[] rCoords = { 8, 8, 8, 8, 8, 8, 8, 8, 7, 5, 4, 3, 2, 1, 0 };
        int[] cCoords = { 0, 1, 2, 3, 4, 5, 7, 8, 8, 8, 8, 8, 8, 8, 8 };
        for (int i = 0; i < 15; i++)
        {
            bool bit = ((formatBits >> (14 - i)) & 1) != 0;
            modules[rCoords[i], cCoords[i]] = bit;
        }

        for (int i = 0; i < 8; i++)
        {
            modules[8, dim - 1 - i] = ((formatBits >> i) & 1) != 0;
        }

        for (int i = 0; i < 7; i++)
        {
            modules[dim - 7 + i, 8] = ((formatBits >> (14 - i)) & 1) != 0;
        }
    }

    private static int ReadFormatBits(bool[,] grid)
    {
        int[] rCoords = { 8, 8, 8, 8, 8, 8, 8, 8, 7, 5, 4, 3, 2, 1, 0 };
        int[] cCoords = { 0, 1, 2, 3, 4, 5, 7, 8, 8, 8, 8, 8, 8, 8, 8 };
        int bits = 0;
        for (int i = 0; i < 15; i++)
        {
            bits = (bits << 1) | (grid[rCoords[i], cCoords[i]] ? 1 : 0);
        }

        return bits;
    }

    private static bool EvaluateMask(int mask, int r, int c)
        => mask switch
        {
            0 => ((r + c) & 1) == 0,
            1 => (r & 1) == 0,
            2 => c % 3 == 0,
            3 => (r + c) % 3 == 0,
            4 => (((r / 2) + (c / 3)) & 1) == 0,
            5 => ((r * c) & 1) + ((r * c) % 3) == 0,
            6 => ((((r * c) & 1) + ((r * c) % 3)) & 1) == 0,
            7 => ((((r + c) & 1) + ((r * c) % 3)) & 1) == 0,
            _ => false,
        };

    private static void WriteBits(Span<byte> buf, ref int bitPos, int val, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            if (((val >> i) & 1) != 0)
            {
                buf[bitPos >> 3] |= (byte)(1 << (7 - (bitPos & 7)));
            }

            bitPos++;
        }
    }

    private static int ReadBits(ReadOnlySpan<byte> buf, ref int bitPos, int count)
    {
        int val = 0;
        for (int i = 0; i < count; i++)
        {
            int b = (buf[bitPos >> 3] >> (7 - (bitPos & 7))) & 1;
            val = (val << 1) | b;
            bitPos++;
        }

        return val;
    }
}
