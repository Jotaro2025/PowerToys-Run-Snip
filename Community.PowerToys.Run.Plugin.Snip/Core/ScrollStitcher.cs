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
    /// Cached pairwise vertical alignment result between two consecutive tiles.
    /// Computed during capture so final stitching can reuse it without re-running FFT/SAD scans.
    /// </summary>
    internal readonly record struct TileAlignment(
        int TopSkip,
        string Mode,
        TileRelation Relation,
        int StickyBottomRows = 0)
    {
        public int ContributedRows(int tileHeight) => Relation switch
        {
            TileRelation.FullDuplicate => 0,
            TileRelation.NoOverlap => tileHeight,
            _ => Math.Max(0, tileHeight - TopSkip),
        };
    }

    /// <summary>
    /// Aligns <paramref name="prev"/> and <paramref name="next"/> and returns a reusable <see cref="TileAlignment"/>.
    /// </summary>
    internal static TileAlignment AlignTiles(ScrollTilePixels prev, ScrollTilePixels next, int scrollHintPx = 0)
    {
        ArgumentNullException.ThrowIfNull(prev);
        ArgumentNullException.ThrowIfNull(next);
        return AlignVerticalTilesPixelsWithFooter(prev, next, scrollHintPx);
    }

    /// <summary>
    /// Rows <paramref name="next"/> would add below <paramref name="prev"/> (0 for full duplicate).
    /// Thread-pool safe; used during capture to track the projected stitched height.
    /// </summary>
    internal static int EstimateContributedRows(ScrollTilePixels prev, ScrollTilePixels next, int scrollHintPx = 0)
    {
        return AlignTiles(prev, next, scrollHintPx).ContributedRows(next.Height);
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
        => StitchPixelsCore(tiles, 0, null, null);

    public static PixelStitchResult Stitch(
        IReadOnlyList<ScrollTilePixels> tiles,
        int estimatedScrollPxPerTile,
        IReadOnlyList<int> perTileScrollPx = null)
        => StitchPixelsCore(tiles, estimatedScrollPxPerTile, perTileScrollPx, null);

    internal static PixelStitchResult Stitch(
        IReadOnlyList<ScrollTilePixels> tiles,
        IReadOnlyList<TileAlignment> precomputedAlignments)
        => StitchPixelsCore(tiles, 0, null, precomputedAlignments);

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

            using var pixels = StitchPixelsCore(owned, 0, null, null);
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

            using var pixels = StitchPixelsCore(owned, estimatedScrollPxPerTile, perTileScrollPx, null);
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
        IReadOnlyList<int> perTileScrollPx,
        IReadOnlyList<TileAlignment> precomputedAlignments)
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
        int lastContributingIdx = 0;

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

            TileAlignment alignment = (precomputedAlignments != null && i - 1 < precomputedAlignments.Count)
                ? precomputedAlignments[i - 1]
                : AlignVerticalTilesPixelsWithFooter(prev, cur, scrollHintPx);

            int topSkip = alignment.TopSkip;
            string mode = alignment.Mode;
            TileRelation relation = alignment.Relation;
            int stickyFooterRows = alignment.StickyBottomRows;

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

            int rawShift = relation == TileRelation.NoOverlap ? curHeight : curHeight - topSkip;
            int effectiveTopSkip = relation == TileRelation.NoOverlap ? 0 : topSkip;

            // Trim static sticky-footer rows from the bottom of the previous contributing tile
            // so the footer only appears once at the bottom of the final stitched image.
            if (relation == TileRelation.PartialOverlap && stickyFooterRows > 0 && effectiveTopSkip >= stickyFooterRows)
            {
                var prevOff = offsets[lastContributingIdx];
                if (prevOff.RowsContributed > stickyFooterRows)
                {
                    offsets[lastContributingIdx] = new StitchOffset
                    {
                        TileIndex = prevOff.TileIndex,
                        TopSkip = prevOff.TopSkip,
                        RowsContributed = prevOff.RowsContributed - stickyFooterRows,
                        DestY = prevOff.DestY,
                        Mode = prevOff.Mode,
                    };
                    totalHeight -= stickyFooterRows;
                    effectiveTopSkip -= stickyFooterRows;
                }
            }

            int contributed = relation == TileRelation.NoOverlap ? curHeight : curHeight - effectiveTopSkip;
            if (totalHeight + contributed > maxHeight)
            {
                contributed = Math.Max(0, maxHeight - totalHeight);
                truncated = true;
                SnipVerbose.Scroll($"[stitch] tile {i}: height cap {maxHeight}px reached — truncating");
            }

            offsets[i] = new StitchOffset
            {
                TileIndex = i,
                TopSkip = effectiveTopSkip,
                RowsContributed = contributed,
                DestY = totalHeight,
                Mode = mode,
            };
            if (contributed > 0)
            {
                lastContributingIdx = i;
            }

            totalHeight += contributed;
            if (relation == TileRelation.PartialOverlap && rawShift >= MinHintContribPx)
            {
                recentDh.Add(rawShift);
                if (recentDh.Count > 6) recentDh.RemoveAt(0);
                scrollHintPx = Median(recentDh);
            }
            SnipVerbose.Scroll($"[stitch] tile {i}: {relation} skip={offsets[i].TopSkip} contrib={contributed} footer={stickyFooterRows} mode={mode} totalH={totalHeight} hint={scrollHintPx}");
        }

        return ComposeStitchedPixels(tiles, offsets, width, totalHeight, truncated);
    }

    private static unsafe PixelStitchResult ComposeStitchedPixels(
        IReadOnlyList<ScrollTilePixels> tiles, StitchOffset[] offsets, int width, int totalHeight, bool truncated)
    {
        int dstStride = CaptureBuffers.BgraStride(width);
        int byteCount = CaptureBuffers.BgraByteCount(width, totalHeight);
        var dest = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount);
        int pixelRowBytes = width * 4;

        // Contributing tiles write contiguous rows from y=0 (DestY advances
        // monotonically), so no pre-clear is needed. Same stride on both sides
        // lets each tile copy as one block instead of per-row spans.
        fixed (byte* dstBase = dest)
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                var tile = tiles[i];
                var off = offsets[i];
                if (off.RowsContributed <= 0) continue;
                var src = tile.DangerousGetArray();
                int srcStride = tile.Stride;
                int srcY = off.TopSkip;
                if (srcStride == dstStride)
                {
                    fixed (byte* srcBase = src)
                    {
                        Buffer.MemoryCopy(
                            srcBase + ((long)srcY * srcStride),
                            dstBase + ((long)off.DestY * dstStride),
                            (long)off.RowsContributed * srcStride,
                            (long)off.RowsContributed * srcStride);
                    }
                }
                else
                {
                    for (int row = 0; row < off.RowsContributed; row++)
                    {
                        int srcOffset = (srcY + row) * srcStride;
                        int dstOffset = (off.DestY + row) * dstStride;
                        src.AsSpan(srcOffset, pixelRowBytes).CopyTo(dest.AsSpan(dstOffset, pixelRowBytes));
                    }
                }
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
        var res = AlignVerticalTilesPixelsWithFooter(prev, next, scrollHintPx);
        return (res.TopSkip, res.Mode, res.Relation);
    }

    private static TileAlignment AlignVerticalTilesPixelsWithFooter(
        ScrollTilePixels prev, ScrollTilePixels next, int scrollHintPx = 0)
    {
        int width = prev.Width;
        int prevH = prev.Height;
        int nextH = next.Height;
        if (prevH <= 0 || nextH <= 0 || width <= 0)
            return new TileAlignment(0, "no-overlap", TileRelation.NoOverlap, 0);

        unsafe
        {
            fixed (byte* prevBase = prev.DangerousGetArray())
            fixed (byte* nextBase = next.DangerousGetArray())
            {
                return AlignVerticalTilesWithFooterFromPointers(
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
        var res = AlignVerticalTilesWithFooterFromPointers(
            prevBase, prevStride, prevH,
            nextBase, nextStride, nextH,
            width, scrollHintPx);
        return (res.TopSkip, res.Mode, res.Relation);
    }

    internal static unsafe TileAlignment AlignVerticalTilesWithFooterFromPointers(
        byte* prevBase, int prevStride, int prevH,
        byte* nextBase, int nextStride, int nextH,
        int width, int scrollHintPx = 0)
    {
        if (prevH <= 0 || nextH <= 0 || width <= 0)
            return new TileAlignment(0, "no-overlap", TileRelation.NoOverlap, 0);

        int h = Math.Min(prevH, nextH);
        int maxDh = h - MinPartialOverlapPx;
        if (maxDh < MinPartialOverlapPx)
            return new TileAlignment(0, "no-overlap", TileRelation.NoOverlap, 0);

        // FullDuplicate：两 tile 同行逐像素几乎相同（Raw≥99.9%），说明滚动未生效或重复截图。
        double rawIdentical = ComputeRawMatchRatio(
            prevBase, prevStride, prevH, 0,
            nextBase, nextStride, nextH, 0,
            h, width, stepX: 1, stepY: 1);
        if (rawIdentical >= 0.999)
            return new TileAlignment(nextH, "raw-dup", TileRelation.FullDuplicate, 0);

        // Exclude static left/right sidebars or scrollbars so stationary chrome does not bias dh=0 / false matches.
        DetectScrollableColumnBounds(
            prevBase, prevStride, nextBase, nextStride, h, width,
            out int activeX0, out int activeWidth);
        byte* activePrevBase = prevBase + (activeX0 * 4);
        byte* activeNextBase = nextBase + (activeX0 * 4);

        // If the scrollable interior is itself identical while only a tiny dynamic widget changed, treat as duplicate.
        if (activeWidth < width)
        {
            double activeIdentical = ComputeRawMatchRatio(
                activePrevBase, prevStride, prevH, 0,
                activeNextBase, nextStride, nextH, 0,
                h, activeWidth, stepX: 1, stepY: 1);
            if (activeIdentical >= 0.999)
                return new TileAlignment(nextH, "raw-dup", TileRelation.FullDuplicate, 0);
        }

        // Detect static bottom sticky-footer rows so they neither skew overlap scoring nor repeat across mid tiles.
        int stickyFooterRows = DetectStickyBottomRows(
            activePrevBase, prevStride, activeNextBase, nextStride, h, activeWidth);
        int usableH = h - stickyFooterRows;
        int usableMaxDh = usableH - MinPartialOverlapPx;
        if (usableMaxDh < MinPartialOverlapPx)
        {
            stickyFooterRows = 0;
            usableH = h;
            usableMaxDh = maxDh;
        }

        int effectiveHint = scrollHintPx;
        List<int> candidates;
        string modeTag;

        if (scrollHintPx > 0)
        {
            // 有 hint 时跳过 FFT——hint 窗口 + SAD 已足够，FFT 是 tile#1 无 hint 时的冷启动路径。
            modeTag = "hint";
            candidates = BuildHintWindowCandidates(usableMaxDh, scrollHintPx);
        }
        else
        {
            var phase = ScrollPhaseCorrelator.EstimateVerticalShift(
                activePrevBase, prevStride, usableH, activeNextBase, nextStride, usableH,
                activeWidth, MinPartialOverlapPx, usableMaxDh, scrollHintPx: 0);
            effectiveHint = phase.Success ? phase.Dh : 0;
            modeTag = phase.Success ? "fft" : "fft-scan";
            candidates = BuildAllDhCandidates(usableMaxDh);
        }

        var finals = ScoreAndClassify(
            activePrevBase, prevStride, usableH, activeNextBase, nextStride, usableH,
            activeWidth, usableH, usableMaxDh, effectiveHint, candidates, modeTag);

        if (finals.relation == TileRelation.NoOverlap && scrollHintPx > 0)
        {
            SnipVerbose.Scroll(
                $"[stitch] hint-fallback: dh={scrollHintPx} (avoid full-tile stack after weak match)");
            return new TileAlignment(h - scrollHintPx, "hint-fallback", TileRelation.PartialOverlap, stickyFooterRows);
        }

        if (finals.relation == TileRelation.PartialOverlap)
        {
            int bestDh = usableH - finals.topSkip;
            return new TileAlignment(h - bestDh, finals.mode, TileRelation.PartialOverlap, stickyFooterRows);
        }

        return new TileAlignment(finals.topSkip, finals.mode, finals.relation, 0);
    }

    /// <summary>
    /// Detects stationary left/right columns (e.g. fixed navigation sidebars or scrollbars)
    /// that remain unchanged at dh=0 while the central viewport scrolls.
    /// </summary>
    internal static unsafe void DetectScrollableColumnBounds(
        byte* prevBase, int prevStride,
        byte* nextBase, int nextStride,
        int h, int width,
        out int activeX0, out int activeWidth)
    {
        activeX0 = 0;
        activeWidth = width;
        if (width < 24 || h < 8)
        {
            return;
        }

        int maxSideTrim = width * 2 / 5;
        int stepY = Math.Max(1, h / 32);

        int leftStatic = 0;
        for (int x = 0; x < maxSideTrim; x++)
        {
            if (IsColumnStatic(prevBase, prevStride, nextBase, nextStride, h, x, stepY))
            {
                leftStatic = x + 1;
            }
            else
            {
                break;
            }
        }

        int rightStatic = 0;
        for (int i = 0; i < maxSideTrim; i++)
        {
            int x = width - 1 - i;
            if (x <= leftStatic)
            {
                break;
            }

            if (IsColumnStatic(prevBase, prevStride, nextBase, nextStride, h, x, stepY))
            {
                rightStatic = i + 1;
            }
            else
            {
                break;
            }
        }

        int candidateWidth = width - leftStatic - rightStatic;
        if (candidateWidth >= Math.Max(12, width / 4) && (leftStatic > 0 || rightStatic > 0))
        {
            activeX0 = leftStatic;
            activeWidth = candidateWidth;
        }
    }

    private static unsafe bool IsColumnStatic(
        byte* prevBase, int prevStride,
        byte* nextBase, int nextStride,
        int h, int x, int stepY)
    {
        int matches = 0;
        int total = 0;
        int byteOffset = x * 4;
        for (int y = 0; y < h; y += stepY)
        {
            byte* p = prevBase + ((long)y * prevStride) + byteOffset;
            byte* n = nextBase + ((long)y * nextStride) + byteOffset;
            total++;
            if (p[0] == n[0] && p[1] == n[1] && p[2] == n[2])
            {
                matches++;
            }
        }

        return total > 0 && (double)matches / total >= 0.96;
    }

    /// <summary>
    /// Detects stationary bottom rows (e.g. sticky footer / status bar) that stay identical at dh=0
    /// while the viewport above them scrolls.
    /// </summary>
    internal static unsafe int DetectStickyBottomRows(
        byte* prevBase, int prevStride,
        byte* nextBase, int nextStride,
        int h, int width)
    {
        const int minStickyRows = 4;
        if (h < 24 || width < 8)
        {
            return 0;
        }

        int maxFooter = Math.Min(h / 4, 160);
        int footerRows = 0;
        for (int r = 1; r <= maxFooter; r++)
        {
            int y = h - r;
            double rowMatch = ComputeRawMatchRatio(
                prevBase, prevStride, h, y,
                nextBase, nextStride, h, y,
                1, width, stepX: 1, stepY: 1);
            if (rowMatch >= 0.985)
            {
                footerRows = r;
            }
            else
            {
                break;
            }
        }

        if (footerRows < minStickyRows)
        {
            return 0;
        }

        int bodyH = h - footerRows;
        double bodyMatchAtZero = ComputeRawMatchRatio(
            prevBase, prevStride, h, 0,
            nextBase, nextStride, h, 0,
            bodyH, width, stepX: 2, stepY: 2);
        if (bodyMatchAtZero >= 0.85)
        {
            return 0;
        }

        // Guard against uniform background margins at the bottom: a true sticky footer differs
        // from the content band immediately above it in the same frame.
        int aboveStart = Math.Max(0, bodyH - footerRows);
        double selfSimilarityAbove = ComputeRawMatchRatio(
            prevBase, prevStride, h, aboveStart,
            prevBase, prevStride, h, bodyH,
            footerRows, width, stepX: 1, stepY: 1);
        if (selfSimilarityAbove >= 0.95)
        {
            return 0;
        }

        return footerRows;
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
        // Materialize luma planes once per tile pair: every SAD/TextEdge metric
        // for every candidate then reads one byte per pixel instead of
        // recomputing the 3-channel luminance (6+ multiplies) on every pass.
        byte[] lumaPrev = BuildLumaPlane(prevBase, prevStride, prevH, width);
        byte[] lumaNext = BuildLumaPlane(nextBase, nextStride, nextH, width);

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
                Sad = ComputeSadMatchRatioLuma(
                    lumaPrev, lumaNext, prevH, dh, nextH, 0,
                    overlapRows, width, stepX, stepY),
                EdgeRaw = ComputeRawMatchRatio(
                    prevBase, prevStride, prevH, dh + edgeStart,
                    nextBase, nextStride, nextH, edgeStart,
                    band, width, stepX, Math.Max(1, stepY)),
                EdgeSad = ComputeSadMatchRatioLuma(
                    lumaPrev, lumaNext, prevH, dh + edgeStart, nextH, edgeStart,
                    band, width, stepX, Math.Max(1, stepY)),
                TextEdge = ComputeTextEdgeMatchRatioLuma(
                    lumaPrev, lumaNext, prevH, dh, nextH, 0,
                    overlapRows, width, stepX, stepY),
                EdgeText = ComputeTextEdgeMatchRatioLuma(
                    lumaPrev, lumaNext, prevH, dh + edgeStart, nextH, edgeStart,
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

    /// <summary>
    /// One luma byte per pixel, row-major with a compact width stride (no stride
    /// gaps). Same 299/587/114 weights as the old RowLum, exact integer match.
    /// </summary>
    private static unsafe byte[] BuildLumaPlane(byte* basePtr, int stride, int height, int width)
    {
        var plane = new byte[width * height];
        fixed (byte* dst = plane)
        {
            for (int y = 0; y < height; y++)
            {
                byte* row = basePtr + ((long)y * stride);
                byte* dRow = dst + ((long)y * width);
                for (int x = 0; x < width; x++)
                {
                    int o = x * 4;
                    dRow[x] = (byte)((299 * row[o + 2] + 587 * row[o + 1] + 114 * row[o]) / 1000);
                }
            }
        }

        return plane;
    }

    private static double ComputeSadMatchRatioLuma(
        byte[] lumaPrev, byte[] lumaNext,
        int prevH, int prevStartY, int nextH, int nextStartY,
        int overlapRows, int width, int stepX, int stepY)
    {
        long sad = 0;
        int total = 0;

        for (int r = 0; r < overlapRows; r += stepY)
        {
            int py = prevStartY + r;
            int ny = nextStartY + r;
            if (py >= prevH || ny >= nextH) continue;

            int prevOff = py * width;
            int nextOff = ny * width;
            for (int x = 0; x < width; x += stepX)
            {
                total++;
                int d = Math.Abs(lumaPrev[prevOff + x] - lumaNext[nextOff + x]);
                sad += d <= SadMatchTolerance ? 0 : d - SadMatchTolerance;
            }
        }
        if (total == 0) return 0;
        double meanSad = (double)sad / total;
        return Math.Clamp(1.0 - meanSad / 64.0, 0, 1);
    }

    private static double ComputeTextEdgeMatchRatioLuma(
        byte[] lumaPrev, byte[] lumaNext,
        int prevH, int prevStartY, int nextH, int nextStartY,
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

            int prevOff = py * width;
            int nextOff = ny * width;
            for (int x = 0; x < width - 1; x += stepX)
            {
                int edgeP = lumaPrev[prevOff + x + 1] - lumaPrev[prevOff + x];
                int edgeN = lumaNext[nextOff + x + 1] - lumaNext[nextOff + x];
                total++;
                if (Math.Abs(edgeP - edgeN) <= TextEdgeTolerance)
                    matches++;
            }
        }
        return total > 0 ? (double)matches / total : 0;
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
