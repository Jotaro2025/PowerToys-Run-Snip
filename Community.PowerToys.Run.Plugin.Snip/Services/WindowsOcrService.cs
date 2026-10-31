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
/// are recognized in overlapping horizontal strips. Each strip is decoded from the source
/// region (not a full-height ScaledHeight transform) so &gt;10k px stitches do not OOM / fail.
/// Source height beyond <see cref="MaxStripPasses"/> × MaxImageDimension is truncated with a tip.
/// </remarks>
public static class WindowsOcrService
{
    /// <summary>Vertical overlap between strips so a line cut by a strip edge is whole in one strip.</summary>
    internal const int StripOverlap = 256;

    /// <summary>Max number of MaxImageDimension-tall passes before truncating very tall stitches.</summary>
    internal const int MaxStripPasses = 6;

    private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

    private static string _tallImageHint;

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

    /// <summary>
    /// Creates the WinRT recognizer once so the first recognition skips engine
    /// construction. Never throws (prewarm is best-effort).
    /// </summary>
    public static void Prewarm()
    {
        try
        {
            _ = CreateEngine();
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip Windows OCR prewarm failed: {ex.Message}", typeof(WindowsOcrService));
        }
    }

    /// <summary>Recognizes text lines (image pixel space, reading order) in a PNG.</summary>
    /// <exception cref="OcrException">No OCR language installed, or recognition failed.</exception>
    public static Task<IReadOnlyList<OcrTextLine>> RecognizeLinesAsync(
        byte[] png,
        string languagePreference = "auto",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        return Task.Run(
            async () =>
            {
                await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await RunAsync(png, languagePreference, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Hard cap on source rows processed for one recognition call.</summary>
    internal static int ResolveOcrSourceHeightCap(int maxImageDimension)
        => Math.Max(maxImageDimension, checked(maxImageDimension * MaxStripPasses));

    /// <summary>
    /// Soft tip when the PNG is taller than one MaxImageDimension (Windows tiles; WeChat may struggle).
    /// </summary>
    internal static string TryBuildTallPreferWindowsHint(int pixelHeight, int maxImageDimension = 0)
    {
        int max = maxImageDimension > 0 ? maxImageDimension : 10000;
        if (pixelHeight <= max)
        {
            return null;
        }

        return Properties.Resources.ocr_hint_tall_prefer_windows.Replace(
            "{0}",
            pixelHeight.ToString(System.Globalization.CultureInfo.CurrentCulture),
            StringComparison.Ordinal);
    }

    /// <summary>Consumes the last truncate tip from <see cref="RecognizeLinesAsync"/> (UI status).</summary>
    public static string ConsumeTallImageHint()
    {
        var tip = _tallImageHint;
        _tallImageHint = null;
        return tip;
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

    /// <summary>
    /// Installed OCR language tags (BCP-47), empty when WinRT OCR is unavailable.
    /// </summary>
    public static IReadOnlyList<string> GetInstalledLanguageTags()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages
                .Select(l => l.LanguageTag)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip Windows OCR language list failed: {ex.Message}", ex, typeof(WindowsOcrService));
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Picks which installed tag to use. <paramref name="preference"/> of <c>auto</c>/empty uses
    /// the first user-profile tag when present, else Simplified Chinese if installed, else the
    /// first available tag. A concrete tag must match an installed entry (prefix or exact).
    /// </summary>
    internal static string ResolveLanguageTag(
        string preference,
        IReadOnlyList<string> availableTags,
        string userProfileTag)
    {
        availableTags ??= Array.Empty<string>();
        if (availableTags.Count == 0)
        {
            return null;
        }

        bool auto = string.IsNullOrWhiteSpace(preference)
            || preference.Equals("auto", StringComparison.OrdinalIgnoreCase);
        if (!auto)
        {
            var match = availableTags.FirstOrDefault(t =>
                t.Equals(preference, StringComparison.OrdinalIgnoreCase)
                || t.StartsWith(preference + "-", StringComparison.OrdinalIgnoreCase)
                || preference.StartsWith(t + "-", StringComparison.OrdinalIgnoreCase)
                || (preference.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                    && t.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)
                    && (preference.Contains("Hans", StringComparison.OrdinalIgnoreCase)
                        || preference.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
                        || preference.Equals("zh", StringComparison.OrdinalIgnoreCase))));
            if (match != null)
            {
                return match;
            }
        }

        if (!string.IsNullOrWhiteSpace(userProfileTag)
            && availableTags.Any(t => t.Equals(userProfileTag, StringComparison.OrdinalIgnoreCase)))
        {
            return availableTags.First(t => t.Equals(userProfileTag, StringComparison.OrdinalIgnoreCase));
        }

        var zh = availableTags.FirstOrDefault(t =>
            t.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)
            || t.Equals("zh-CN", StringComparison.OrdinalIgnoreCase));
        return zh ?? availableTags[0];
    }

    internal static OcrEngine CreateEngine(string languagePreference = "auto")
    {
        var languages = OcrEngine.AvailableRecognizerLanguages;
        if (languages.Count == 0)
        {
            return null;
        }

        var tags = languages.Select(l => l.LanguageTag).ToList();
        string profileTag = null;
        try
        {
            var profileEngine = OcrEngine.TryCreateFromUserProfileLanguages();
            profileTag = profileEngine?.RecognizerLanguage?.LanguageTag;
        }
        catch (Exception)
        {
            // Fall through to ResolveLanguageTag defaults when profile engine fails.
        }

        var chosen = ResolveLanguageTag(languagePreference, tags, profileTag);
        if (chosen == null)
        {
            return null;
        }

        var lang = languages.FirstOrDefault(l =>
            l.LanguageTag.Equals(chosen, StringComparison.OrdinalIgnoreCase));
        return lang != null ? OcrEngine.TryCreateFromLanguage(lang) : OcrEngine.TryCreateFromUserProfileLanguages();
    }

    private static async Task<IReadOnlyList<OcrTextLine>> RunAsync(
        byte[] png,
        string languagePreference,
        CancellationToken cancellationToken)
    {
        var engine = CreateEngine(languagePreference) ?? throw new OcrException(Properties.Resources.ocr_err_windows_no_language);
        Log.Info($"Snip Windows OCR language: {engine.RecognizerLanguage?.LanguageTag}", typeof(WindowsOcrService));

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);

        int max = (int)OcrEngine.MaxImageDimension;
        int srcW = (int)decoder.PixelWidth;
        int srcH = (int)decoder.PixelHeight;
        _tallImageHint = null;

        // Width fit into MaxImageDimension; strip height in SOURCE rows so each scaled strip fits.
        double scaleW = srcW > max ? (double)max / srcW : 1.0;
        int maxSrcStripH = Math.Max(1, (int)Math.Floor(max / scaleW));
        int heightCap = ResolveOcrSourceHeightCap(max);
        int processH = Math.Min(srcH, heightCap);
        if (processH < srcH)
        {
            _tallImageHint = Properties.Resources.ocr_hint_tall_truncated
                .Replace("{0}", srcH.ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal)
                .Replace("{1}", processH.ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal);
            Log.Warn(
                $"Snip Windows OCR: truncating tall image {srcW}x{srcH} to height {processH} (cap {heightCap})",
                typeof(WindowsOcrService));
        }

        var result = new List<OcrTextLine>();
        foreach (var strip in PlanStrips(processH, maxSrcStripH, StripOverlap))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stripBmp = await DecodeSourceStripAsync(
                decoder,
                strip,
                (uint)srcW,
                scaleW,
                cancellationToken).ConfigureAwait(false);

            var ocr = await engine.RecognizeAsync(stripBmp).AsTask(cancellationToken).ConfigureAwait(false);
            foreach (var line in ocr.Lines)
            {
                var mapped = MapLine(line, strip, scaleW);
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

    /// <summary>Decode one source-pixel strip; scales via the decode transform when
    /// <paramref name="scaledWidth"/> is below the source width, avoiding a PNG
    /// encode→decode round-trip per strip.</summary>
    private static async Task<SoftwareBitmap> DecodeSourceStripAsync(
        BitmapDecoder decoder,
        OcrStrip strip,
        uint srcW,
        double scaleW,
        CancellationToken cancellationToken)
    {
        bool scale = scaleW < 0.999;
        var transform = new BitmapTransform
        {
            Bounds = new BitmapBounds
            {
                X = 0,
                Y = (uint)strip.Top,
                Width = srcW,
                Height = (uint)strip.Height,
            },
        };

        if (scale)
        {
            transform.ScaledWidth = (uint)Math.Max(1, (int)Math.Round(srcW * scaleW));
            transform.ScaledHeight = (uint)Math.Max(1, (int)Math.Round(strip.Height * scaleW));
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);
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

        // Line boxes are in the (possibly width-scaled) strip bitmap; strip.Top is source Y.
        double inv = scale > 0.0001 ? 1.0 / scale : 1.0;
        double centerInSource = strip.Top + (((top + bottom) / 2) * inv);
        if (centerInSource < strip.KeepTop || centerInSource >= strip.KeepBottom)
        {
            return null; // reported by the neighbouring strip
        }

        string text = JoinWords(words.Select(w => w.Text));
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var box = new WpfRect(
            left * inv,
            strip.Top + (top * inv),
            (right - left) * inv,
            (bottom - top) * inv);
        return new OcrTextLine(text, box, 1f);
    }

    /// <summary>One horizontal strip: rows [Top, Top+Height); keeps lines centered in [KeepTop, KeepBottom).</summary>
    internal readonly record struct OcrStrip(int Top, int Height, int KeepTop, int KeepBottom);
}
