// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using WeChatOcr;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Text extraction (OCR) through the <c>WeChatOcr</c> NuGet package, using the
/// OCR component it bundles (WeChatOCR.exe, mmmojo DLLs and model files).
/// </summary>
/// <remarks>
/// The package copies its files to <c>wco_data\</c> next to the plugin; nothing
/// has to be installed. <see cref="DataLocation"/> resolves <c>wco_data</c>
/// against a base directory that defaults to the process working directory (the
/// PowerToys install folder), so it is pointed at the plugin folder. In this
/// mode the package only reads from <c>wco_data</c>; the image handed to the OCR
/// process is written to the user's temp folder.
/// </remarks>
public static class WeChatOcrService
{
    private const int MaxAttempts = 3;

    /// <summary>Files of the bundled OCR component, relative to <c>wco_data</c>.</summary>
    private static readonly string[] RequiredFiles =
    {
        "WeChatOCR.exe",
        "mmmojo_64.dll",
        Path.Combine("Model", "OCRRecogFP32V1.1.0.26.xnet"),
    };

    /// <summary>
    /// WeChatOcr drops a task silently when the OCR process is not connected
    /// yet (e.g. while it starts), so each attempt gets a deadline and is retried.
    /// </summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(4);

    /// <summary>WeChatOcr keeps process-wide state; run one recognition at a time.</summary>
    private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

    /// <summary>Folder holding the bundled OCR component.</summary>
    public static string OcrDataDirectory => Path.Combine(PluginDirectory, "wco_data");

    /// <summary>
    /// Recognizes the text in a PNG image. Runs off the calling thread.
    /// </summary>
    /// <param name="png">PNG-encoded image.</param>
    /// <param name="cancellationToken">Cancels waiting for the result.</param>
    /// <returns>Recognized lines joined by new lines; empty when no text was found.</returns>
    /// <exception cref="OcrException">The OCR component is missing or unusable, or OCR failed.</exception>
    public static async Task<string> RecognizeAsync(byte[] png, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(png);

        // mmmojo_64.dll / WeChatOCR.exe are x64 binaries.
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new OcrException(Properties.Resources.ocr_err_arch);
        }

        // Everything below touches the disk or a child process: keep it off the UI thread.
        return await Task.Run(
            async () =>
            {
                var missing = FindMissingFiles(OcrDataDirectory).ToList();
                if (missing.Count > 0)
                {
                    throw new OcrException($"{Properties.Resources.ocr_err_component_missing} {Path.Combine(OcrDataDirectory, missing[0])}");
                }

                await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await RunOcrAsync(png, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Gate.Release();
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists the bundled OCR files missing from <paramref name="dataDirectory"/>.</summary>
    public static IEnumerable<string> FindMissingFiles(string dataDirectory)
    {
        return RequiredFiles.Where(f => !File.Exists(Path.Combine(dataDirectory, f)));
    }

    /// <summary>Joins the recognized lines of a WeChatOcr result.</summary>
    public static string JoinLines(WeChatOcrResult result)
    {
        var lines = result?.OcrResult?.SingleResult;
        if (lines == null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (!string.IsNullOrEmpty(line?.SingleStrUtf8))
            {
                sb.AppendLine(line.SingleStrUtf8);
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>User-facing "text extraction failed" message with the technical detail appended.</summary>
    public static string FailedMessage(string detail)
        => $"{Properties.Resources.ocr_err_failed} {detail}";

    private static async Task<string> RunOcrAsync(byte[] png, CancellationToken cancellationToken)
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"snip-ocr-{Guid.NewGuid():N}.png");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await File.WriteAllBytesAsync(imagePath, png, cancellationToken).ConfigureAwait(false);

            // Resolve wco_data (WeChatOCR.exe, mmmojo_64.dll, models) in the plugin folder.
            DataLocation.SetBaseDirectory(PluginDirectory);

            // No path: use the bundled component instead of an installed WeChat.
            using (var ocr = new ImageOcr())
            {
                for (int attempt = 0; attempt < MaxAttempts && !tcs.Task.IsCompleted; attempt++)
                {
                    ocr.Run(imagePath, (path, result) =>
                    {
                        // WeChatOcr may report progress with a null result; wait for the real one.
                        if (result != null)
                        {
                            tcs.TrySetResult(JoinLines(result));
                        }
                    });

                    await Task.WhenAny(tcs.Task, Task.Delay(AttemptTimeout, cancellationToken)).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            if (!tcs.Task.IsCompleted)
            {
                throw new OcrException(Properties.Resources.ocr_err_timeout);
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OcrException && ex is not OperationCanceledException)
        {
            Log.Exception($"Snip WeChat OCR failed: {ex.Message}", ex, typeof(WeChatOcrService));
            throw new OcrException(FailedMessage(ex.Message), ex);
        }
        finally
        {
            TryDelete(imagePath);
        }
    }

    private static string PluginDirectory
        => Path.GetDirectoryName(typeof(WeChatOcrService).Assembly.Location) ?? AppContext.BaseDirectory;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // Temp file; Windows cleans the temp folder eventually.
        }
    }
}
