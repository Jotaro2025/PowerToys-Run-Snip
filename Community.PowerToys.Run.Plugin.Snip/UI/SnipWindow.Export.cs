// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

public sealed partial class SnipWindow
{
    private async Task DoSaveAsync()
    {
        if (_disposed || !_hasSelection || _exportBusy)
        {
            return;
        }

        DismissSessionChild(restoreOverlay: false);
        try
        {
            var built = await BuildAndEncodeAsync().ConfigureAwait(true);
            if (built == null)
            {
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Properties.Resources.save_dialog_title,
                DefaultExt = ".png",
                FileName = ScreenshotFileSaver.BuildFileName(),
                Filter = Properties.Resources.save_dialog_filter,
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var path = dialog.FileName;
            var format = PngEncoder.FormatFromPath(path);
            if (format == ImageFileFormat.Png)
            {
                await File.WriteAllBytesAsync(path, built.Value.Png).ConfigureAwait(true);
            }
            else
            {
                await PngEncoder.EncodeToFileAsync(built.Value.Bitmap, path, format).ConfigureAwait(true);
            }

            Result = new EditResult
            {
                Outcome = EditOutcome.Saved,
                Selection = _selection,
                Annotations = _annoCanvas.Items.ToArray(),
                Result = built.Value.Bitmap,
                PngBytes = built.Value.Png,
                SavedPath = path,
            };
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow DoSaveAsync failed: {ex.Message}", ex, GetType());
            Result ??= new EditResult { Outcome = EditOutcome.Cancelled };
        }

        Close();
    }

    /// <summary>
    /// Composites the selection on the UI thread (RenderTargetBitmap only when
    /// annotations exist; otherwise a CroppedBitmap), then PNG-encodes on the
    /// thread pool. Full-res source is always used — OCR / translate / save must
    /// not see the overlay preview downsample.
    /// </summary>
    private async Task<(BitmapSource Bitmap, byte[] Png)?> BuildAndEncodeAsync(bool includePng = true)
    {
        if (_disposed || !_hasSelection || _exportBusy)
        {
            return null;
        }

        _exportBusy = true;
        Cursor = Cursors.Wait;
        try
        {
            if (_scrollController is { HasTiles: true } or { IsActive: true })
            {
                var stitched = await StitchScrollTilesForExportAsync().ConfigureAwait(true);
                if (stitched == null)
                {
                    return null;
                }
            }

            var bmp = BuildResultBitmap(_selection);

            // PaddleOCR reads the bitmap directly; only the WeChat engine needs the
            // encoded PNG (it writes a temp file). Skipping the encode here saves
            // ~50–200 ms and one full-frame copy per capture.
            var png = includePng
                ? await PngEncoder.EncodeAsync(bmp).ConfigureAwait(true)
                : null;
            return _disposed ? null : (bmp, png);
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow BuildAndEncodeAsync failed: {ex.Message}", ex, GetType());
            Result ??= new EditResult { Outcome = EditOutcome.Cancelled };
            Close();
            return null;
        }
        finally
        {
            _exportBusy = false;
            if (!_disposed)
            {
                Cursor = Cursors.Cross;
            }
        }
    }
}
