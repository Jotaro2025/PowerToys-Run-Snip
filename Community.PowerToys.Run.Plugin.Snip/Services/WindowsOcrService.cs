// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using Wox.Plugin.Logger;

using WpfRect = System.Windows.Rect;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Text extraction through the built-in <c>Windows.Media.Ocr</c> engine. Runs natively on
/// x64 and ARM64 and needs no download, but requires an installed OCR language (Windows
/// language feature "Optical character recognition").
/// </summary>
/// <remarks>
/// Results use the same <see cref="OcrTextLine"/> model as WeChat OCR (image pixel space,
/// reading order), so text extraction and image translate work unchanged. Windows OCR gives
/// no per-line confidence or rotated quad: <see cref="OcrTextLine.Confidence"/> is 1 and
/// <see cref="OcrTextLine.Corners"/> is null (renderer uses the axis-aligned box).
/// Images taller / wider than <see cref="OcrEngine.MaxImageDimension"/> (long scroll captures)
/// are recognized in overlapping horizontal strips.
/// </remarks>
public static class WindowsOcrService
{
    /// <summary>Vertical overlap between strips so a line cut by a strip edge is whole in one strip.</summary>
    internal const int StripOverlap = 256;

    private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

    /// <summary>True when another Windows OCR recognition is running.</summary>
    public static bool IsBusy => Gate.CurrentCount == 0;

    /// <summary>True when at least one OCR recognizer language is installed.</summary>
    public static bool IsAvailable()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages.Count > 0;
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip Windows OCR availability check failed: {ex.Message}", ex, typeof(WindowsOcrService));
            return false;
        }
    }

    /// <summary>Recognizes text lines (image pixel space, reading order) in a PNG.</summary>
    /// <exception cref="OcrException">No OCR language installed, or recognition failed.</exception>
    public static Task<IReadOnlyList<OcrTextLine>> RecognizeLinesAsync(byte[] png, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        return Task.Run(
            async () =>
            {
                await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await RunAsync(png, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OcrException && ex is not OperationCanceledException)
                {
                    Log.Exception($"Snip Windows OCR failed: {ex.Message}", ex, typeof(WindowsOcrService));
                    throw new OcrException(WeChatOcrService.FailedMessage(ex.Message), ex);
                }
                finally
                {
                    Gate.Release();
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Plans horizontal strips covering <paramref name="height"/> rows, each at most
    /// <paramref name="maxHeight"/> tall and overlapping by <paramref name="overlap"/>.
    /// A line belongs to the strip whose [KeepTop, KeepBottom) contains its vertical center,
    /// so lines in an overlap are reported exactly once.
    /// </summary>
    internal static IReadOnlyList<OcrStrip> PlanStrips(int height, int maxHeight, int overlap)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);
        overlap = Math.Clamp(overlap, 0, maxHeight / 2);

        if (height <= maxHeight)
        {
            return [new OcrStrip(0, height, 0, height)];
        }

        int step = maxHeight - overlap;
        var starts = new List<int>();
        for (int y = 0; ; y += step)
        {
            if (y + maxHeight >= height)
            {
                starts.Add(Math.Max(0, height - maxHeight));
                break;
            }

            starts.Add(y);
        }

        var strips = new List<OcrStrip>(starts.Count);
        for (int i = 0; i < starts.Count; i++)
        {
            int top = starts[i];
            int bottom = Math.Min(height, top + maxHeight);
            int keepTop = i == 0 ? 0 : (top + Math.Min(height, starts[i - 1] + maxHeight)) / 2;
            int keepBottom = i == starts.Count - 1 ? height : (starts[i + 1] + bottom) / 2;
            strips.Add(new OcrStrip(top, bottom - top, keepTop, keepBottom));
        }

        return strips;
    }

    /// <summary>
    /// Joins Windows OCR words. CJK scripts (Han, kana) are recognized one character per word,
    /// so no space is inserted next to them; other scripts are space-separated.
    /// </summary>
    internal static string JoinWords(IEnumerable<string> words)
    {
        var sb = new StringBuilder();
        foreach (var word in words)
        {
            if (string.IsNullOrEmpty(word))
            {
                continue;
            }

            if (sb.Length > 0 && !IsNoSpaceScript(sb[sb.Length - 1]) && !IsNoSpaceScript(word[0]))
            {
                sb.Append(' ');
            }

            sb.Append(word);
        }

        return sb.ToString();
    }

    internal static bool IsNoSpaceScript(char c)
        => (c >= '\u3000' && c <= '\u30FF') // CJK punctuation, Hiragana, Katakana
            || (c >= '\u3400' && c <= '\u4DBF') // CJK Ext A
            || (c >= '\u4E00' && c <= '\u9FFF') // CJK Unified
            || (c >= '\uF900' && c <= '\uFAFF') // CJK Compatibility
            || (c >= '\uFF00' && c <= '\uFFEF'); // Full-width forms

    private static OcrEngine CreateEngine()
    {
        var languages = OcrEngine.AvailableRecognizerLanguages;
        if (languages.Count == 0)
        {
            return null;
        }

        // Respect the user's language order first; then prefer Simplified Chinese; then anything.
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine != null)
        {
            return engine;
        }

        var zh = languages.FirstOrDefault(l =>
            l.LanguageTag.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)
            || l.LanguageTag.Equals("zh-CN", StringComparison.OrdinalIgnoreCase));
        return OcrEngine.TryCreateFromLanguage(zh ?? languages[0]);
    }

    private static async Task<IReadOnlyList<OcrTextLine>> RunAsync(byte[] png, CancellationToken cancellationToken)
    {
        var engine = CreateEngine() ?? throw new OcrException(Properties.Resources.ocr_err_windows_no_language);
        Log.Info($"Snip Windows OCR language: {engine.RecognizerLanguage?.LanguageTag}", typeof(WindowsOcrService));

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);

        int max = (int)OcrEngine.MaxImageDimension;
        uint srcW = decoder.PixelWidth;
        uint srcH = decoder.PixelHeight;

        // Too wide: scale the whole image down to the max width; then strip the (scaled) height.
        double scale = srcW > max ? (double)max / srcW : 1.0;
        uint scaledW = (uint)Math.Max(1, Math.Round(srcW * scale));
        uint scaledH = (uint)Math.Max(1, Math.Round(srcH * scale));

        var result = new List<OcrTextLine>();
        foreach (var strip in PlanStrips((int)scaledH, max, StripOverlap))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transform = new BitmapTransform
            {
                ScaledWidth = scaledW,
                ScaledHeight = scaledH,
                InterpolationMode = BitmapInterpolationMode.Fant,
                Bounds = new BitmapBounds { X = 0, Y = (uint)strip.Top, Width = scaledW, Height = (uint)strip.Height },
            };

            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);

            var ocr = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
            foreach (var line in ocr.Lines)
            {
                var mapped = MapLine(line, strip, scale);
                if (mapped != null)
                {
                    result.Add(mapped);
                }
            }
        }

        return result
            .OrderBy(l => l.Box.Top)
            .ThenBy(l => l.Box.Left)
            .ToList();
    }

    private static OcrTextLine MapLine(OcrLine line, OcrStrip strip, double scale)
    {
        var words = line.Words;
        if (words == null || words.Count == 0)
        {
            return null;
        }

        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var w in words)
        {
            var r = w.BoundingRect;
            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.X + r.Width);
            bottom = Math.Max(bottom, r.Y + r.Height);
        }

        if (right <= left || bottom <= top)
        {
            return null;
        }

        double centerInScaled = strip.Top + ((top + bottom) / 2);
        if (centerInScaled < strip.KeepTop || centerInScaled >= strip.KeepBottom)
        {
            return null; // reported by the neighbouring strip
        }

        string text = JoinWords(words.Select(w => w.Text));
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        double inv = 1.0 / scale;
        var box = new WpfRect(
            left * inv,
            (strip.Top + top) * inv,
            (right - left) * inv,
            (bottom - top) * inv);
        return new OcrTextLine(text, box, 1f);
    }

    /// <summary>One horizontal strip: rows [Top, Top+Height); keeps lines centered in [KeepTop, KeepBottom).</summary>
    internal readonly record struct OcrStrip(int Top, int Height, int KeepTop, int KeepBottom);
}
