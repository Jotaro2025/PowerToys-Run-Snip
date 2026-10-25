// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Core;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// Live-updated preview bitmap shown inside the selection while the window-capture
/// scroll path runs (#100/#103): clones the frozen source once, then blits each
/// captured tile into the selection area of the clone so the overlay shows the
/// scrolling content without punching a hole.
/// </summary>
internal sealed class ScrollLivePreview
{
    private WriteableBitmap _bitmap;

    /// <summary>Selection rect in preview-pixel coordinates (physical, overlay-relative).</summary>
    private Int32Rect _targetRect;

    /// <summary>
    /// Creates the preview clone from <paramref name="frozenSource"/>. Returns null
    /// when there is no source to clone (caller keeps whatever it shows today).
    /// </summary>
    public static ScrollLivePreview Create(BitmapSource frozenSource, Int32Rect targetRect)
    {
        if (frozenSource == null)
        {
            return null;
        }

        return new ScrollLivePreview
        {
            _bitmap = (WriteableBitmap)frozenSource.Clone(),
            _targetRect = targetRect,
        };
    }

    /// <summary>Current clone to bind as the overlay source; null after <see cref="Reset"/>.</summary>
    public WriteableBitmap Bitmap => _bitmap;

    /// <summary>
    /// Blits one captured tile into the selection area. Safe to call from the capture
    /// event on the UI thread; any failure only skips this preview update.
    /// </summary>
    public unsafe void BlitTile(ScrollTilePixels tile)
    {
        try
        {
            var preview = _bitmap;
            if (preview == null || tile == null)
            {
                return;
            }

            var srcArr = tile.DangerousGetArray();
            if (srcArr == null)
            {
                return;
            }

            int dstX = _targetRect.X;
            int dstY = _targetRect.Y;
            int copyW = Math.Min(tile.Width, preview.PixelWidth - dstX);
            int copyH = Math.Min(tile.Height, preview.PixelHeight - dstY);
            if (copyW <= 0 || copyH <= 0)
            {
                return;
            }

            int rowBytes = copyW * 4;
            int srcStride = tile.Stride;
            preview.Lock();
            try
            {
                byte* dstBase = (byte*)preview.BackBuffer;
                int dstStride = preview.BackBufferStride;
                for (int y = 0; y < copyH; y++)
                {
                    var srcRow = srcArr.AsSpan(y * srcStride, rowBytes);
                    var dstRow = new Span<byte>(dstBase + ((long)(dstY + y) * dstStride) + (dstX * 4), rowBytes);
                    srcRow.CopyTo(dstRow);
                }

                preview.AddDirtyRect(new Int32Rect(dstX, dstY, copyW, copyH));
            }
            finally
            {
                preview.Unlock();
            }
        }
        catch (Exception ex)
        {
            SnipVerbose.Scroll($"[scroll] live preview update skipped: {ex.Message}");
        }
    }

    /// <summary>Drops the clone so the overlay falls back to the frozen source.</summary>
    public void Reset()
    {
        _bitmap = null;
    }
}
