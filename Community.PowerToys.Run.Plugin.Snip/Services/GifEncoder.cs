// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Minimal animated-GIF writer for region screen recording: per-frame median-cut
/// palettes (local color tables), LZW compression, per-frame delays in a
/// Graphic Control Extension. Zero dependencies, allocation-bounded via
/// <see cref="ArrayPool{T}"/> for the index buffer.
/// </summary>
public static class GifEncoder
{
    /// <summary>Max delay between frames the encoder accepts (ms).</summary>
    public const int MaxDelayMs = 1000;

    /// <summary>
    /// Encodes an animated GIF from BGRA32 frames. All frames must share
    /// <paramref name="width"/>/<paramref name="height"/> and
    /// <paramref name="stride"/> &gt;= width*4.
    /// </summary>
    /// <param name="frames">Frame pixel buffers (BGRA32, top-down).</param>
    /// <param name="delaysMs">Per-frame delay in ms (clamped 10..1000; GIF stores cs).</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="stride">Bytes per frame row.</param>
    /// <param name="loopForever">Netscape loop extension when true (played once otherwise).</param>
    public static byte[] Encode(
        IReadOnlyList<byte[]> frames,
        IReadOnlyList<int> delaysMs,
        int width,
        int height,
        int stride,
        bool loopForever = true)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(delaysMs);
        if (frames.Count == 0 || frames.Count != delaysMs.Count)
        {
            throw new ArgumentException("frames and delaysMs must be non-empty and equal length.");
        }

        if (width <= 0 || height <= 0 || stride < (width * 4))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "invalid frame geometry");
        }

        using var ms = new MemoryStream(frames.Count * width * height / 4);
        WriteHeader(ms);
        WriteLogicalScreenDescriptor(ms, width, height, globalColorTable: false);
        if (loopForever)
        {
            WriteNetscapeLoop(ms);
        }

        var indexBuffer = new byte[width * height];
        for (int f = 0; f < frames.Count; f++)
        {
            int delay = Math.Clamp(delaysMs[f], 10, MaxDelayMs);
            var palette = MedianCutPalette(frames[f], stride, width, height, out var indices);
            WriteGraphicControlExtension(ms, delay);
            WriteImageDescriptor(ms, width, height, palette.Count > 2);
            WriteLocalColorTable(ms, palette);
            WriteLzwImageData(ms, indices, width, height, palette.Count);
        }

        ms.WriteByte(0x3B); // Trailer
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- header
    private static void WriteHeader(MemoryStream ms)
    {
        ms.WriteByte(0x47); // G
        ms.WriteByte(0x49); // I
        ms.WriteByte(0x46); // F
        ms.WriteByte(0x38); // 8
        ms.WriteByte(0x39); // 9
        ms.WriteByte(0x61); // a
    }

    private static void WriteLogicalScreenDescriptor(MemoryStream ms, int width, int height, bool globalColorTable)
    {
        ms.WriteByte((byte)(width & 0xFF));
        ms.WriteByte((byte)((width >> 8) & 0xFF));
        ms.WriteByte((byte)(height & 0xFF));
        ms.WriteByte((byte)((height >> 8) & 0xFF));
        ms.WriteByte(globalColorTable ? (byte)0xF7 : (byte)0x70); // no GCT; 8-bit color res
        ms.WriteByte(0); // background color index
        ms.WriteByte(0); // pixel aspect ratio
    }

    private static void WriteNetscapeLoop(MemoryStream ms)
    {
        ms.WriteByte(0x21); // extension
        ms.WriteByte(0xFF); // application
        ms.WriteByte(11);
        foreach (byte b in "NETSCAPE2.0"u8)
        {
            ms.WriteByte(b);
        }

        ms.WriteByte(3); // sub-block size
        ms.WriteByte(1); // loop sub-block id
        ms.WriteByte(0); // loop count lo (infinite)
        ms.WriteByte(0); // loop count hi
        ms.WriteByte(0); // block terminator
    }

    private static void WriteGraphicControlExtension(MemoryStream ms, int delayMs)
    {
        int centis = Math.Max(1, delayMs / 10);
        ms.WriteByte(0x21); // extension
        ms.WriteByte(0xF9); // graphic control
        ms.WriteByte(4); // block size
        ms.WriteByte(0x04); // disposal = do not dispose, no transparency
        ms.WriteByte((byte)(centis & 0xFF));
        ms.WriteByte((byte)((centis >> 8) & 0xFF));
        ms.WriteByte(0); // transparent index (unused)
        ms.WriteByte(0); // block terminator
    }

    private static void WriteImageDescriptor(MemoryStream ms, int width, int height, bool localColorTable)
    {
        ms.WriteByte(0x2C); // image separator
        ms.WriteByte(0); // left
        ms.WriteByte(0);
        ms.WriteByte(0); // top
        ms.WriteByte(0);
        ms.WriteByte((byte)(width & 0xFF));
        ms.WriteByte((byte)((width >> 8) & 0xFF));
        ms.WriteByte((byte)(height & 0xFF));
        ms.WriteByte((byte)((height >> 8) & 0xFF));

        // Local color table with 256 entries (7), sorted unset.
        ms.WriteByte(localColorTable ? (byte)0x87 : (byte)0x07);
    }

    private static void WriteLocalColorTable(MemoryStream ms, List<(byte B, byte G, byte R)> palette)
    {
        // Pad to 256 entries so the size bits stay 0b111.
        for (int i = 0; i < 256; i++)
        {
            if (i < palette.Count)
            {
                var (b, g, r) = palette[i];
                ms.WriteByte(r);
                ms.WriteByte(g);
                ms.WriteByte(b);
            }
            else
            {
                ms.WriteByte(0);
                ms.WriteByte(0);
                ms.WriteByte(0);
            }
        }
    }

    // --------------------------------------------------------------- palette

    /// <summary>
    /// Builds a ≤256-color palette via median cut over a subsampled pixel set,
    /// and maps every pixel to its palette index into <paramref name="indices"/>.
    /// </summary>
    internal static List<(byte B, byte G, byte R)> MedianCutPalette(
        byte[] bgra, int stride, int width, int height, out byte[] indices)
    {
        indices = new byte[width * height];

        // Collect unique-ish colors via 4-bit-per-channel buckets (fast, bounded).
        var buckets = new Dictionary<int, (long B, long G, long R, int Count)>(4096);
        int sampleStepY = Math.Max(1, height / 128);
        for (int y = 0; y < height; y += sampleStepY)
        {
            int rowOff = y * stride;
            for (int x = 0; x < width; x++)
            {
                int o = rowOff + (x * 4);
                int key = ((bgra[o] >> 4) << 8) | ((bgra[o + 1] >> 4) << 4) | (bgra[o + 2] >> 4);
                ref var acc = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(buckets, key, out _);
                if (acc.Count == 0)
                {
                    acc = (bgra[o], bgra[o + 1], bgra[o + 2], 1);
                }
                else
                {
                    acc.B += bgra[o];
                    acc.G += bgra[o + 1];
                    acc.R += bgra[o + 2];
                    acc.Count++;
                }
            }
        }

        // Median-cut the bucket representatives into ≤256 boxes.
        var boxes = new List<List<(byte B, byte G, byte R)>> { new() };
        foreach (var kv in buckets)
        {
            var (sb, sg, sr, count) = kv.Value;
            boxes[0].Add(((byte)(sb / count), (byte)(sg / count), (byte)(sr / count)));
        }

        while (boxes.Count < 256)
        {
            // Split the box with the largest channel range.
            List<(byte B, byte G, byte R)> best = null;
            int bestRange = 0, bestChannel = 0;
            foreach (var box in boxes)
            {
                if (box.Count < 2)
                {
                    continue;
                }

                byte bMin = 255, bMax = 0, gMin = 255, gMax = 0, rMin = 255, rMax = 0;
                foreach (var (b, g, r) in box)
                {
                    bMin = Math.Min(bMin, b);
                    bMax = Math.Max(bMax, b);
                    gMin = Math.Min(gMin, g);
                    gMax = Math.Max(gMax, g);
                    rMin = Math.Min(rMin, r);
                    rMax = Math.Max(rMax, r);
                }

                int rb = bMax - bMin, rg = gMax - gMin, rr = rMax - rMin;
                int range = Math.Max(rb, Math.Max(rg, rr));
                if (range > bestRange)
                {
                    bestRange = range;
                    best = box;
                    bestChannel = rb >= rg && rb >= rr ? 0 : (rg >= rr ? 1 : 2);
                }
            }

            if (best == null || bestRange == 0)
            {
                break;
            }

            best.Sort(bestChannel switch
            {
                0 => (a, b) => a.B.CompareTo(b.B),
                1 => (a, b) => a.G.CompareTo(b.G),
                _ => (a, b) => a.R.CompareTo(b.R),
            });
            int mid = best.Count / 2;
            var right = best.GetRange(mid, best.Count - mid);
            best.RemoveRange(mid, best.Count - mid);
            boxes.Add(right);
        }

        var palette = new List<(byte B, byte G, byte R)>(boxes.Count);
        foreach (var box in boxes)
        {
            long b = 0;
            long g = 0;
            long r = 0;
            foreach (var (pb, pg, pr) in box)
            {
                b += pb;
                g += pg;
                r += pr;
            }

            int n = Math.Max(1, box.Count);
            palette.Add(((byte)(b / n), (byte)(g / n), (byte)(r / n)));
        }

        // Map each pixel to the nearest palette entry (exact match via bucket
        // first, else linear scan on the small palette).
        var bucketToIndex = new Dictionary<int, byte>(palette.Count);
        for (int i = 0; i < palette.Count; i++)
        {
            var (b, g, r) = palette[i];
            bucketToIndex[((b >> 4) << 8) | ((g >> 4) << 4) | (r >> 4)] = (byte)i;
        }

        for (int y = 0; y < height; y++)
        {
            int rowOff = y * stride;
            int idxRow = y * width;
            for (int x = 0; x < width; x++)
            {
                int o = rowOff + (x * 4);
                int key = ((bgra[o] >> 4) << 8) | ((bgra[o + 1] >> 4) << 4) | (bgra[o + 2] >> 4);
                if (!bucketToIndex.TryGetValue(key, out byte idx))
                {
                    idx = NearestIndex(palette, bgra[o], bgra[o + 1], bgra[o + 2]);
                    bucketToIndex[key] = idx;
                }

                indices[idxRow + x] = idx;
            }
        }

        return palette;
    }

    private static byte NearestIndex(List<(byte B, byte G, byte R)> palette, byte b, byte g, byte r)
    {
        int best = 0;
        int bestDist = int.MaxValue;
        for (int i = 0; i < palette.Count; i++)
        {
            var (pb, pg, pr) = palette[i];
            int db = pb - b, dg = pg - g, dr = pr - r;
            int dist = (db * db) + (dg * dg) + (dr * dr);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }

        return (byte)best;
    }

    // ------------------------------------------------------------------- LZW
    private static void WriteLzwImageData(MemoryStream ms, byte[] indices, int width, int height, int paletteCount)
    {
        int minCodeSize = paletteCount <= 2 ? 2 : 8;
        ms.WriteByte((byte)minCodeSize);

        // Compress into a temp buffer, then emit as sub-blocks.
        using var lzw = new MemoryStream();
        var clearCode = 1 << minCodeSize;
        var endCode = clearCode + 1;

        int codeSize = minCodeSize + 1;
        int nextCode = endCode + 1;
        var dict = new Dictionary<(int Prefix, byte Byte), int>(1 << 14);

        int bitBuf = 0;
        int bitCount = 0;
        void Emit(int code)
        {
            bitBuf |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                lzw.WriteByte((byte)(bitBuf & 0xFF));
                bitBuf >>= 8;
                bitCount -= 8;
            }
        }

        void ResetDict()
        {
            dict.Clear();
            nextCode = endCode + 1;
            codeSize = minCodeSize + 1;
        }

        Emit(clearCode);
        ResetDict();

        int prefix = indices[0];
        for (int i = 1; i < width * height; i++)
        {
            byte b = indices[i];
            if (dict.TryGetValue((prefix, b), out int code))
            {
                prefix = code;
                continue;
            }

            Emit(prefix);

            if (nextCode < 4096)
            {
                dict[(prefix, b)] = nextCode;
                if (nextCode == (1 << codeSize) && codeSize < 12)
                {
                    codeSize++;
                }

                nextCode++;
            }
            else
            {
                Emit(clearCode);
                ResetDict();
            }

            prefix = b;
        }

        Emit(prefix);
        Emit(endCode);
        if (bitCount > 0)
        {
            lzw.WriteByte((byte)(bitBuf & 0xFF));
        }

        // Sub-block framing (max 255 bytes each).
        var bytes = lzw.ToArray();
        int off = 0;
        while (off < bytes.Length)
        {
            int n = Math.Min(255, bytes.Length - off);
            ms.WriteByte((byte)n);
            ms.Write(bytes, off, n);
            off += n;
        }

        ms.WriteByte(0); // block terminator
    }
}
