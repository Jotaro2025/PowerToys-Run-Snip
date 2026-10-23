// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// Adapted from LdotJdot/TDS (MIT) ScreenShot ScrollStitcher for WPF WriteableBitmap.
#pragma warning disable SA1117, SA1214, SA1316, SA1407, SA1503, SA1508, SA1512, SA1513, SA1515, SA1516, SA1518, SA1519

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

public static class ScrollStitcher
{
    /// <summary>Hard cap on stitched output height (px). Capture auto-stops near it; stitch truncates beyond it.</summary>
    public const int MaxStitchedHeightPx = 32768;

    /// <summary>Hard cap on stitched BGRA buffer size (bytes) — keeps wide monitors from a multi-GB single allocation.</summary>
    public const long MaxStitchedBytes = 512L * 1024 * 1024;

    /// <summary>Effective max output height for a given tile width (min of px cap and byte cap).</summary>
    public static int ResolveMaxStitchedHeight(int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        long byBytes = MaxStitchedBytes / CaptureBuffers.BgraStride(width);
        return (int)Math.Max(1, Math.Min(MaxStitchedHeightPx, byBytes));
    }

    /// <summary>
    /// Rows <paramref name="next"/> would add below <paramref name="prev"/> (0 for full duplicate).
    /// Thread-pool safe; used during capture to track the projected stitched height.
    /// </summary>
    internal static int EstimateContributedRows(ScrollTilePixels prev, ScrollTilePixels next, int scrollHintPx = 0)
    {
        ArgumentNullException.ThrowIfNull(prev);
        ArgumentNullException.ThrowIfNull(next);
        var (topSkip, _, relation) = AlignVerticalTilesPixels(prev, next, scrollHintPx);
        return relation switch
        {
            TileRelation.FullDuplicate => 0,
            TileRelation.NoOverlap => next.Height,
            _ => Math.Max(0, next.Height - topSkip),
        };
    }

    public sealed class StitchOffset
    {
        public int TileIndex { get; init; }
        public int TopSkip { get; init; }
        public int RowsContributed { get; init; }
        public int DestY { get; init; }
        public string Mode { get; init; } = "anchor";
    }

    /// <summary>BGRA stitch output — materialize with <see cref="ToWriteableBitmap"/> on the UI STA.</summary>
    public sealed class PixelStitchResult : IDisposable
    {
        private byte[] _pixels;
        private bool _rented;
        private bool _disposed;

        public required byte[] Pixels
        {
            get => _pixels;
            init { _pixels = value; }
        }

        public required int Width { get; init; }

        public required int Height { get; init; }

        public required int Stride { get; init; }

        public required IReadOnlyList<StitchOffset> Offsets { get; init; }

        public required int TotalHeight { get; init; }

        /// <summary>True when output was clipped at <see cref="ResolveMaxStitchedHeight"/>.</summary>
        public bool Truncated { get; init; }

        public bool RentedFromPool
        {
            get => _rented;
            init => _rented = value;
        }

        /// <summary>UI STA only.</summary>
        public WriteableBitmap ToWriteableBitmap()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var tile = new ScrollTilePixels(_pixels, Width, Height, Stride, rentedFromPool: false);
            try
            {
                return tile.ToWriteableBitmap();
            }
            finally
            {
                // Detach so ScrollTilePixels.Dispose does not clear our array.
                // ScrollTilePixels only Returns to pool when rentedFromPool is true.
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_rented && _pixels != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(_pixels);
            }

            _pixels = null;
        }
    }

    public sealed class StitchResult
    {
        public required WriteableBitmap Bitmap { get; init; }
        public required IReadOnlyList<StitchOffset> Offsets { get; init; }
        public required int TotalHeight { get; init; }
    }

    internal enum TileRelation
    {
        FullDuplicate,
        NoOverlap,
        PartialOverlap,
    }

    private const int MinPartialOverlapPx = 4;
    private const double MinPartialMatchRatio = 0.82;
    private const double PeakRatioTolerance = 0.06;
    private const double RawPeakMinFraction = 0.95;
    private const int SadMatchTolerance = 14;
    /// <summary>水平边缘差分匹配容差，用于 TextEdge/EdgeText——区分重复文件夹行与真实滚动重叠。</summary>
    private const int TextEdgeTolerance = 4;

    private const int RefineRadiusPx = 2;
    /// <summary>hint 窗口候选超过此数时先做 step=2 粗搜，再对 top-K 全分辨率精修。</summary>
    private const int CoarseCandidateThreshold = 36;
    private const int CoarseTopK = 5;
    /// <summary>低于此 contrib 的 dh 不写入 scrollHint，避免 tile#1 的 dh=7 之类误估污染后续。</summary>
    private const int MinHintContribPx = 16;
    /// <summary>卡片流等弱纹理场景：全局峰值低于主阈值但仍可接受时的下限。</summary>
    private const double SoftPartialMatchRatio = 0.72;

    private const int SeamBandMaxRows = 56;
    private const int SeamBandMinRows = 12;
    private const double ScrollHintPenaltyPerPx = 2.5;
    private const double ScrollHintSlackRatio = 0.35;

    private readonly struct DhMetrics
    {
        public int Dh { get; init; }
        public double Raw { get; init; }
        public double Sad { get; init; }
        public double EdgeSad { get; init; }
        public double EdgeRaw { get; init; }
        public double TextEdge { get; init; }
        public double EdgeText { get; init; }
    /// <summary>
        /// EdgeText/TextEdge 主导；SAD/Raw 为连续灰度信号，无二值化量化误差。
    /// </summary>
        public double Score(int scrollHintPx)
        {
            double baseScore = EdgeText * 2200.0 + TextEdge * 600.0
                               + EdgeSad * 150.0 + Sad * 80.0
                               + EdgeRaw * 400.0 + Raw * 120.0;
            if (scrollHintPx <= 0) return baseScore;
            double slack = Math.Max(SeamBandMinRows, scrollHintPx * ScrollHintSlackRatio);
            double dev = Math.Abs(Dh - scrollHintPx);
            if (dev > slack)
                baseScore -= (dev - slack) * ScrollHintPenaltyPerPx;
            return baseScore;
        }
    }

    /// <summary>Thread-pool safe: stitches owned BGRA tiles into a pixel buffer (no WriteableBitmap).</summary>
    public static PixelStitchResult Stitch(IReadOnlyList<ScrollTilePixels> tiles)
        => StitchPixelsCore(tiles, 0, null);

    public static PixelStitchResult Stitch(
        IReadOnlyList<ScrollTilePixels> tiles,
        int estimatedScrollPxPerTile,
        IReadOnlyList<int> perTileScrollPx = null)
        => StitchPixelsCore(tiles, estimatedScrollPxPerTile, perTileScrollPx);

    /// <summary>
    /// Legacy UI-thread helper: copies WriteableBitmap tiles into pixel buffers, stitches off any thread
    /// affinity for Lock during compose is avoided by working on managed arrays.
    /// Prefer <see cref="Stitch(IReadOnlyList{ScrollTilePixels})"/>.
    /// </summary>
    public static StitchResult Stitch(IReadOnlyList<WriteableBitmap> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        var owned = new List<ScrollTilePixels>(tiles.Count);
        try
        {
            foreach (var wb in tiles)
            {
                owned.Add(CopyWriteableBitmapToTile(wb));
            }

            using var pixels = StitchPixelsCore(owned, 0, null);
            return new StitchResult
            {
                Bitmap = pixels.ToWriteableBitmap(),
                Offsets = pixels.Offsets,
                TotalHeight = pixels.TotalHeight,
            };
        }
        finally
        {
            foreach (var t in owned)
            {
                t.Dispose();
            }
        }
    }

    public static StitchResult Stitch(
        IReadOnlyList<WriteableBitmap> tiles,
        int estimatedScrollPxPerTile = 0,
        IReadOnlyList<int> perTileScrollPx = null)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        var owned = new List<ScrollTilePixels>(tiles.Count);
        try
        {
            foreach (var wb in tiles)
            {
                owned.Add(CopyWriteableBitmapToTile(wb));
            }

            using var pixels = StitchPixelsCore(owned, estimatedScrollPxPerTile, perTileScrollPx);
            return new StitchResult
            {
                Bitmap = pixels.ToWriteableBitmap(),
                Offsets = pixels.Offsets,
                TotalHeight = pixels.TotalHeight,
            };
        }
        finally
        {
            foreach (var t in owned)
            {
                t.Dispose();
            }
        }
    }

    private static ScrollTilePixels CopyWriteableBitmapToTile(WriteableBitmap wb)
    {
        int w = wb.PixelWidth;
        int h = wb.PixelHeight;
        int stride = CaptureBuffers.BgraStride(w);
        int byteCount = CaptureBuffers.BgraByteCount(w, h);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount);
        wb.Lock();
        try
        {
            unsafe
            {
                int srcStride = wb.BackBufferStride;
                byte* src = (byte*)wb.BackBuffer;
                int rowBytes = w * 4;
                for (int y = 0; y < h; y++)
                {
                    new ReadOnlySpan<byte>(src + ((long)y * srcStride), rowBytes)
                        .CopyTo(buffer.AsSpan(y * stride, rowBytes));
                }
            }
        }
        finally
        {
            wb.Unlock();
        }

        return new ScrollTilePixels(buffer, w, h, stride, rentedFromPool: true);
    }

    private static PixelStitchResult StitchPixelsCore(
        IReadOnlyList<ScrollTilePixels> tiles,
        int estimatedScrollPxPerTile,
        IReadOnlyList<int> perTileScrollPx)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        if (tiles.Count == 0) throw new ArgumentException("At least one tile is required.", nameof(tiles));

        var first = tiles[0];
        int width = first.Width;
        int firstHeight = first.Height;
        if (width <= 0 || firstHeight <= 0)
            throw new ArgumentException("First tile has zero area.", nameof(tiles));
        ValidatePixelTiles(tiles, width);

        var offsets = new StitchOffset[tiles.Count];
        int maxHeight = ResolveMaxStitchedHeight(width);
        bool truncated = false;
        if (firstHeight > maxHeight)
        {
            firstHeight = maxHeight;
            truncated = true;
        }

        int totalHeight = firstHeight;
        int scrollHintPx = estimatedScrollPxPerTile;
        var recentDh = new List<int>(8);
        offsets[0] = new StitchOffset
        {
            TileIndex = 0,
            TopSkip = 0,
            RowsContributed = firstHeight,
            DestY = 0,
            Mode = "anchor",
        };

        for (int i = 1; i < tiles.Count; i++)
        {
            var prev = tiles[i - 1];
            var cur = tiles[i];
            int curHeight = cur.Height;

            if (truncated)
            {
                offsets[i] = new StitchOffset
                {
                    TileIndex = i,
                    TopSkip = curHeight,
                    RowsContributed = 0,
                    DestY = totalHeight,
                    Mode = "height-cap",
                };
                continue;
            }

            if (perTileScrollPx != null && i - 1 < perTileScrollPx.Count && perTileScrollPx[i - 1] > 0)
                scrollHintPx = perTileScrollPx[i - 1];

            var (topSkip, mode, relation) = AlignVerticalTilesPixels(prev, cur, scrollHintPx);

            if (relation == TileRelation.FullDuplicate)
            {
                offsets[i] = new StitchOffset
                {
                    TileIndex = i,
                    TopSkip = curHeight,
                    RowsContributed = 0,
                    DestY = totalHeight,
                    Mode = mode,
                };
                SnipVerbose.Scroll($"[stitch] tile {i}: full-duplicate — skipped");
                continue;
            }

            int contributed = relation == TileRelation.NoOverlap ? curHeight : curHeight - topSkip;
            if (totalHeight + contributed > maxHeight)
            {
                contributed = Math.Max(0, maxHeight - totalHeight);
                truncated = true;
                SnipVerbose.Scroll($"[stitch] tile {i}: height cap {maxHeight}px reached — truncating");
            }

            offsets[i] = new StitchOffset
            {
                TileIndex = i,
                TopSkip = relation == TileRelation.NoOverlap ? 0 : topSkip,
                RowsContributed = contributed,
                DestY = totalHeight,
                Mode = mode,
            };
            totalHeight += contributed;
            if (relation == TileRelation.PartialOverlap && contributed >= MinHintContribPx)
            {
                recentDh.Add(contributed);
                if (recentDh.Count > 6) recentDh.RemoveAt(0);
                scrollHintPx = Median(recentDh);
            }
            SnipVerbose.Scroll($"[stitch] tile {i}: {relation} skip={offsets[i].TopSkip} contrib={contributed} mode={mode} totalH={totalHeight} hint={scrollHintPx}");
        }

        return ComposeStitchedPixels(tiles, offsets, width, totalHeight, truncated);
    }

    private static PixelStitchResult ComposeStitchedPixels(
        IReadOnlyList<ScrollTilePixels> tiles, StitchOffset[] offsets, int width, int totalHeight, bool truncated)
    {
        int dstStride = CaptureBuffers.BgraStride(width);
        int byteCount = CaptureBuffers.BgraByteCount(width, totalHeight);
        var dest = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount);
        dest.AsSpan(0, byteCount).Clear();
        int pixelRowBytes = width * 4;

        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var off = offsets[i];
            if (off.RowsContributed <= 0) continue;
            var src = tile.DangerousGetArray();
            int srcStride = tile.Stride;
            int srcY = off.TopSkip;
            for (int row = 0; row < off.RowsContributed; row++)
            {
                int srcOffset = (srcY + row) * srcStride;
                int dstOffset = (off.DestY + row) * dstStride;
                src.AsSpan(srcOffset, pixelRowBytes).CopyTo(dest.AsSpan(dstOffset, pixelRowBytes));
            }
        }

        return new PixelStitchResult
        {
            Pixels = dest,
            Width = width,
            Height = totalHeight,
            Stride = dstStride,
            Offsets = offsets,
            TotalHeight = totalHeight,
            Truncated = truncated,
            RentedFromPool = true,
        };
    }

    private static (int topSkip, string mode, TileRelation relation) AlignVerticalTilesPixels(
        ScrollTilePixels prev, ScrollTilePixels next, int scrollHintPx = 0)
    {
        int width = prev.Width;
        int prevH = prev.Height;
        int nextH = next.Height;
        if (prevH <= 0 || nextH <= 0 || width <= 0)
            return (0, "no-overlap", TileRelation.NoOverlap);

        unsafe
        {
            fixed (byte* prevBase = prev.DangerousGetArray())
            fixed (byte* nextBase = next.DangerousGetArray())
            {
                return AlignVerticalTilesFromPointers(
                    prevBase, prev.Stride, prevH,
                    nextBase, next.Stride, nextH,
                    width, scrollHintPx);
            }
        }
    }

    private static void ValidatePixelTiles(IReadOnlyList<ScrollTilePixels> tiles, int width)
    {
        for (int i = 1; i < tiles.Count; i++)
        {
            var t = tiles[i];
            if (t.Width != width)
                throw new ArgumentException(
                    $"Tile {i} has width {t.Width}, expected {width}.",
                    nameof(tiles));
            if (t.Height != tiles[i - 1].Height)
                throw new ArgumentException(
                    $"Tile {i} height {t.Height} differs from previous tile.",
                    nameof(tiles));
            if (t.Height <= 0)
                throw new ArgumentException($"Tile {i} has zero height.", nameof(tiles));
        }
    }

    private static int Median(IReadOnlyList<int> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }

    /// <summary>
    /// Lightweight full-frame identity check used during capture to auto-stop
    /// when the page no longer scrolls (same threshold as stitch FullDuplicate).
    /// Safe on any thread for <see cref="ScrollTilePixels"/>.
    /// </summary>
    internal static bool IsNearlyIdentical(ScrollTilePixels prev, ScrollTilePixels next)
    {
        if (prev is null || next is null) return false;
        if (prev.Width != next.Width || prev.Height != next.Height)
            return false;
        int width = prev.Width;
        int h = prev.Height;
        if (width <= 0 || h <= 0) return false;

        unsafe
        {
            fixed (byte* prevBase = prev.DangerousGetArray())
            fixed (byte* nextBase = next.DangerousGetArray())
            {
                double raw = ComputeRawMatchRatio(
                    prevBase, prev.Stride, h, 0,
                    nextBase, next.Stride, h, 0,
                    h, width, stepX: 1, stepY: 1);
                return raw >= 0.999;
            }
        }
    }

    /// <summary>UI STA only — prefer the <see cref="ScrollTilePixels"/> overload.</summary>
    internal static bool IsNearlyIdentical(WriteableBitmap prev, WriteableBitmap next)
    {
        if (prev is null || next is null) return false;
        if (prev.PixelWidth != next.PixelWidth || prev.PixelHeight != next.PixelHeight)
            return false;
        int width = prev.PixelWidth;
        int h = prev.PixelHeight;
        if (width <= 0 || h <= 0) return false;

        prev.Lock();
        next.Lock();
        try
        {
            unsafe
            {
                double raw = ComputeRawMatchRatio(
                    (byte*)prev.BackBuffer, prev.BackBufferStride, h, 0,
                    (byte*)next.BackBuffer, next.BackBufferStride, h, 0,
                    h, width, stepX: 1, stepY: 1);
                return raw >= 0.999;
            }
        }
        finally
        {
            next.Unlock();
            prev.Unlock();
        }
    }

    public static int MeasureTopSkip(WriteableBitmap prev, WriteableBitmap next, int hintPx = 0)
    {
        var (skip, _, relation) = AlignVerticalTilesLocked(prev, next, hintPx);
        if (relation is TileRelation.FullDuplicate or TileRelation.NoOverlap) return 0;
        return skip;
    }

    private static (int topSkip, string mode, TileRelation relation) AlignVerticalTilesLocked(
        WriteableBitmap prev, WriteableBitmap next, int scrollHintPx = 0)
    {
        int width = prev.PixelWidth;
        int prevH = prev.PixelHeight;
        int nextH = next.PixelHeight;
        if (prevH <= 0 || nextH <= 0 || width <= 0)
            return (0, "no-overlap", TileRelation.NoOverlap);

        prev.Lock();
        next.Lock();
        try
        {
            unsafe
            {
                return AlignVerticalTilesFromPointers(
                    (byte*)prev.BackBuffer, prev.BackBufferStride, prevH,
                    (byte*)next.BackBuffer, next.BackBufferStride, nextH,
                    width, scrollHintPx);
            }
        }
        finally
        {
            next.Unlock();
            prev.Unlock();
        }
    }

    internal static unsafe (int topSkip, string mode) DeduceTopSkipFromPointers(
        byte* prevBase, int prevStride, int prevH,
        byte* nextBase, int nextStride, int nextH,
        int width, int estimatedScrollPxPerTile)
    {
        var (skip, mode, _) = AlignVerticalTilesFromPointers(
            prevBase, prevStride, prevH, nextBase, nextStride, nextH, width, estimatedScrollPxPerTile);
        return (skip, mode);
    }

    internal static unsafe (int topSkip, string mode, TileRelation relation) AlignVerticalTilesFromPointers(
        byte* prevBase, int prevStride, int prevH,
        byte* nextBase, int nextStride, int nextH,
        int width, int scrollHintPx = 0)
    {
        if (prevH <= 0 || nextH <= 0 || width <= 0)
            return (0, "no-overlap", TileRelation.NoOverlap);

        int h = Math.Min(prevH, nextH);
        int maxDh = h - MinPartialOverlapPx;
        if (maxDh < MinPartialOverlapPx)
            return (0, "no-overlap", TileRelation.NoOverlap);

        // FullDuplicate：两 tile 同行逐像素几乎相同（Raw≥99.9%），说明滚动未生效或重复截图。
        double rawIdentical = ComputeRawMatchRatio(
            prevBase, prevStride, prevH, 0,
            nextBase, nextStride, nextH, 0,
            h, width, stepX: 1, stepY: 1);
        if (rawIdentical >= 0.999)
            return (nextH, "raw-dup", TileRelation.FullDuplicate);

        int effectiveHint = scrollHintPx;
        List<int> candidates;
        string modeTag;

        if (scrollHintPx > 0)
        {
            // 有 hint 时跳过 FFT——hint 窗口 + SAD 已足够，FFT 是 tile#1 无 hint 时的冷启动路径。
            modeTag = "hint";
            candidates = BuildHintWindowCandidates(maxDh, scrollHintPx);
        }
        else
        {
            var phase = ScrollPhaseCorrelator.EstimateVerticalShift(
                prevBase, prevStride, prevH, nextBase, nextStride, nextH,
                width, MinPartialOverlapPx, maxDh, scrollHintPx: 0);
            effectiveHint = phase.Success ? phase.Dh : 0;
            modeTag = phase.Success ? "fft" : "fft-scan";
            candidates = BuildAllDhCandidates(maxDh);
        }

        var finals = ScoreAndClassify(
            prevBase, prevStride, prevH, nextBase, nextStride, nextH,
            width, h, maxDh, effectiveHint, candidates, modeTag);

        if (finals.relation == TileRelation.NoOverlap && scrollHintPx > 0)
        {
            SnipVerbose.Scroll(
                $"[stitch] hint-fallback: dh={scrollHintPx} (avoid full-tile stack after weak match)");
            return (h - scrollHintPx, "hint-fallback", TileRelation.PartialOverlap);
        }

        return finals;
    }

    private static List<int> BuildHintWindowCandidates(int maxDh, int scrollHintPx)
    {
        int slack = Math.Max(SeamBandMinRows, (int)(scrollHintPx * ScrollHintSlackRatio));
        int lo = Math.Max(MinPartialOverlapPx, scrollHintPx - slack);
        int hi = Math.Min(maxDh, scrollHintPx + slack);
        var list = new List<int>(Math.Max(0, hi - lo + 1));
        for (int dh = lo; dh <= hi; dh++)
            list.Add(dh);
        return list;
    }

    private static unsafe (int topSkip, string mode, TileRelation relation) ScoreAndClassify(
        byte* prevBase, int prevStride, int prevH,
        byte* nextBase, int nextStride, int nextH,
        int width, int h, int maxDh, int effectiveHint,
        List<int> candidates, string modeTag)
    {
        DhMetrics[] finals = ScoreCandidatesAdaptive(
            prevBase, prevStride, prevH, nextBase, nextStride, nextH,
            width, h, effectiveHint, candidates, maxDh);

        var result = TryClassify(finals, h, modeTag, effectiveHint);
        if (result.relation != TileRelation.NoOverlap && IsViable(finals, effectiveHint))
            return result;

        if (candidates.Count >= maxDh - MinPartialOverlapPx)
            return result;

        SnipVerbose.Scroll($"[stitch] rescan full dh range (was {modeTag}, viable={IsViable(finals, effectiveHint)})");
        finals = ScoreCandidatesAdaptive(
            prevBase, prevStride, prevH, nextBase, nextStride, nextH,
            width, h, effectiveHint, BuildAllDhCandidates(maxDh), maxDh);
        return TryClassify(finals, h, "rescan", effectiveHint);
    }

    /// <summary>大窗口先 step=2 粗搜，再对 top-K 做 step=1 精修。</summary>
    private static unsafe DhMetrics[] ScoreCandidatesAdaptive(
        byte* prevBase, int prevStride, int prevH,
        byte* nextBase, int nextStride, int nextH,
        int width, int h, int scrollHintPx, IReadOnlyList<int> candidates, int maxDh)
    {
        if (candidates.Count <= CoarseCandidateThreshold)
        {
            return ScoreCandidates(
                prevBase, prevStride, prevH, nextBase, nextStride, nextH,
                width, h, scrollHintPx, candidates, stepX: 1, stepY: 1);
        }

        var coarse = ScoreCandidates(
            prevBase, prevStride, prevH, nextBase, nextStride, nextH,
            width, h, scrollHintPx, candidates, stepX: 2, stepY: 2);
        if (coarse.Length == 0)
            return coarse;

        var refineSet = new SortedSet<int>();
        foreach (var m in coarse.Take(CoarseTopK))
        {
            for (int dh = m.Dh - RefineRadiusPx; dh <= m.Dh + RefineRadiusPx; dh++)
            {
                if (dh >= MinPartialOverlapPx && dh <= maxDh)
                    refineSet.Add(dh);
            }
        }
        if (refineSet.Count == 0)
            return coarse;

        return ScoreCandidates(
            prevBase, prevStride, prevH, nextBase, nextStride, nextH,
            width, h, scrollHintPx, refineSet.ToList(), stepX: 1, stepY: 1);
    }

    private static (int topSkip, string mode, TileRelation relation) TryClassify(
        DhMetrics[] finals, int h, string modeTag, int scrollHintPx)
    {
        if (finals.Length == 0)
            return (0, "no-overlap", TileRelation.NoOverlap);
        return ClassifyFromMetrics(finals, h, modeTag, scrollHintPx);
    }

    private static List<int> BuildAllDhCandidates(int maxDh)
    {
        var list = new List<int>(maxDh);
        for (int dh = MinPartialOverlapPx; dh <= maxDh; dh++)
            list.Add(dh);
        return list;
    }

    private static bool IsViable(DhMetrics[] ranked, int scrollHintPx)
    {
        if (ranked.Length == 0) return false;
        var top = ranked[0];
        return top.EdgeText >= MinPartialMatchRatio
               || top.TextEdge >= MinPartialMatchRatio
               || top.EdgeSad >= MinPartialMatchRatio
               || top.Sad >= MinPartialMatchRatio
               || top.Score(scrollHintPx) >= MinPartialMatchRatio * 1000.0;
    }

    private static int SeamBandRows(int overlapRows)
        => overlapRows <= 0 ? 0 : Math.Min(overlapRows, SeamBandMaxRows);

    private static unsafe DhMetrics[] ScoreCandidates(
        byte* prevBase, int prevStride, int prevH,
        byte* nextBase, int nextStride, int nextH,
        int width, int h, int scrollHintPx, IReadOnlyList<int> candidates,
        int stepX, int stepY)
    {
        var metrics = new DhMetrics[candidates.Count];
        Parallel.For(0, candidates.Count, i =>
        {
            int dh = candidates[i];
            int overlapRows = h - dh;
            if (overlapRows < MinPartialOverlapPx)
            {
                metrics[i] = new DhMetrics { Dh = dh };
                return;
            }
            int band = SeamBandRows(overlapRows);
            int edgeStart = overlapRows - band;
            metrics[i] = new DhMetrics
            {
                Dh = dh,
                Raw = ComputeRawMatchRatio(
                    prevBase, prevStride, prevH, dh,
                    nextBase, nextStride, nextH, 0,
                    overlapRows, width, stepX, stepY),
                Sad = ComputeSadMatchRatio(
                    prevBase, prevStride, prevH, dh,
                    nextBase, nextStride, nextH, 0,
                    overlapRows, width, stepX, stepY),
                EdgeRaw = ComputeRawMatchRatio(
                    prevBase, prevStride, prevH, dh + edgeStart,
                    nextBase, nextStride, nextH, edgeStart,
                    band, width, stepX, Math.Max(1, stepY)),
                EdgeSad = ComputeSadMatchRatio(
                    prevBase, prevStride, prevH, dh + edgeStart,
                    nextBase, nextStride, nextH, edgeStart,
                    band, width, stepX, Math.Max(1, stepY)),
                TextEdge = ComputeTextEdgeMatchRatio(
                    prevBase, prevStride, prevH, dh,
                    nextBase, nextStride, nextH, 0,
                    overlapRows, width, stepX, stepY),
                EdgeText = ComputeTextEdgeMatchRatio(
                    prevBase, prevStride, prevH, dh + edgeStart,
                    nextBase, nextStride, nextH, edgeStart,
                    band, width, stepX, Math.Max(1, stepY)),
            };
        });
        return metrics
            .OrderByDescending(m => m.Score(scrollHintPx))
            .ThenByDescending(m => m.EdgeText)
            .ThenByDescending(m => m.TextEdge)
            .ThenByDescending(m => m.EdgeSad)
            .ThenByDescending(m => m.Dh)
            .ToArray();
    }

    private static (int topSkip, string mode, TileRelation relation) ClassifyFromMetrics(
        DhMetrics[] ranked, int h, string modeTag, int scrollHintPx)
    {
        // ranked 已在 ScoreCandidates 排好序，避免重复 LINQ。
        double maxEdgeText = ranked[0].EdgeText;
        double maxText = ranked[0].TextEdge;
        double maxEdgeSad = ranked[0].EdgeSad;
        for (int i = 1; i < ranked.Length; i++)
        {
            if (ranked[i].EdgeText > maxEdgeText) maxEdgeText = ranked[i].EdgeText;
            if (ranked[i].TextEdge > maxText) maxText = ranked[i].TextEdge;
            if (ranked[i].EdgeSad > maxEdgeSad) maxEdgeSad = ranked[i].EdgeSad;
        }
        double softFloor = scrollHintPx > 0 ? SoftPartialMatchRatio : MinPartialMatchRatio;

        if (maxEdgeText < softFloor && maxText < softFloor && maxEdgeSad < softFloor)
        {
            SnipVerbose.Scroll($"[stitch] no-overlap: text={maxText:F3} edgeSad={maxEdgeSad:F3} edgeText={maxEdgeText:F3}");
            return (0, "no-overlap", TileRelation.NoOverlap);
        }

        double ratioFloor = Math.Max(softFloor, Math.Max(maxEdgeText, maxText) - PeakRatioTolerance);

        var viable = ranked
            .Where(m => m.EdgeText >= ratioFloor || m.TextEdge >= ratioFloor
                        || m.EdgeSad >= ratioFloor || m.Sad >= ratioFloor)
            .ToArray();
        if (viable.Length == 0)
        {
            // 弱匹配：有 hint 时取 hint 窗口内得分最高者，避免整 tile 叠加。
            if (scrollHintPx > 0)
            {
                var softBest = ranked[0];
                int softSkip = h - softBest.Dh;
                SnipVerbose.Scroll(
                    $"[stitch] partial-soft: dh={softBest.Dh} skip={softSkip} edgeText={softBest.EdgeText:F3} text={softBest.TextEdge:F3} hint={scrollHintPx} mode={modeTag}");
                return (softSkip, modeTag + "-soft", TileRelation.PartialOverlap);
            }
            SnipVerbose.Scroll($"[stitch] no-overlap: floor={ratioFloor:F3} maxEdgeSad={maxEdgeSad:F3}");
            return (0, "no-overlap", TileRelation.NoOverlap);
        }

        int bestDh = viable[0].Dh;
        if (scrollHintPx > 0)
        {
            double slack = Math.Max(SeamBandMinRows, scrollHintPx * ScrollHintSlackRatio);
            var hinted = viable
                .Where(m => Math.Abs(m.Dh - scrollHintPx) <= slack)
                .OrderByDescending(m => m.Score(scrollHintPx))
                .ThenByDescending(m => m.EdgeText)
                .ThenByDescending(m => m.TextEdge)
                .ToArray();
            if (hinted.Length > 0)
                bestDh = hinted[0].Dh;
        }

        double peakRaw = viable.Max(m => m.EdgeRaw);
        var bestRawDh = viable.OrderByDescending(m => m.EdgeRaw).First().Dh;
        var bestCandidate = viable.First(m => m.Dh == bestDh);
        if (peakRaw > 0 && bestCandidate.EdgeRaw < peakRaw * RawPeakMinFraction)
            bestDh = bestRawDh;

        var best = viable.First(m => m.Dh == bestDh);
        int topSkip = h - bestDh;
        SnipVerbose.Scroll(
            $"[stitch] partial: dh={bestDh} skip={topSkip} edgeText={best.EdgeText:F3} text={best.TextEdge:F3} sad={best.EdgeSad:F3} raw={best.EdgeRaw:F3} hint={scrollHintPx} mode={modeTag}");
        return (topSkip, modeTag, TileRelation.PartialOverlap);
    }

    /// <summary>
    /// Compare horizontal luminance edges — sensitive to text strokes, ignores flat backgrounds.
    /// </summary>
    private static unsafe double ComputeTextEdgeMatchRatio(
        byte* prevBase, int prevStride, int prevH, int prevStartY,
        byte* nextBase, int nextStride, int nextH, int nextStartY,
        int overlapRows, int width, int stepX, int stepY)
    {
        if (width < 2) return 0;
        int matches = 0;
        int total = 0;

        for (int r = 0; r < overlapRows; r += stepY)
        {
            int py = prevStartY + r;
            int ny = nextStartY + r;
            if (py >= prevH || ny >= nextH) continue;

            byte* prevRow = prevBase + (long)py * prevStride;
            byte* nextRow = nextBase + (long)ny * nextStride;
            for (int x = 0; x < width - 1; x += stepX)
            {
                int o0 = x * 4;
                int o1 = (x + 1) * 4;
                int edgeP = RowLum(prevRow, o1) - RowLum(prevRow, o0);
                int edgeN = RowLum(nextRow, o1) - RowLum(nextRow, o0);
                total++;
                if (Math.Abs(edgeP - edgeN) <= TextEdgeTolerance)
                    matches++;
            }
        }
        return total > 0 ? (double)matches / total : 0;
    }

    private static unsafe int RowLum(byte* row, int o)
        => (299 * row[o + 2] + 587 * row[o + 1] + 114 * row[o]) / 1000;

    private static unsafe double ComputeRawMatchRatio(
        byte* prevBase, int prevStride, int prevH, int prevStartY,
        byte* nextBase, int nextStride, int nextH, int nextStartY,
        int overlapRows, int width, int stepX, int stepY)
    {
        int matches = 0;
        int total = 0;

        for (int r = 0; r < overlapRows; r += stepY)
        {
            int py = prevStartY + r;
            int ny = nextStartY + r;
            if (py >= prevH || ny >= nextH) continue;

            byte* prevRow = prevBase + (long)py * prevStride;
            byte* nextRow = nextBase + (long)ny * nextStride;
            for (int x = 0; x < width; x += stepX)
            {
                total++;
                int o = x * 4;
                if (prevRow[o] == nextRow[o]
                    && prevRow[o + 1] == nextRow[o + 1]
                    && prevRow[o + 2] == nextRow[o + 2])
                    matches++;
            }
        }
        return total > 0 ? (double)matches / total : 0;
    }

    private static unsafe double ComputeSadMatchRatio(
        byte* prevBase, int prevStride, int prevH, int prevStartY,
        byte* nextBase, int nextStride, int nextH, int nextStartY,
        int overlapRows, int width, int stepX, int stepY)
    {
        long sad = 0;
        int total = 0;

        for (int r = 0; r < overlapRows; r += stepY)
        {
            int py = prevStartY + r;
            int ny = nextStartY + r;
            if (py >= prevH || ny >= nextH) continue;

            byte* prevRow = prevBase + (long)py * prevStride;
            byte* nextRow = nextBase + (long)ny * nextStride;
            for (int x = 0; x < width; x += stepX)
            {
                total++;
                int o = x * 4;
                int lumP = (299 * prevRow[o + 2] + 587 * prevRow[o + 1] + 114 * prevRow[o]) / 1000;
                int lumN = (299 * nextRow[o + 2] + 587 * nextRow[o + 1] + 114 * nextRow[o]) / 1000;
                int d = Math.Abs(lumP - lumN);
                sad += d <= SadMatchTolerance ? 0 : d - SadMatchTolerance;
            }
        }
        if (total == 0) return 0;
        double meanSad = (double)sad / total;
        return Math.Clamp(1.0 - meanSad / 64.0, 0, 1);
    }

    private static void ValidateTiles(IReadOnlyList<WriteableBitmap> tiles, int width)
    {
        for (int i = 1; i < tiles.Count; i++)
        {
            var t = tiles[i];
            if (t.PixelWidth != width)
                throw new ArgumentException(
                    $"Tile {i} has width {t.PixelWidth}, expected {width}.",
                    nameof(tiles));
            if (t.PixelHeight != tiles[i - 1].PixelHeight)
                throw new ArgumentException(
                    $"Tile {i} height {t.PixelHeight} differs from previous tile.",
                    nameof(tiles));
            if (t.PixelHeight <= 0)
                throw new ArgumentException($"Tile {i} has zero height.", nameof(tiles));
        }
    }
}
