// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Region GIF recording session: captures the frozen selection area on a timer
/// (GDI BitBlt of the live screen through the overlay hole), collects frames up
/// to a memory budget, and encodes via <see cref="GifEncoder"/> on stop.
/// Esc / the toolbar toggle ends the session; the result is saved next to the
/// user's save directory with a timestamped name.
/// </summary>
internal sealed class GifRecordingSession : IDisposable
{
    /// <summary>Capture interval; ~12.5 fps keeps files small and smooth enough.</summary>
    private const int IntervalMs = 80;

    /// <summary>Hard frame cap so a forgotten session cannot exhaust memory.</summary>
    private const int MaxFrames = 300;

    /// <summary>Target start fps via frame delay (GIF centiseconds resolve to 10 fps at 100 ms).</summary>
    public static TimeSpan Interval => TimeSpan.FromMilliseconds(IntervalMs);

    private readonly Int32Rect _physicalRect;
    private readonly ICaptureService _capture;
    private readonly List<byte[]> _frames = new();
    private readonly List<int> _delays = new();
    private readonly CancellationTokenSource _cts = new();

    // Cached token: safe to read after the CTS is disposed (unlike _cts.Token,
    // which throws ObjectDisposedException once Dispose has run mid-loop).
    private readonly CancellationToken _token;
    private Task _loop;
    private long _budgetBytes;

    public GifRecordingSession(Int32Rect physicalRect, ICaptureService capture)
    {
        _physicalRect = physicalRect;
        _capture = capture;
        _token = _cts.Token;

        // ~24 MB budget: roughly 40 seconds of 400×300 at ~80 ms, enough for a
        // demo clip without ballooning PowerLauncher memory.
        _budgetBytes = 24 * 1024 * 1024;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    public int FrameCount => _frames.Count;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _loop = Task.Run(
            async () =>
            {
                while (!_token.IsCancellationRequested && _frames.Count < MaxFrames && _budgetBytes > 0)
                {
                    try
                    {
                        using var frame = _capture.CaptureScreenRectPixels(
                            _physicalRect.X, _physicalRect.Y, _physicalRect.Width, _physicalRect.Height);
                        if (frame == null)
                        {
                            await Task.Delay(IntervalMs, _token).ConfigureAwait(false);
                            continue;
                        }

                        // ToOwnedPixels transfers a contiguous BGRA copy (pooled when
                        // the frame is pool-backed); we hand ownership to the frame list.
                        using var tile = frame.ToOwnedPixels();
                        var arr = tile.DangerousGetArray();
                        var copy = new byte[tile.Stride * tile.Height];
                        System.Array.Copy(arr, copy, Math.Min(arr.Length, copy.Length));
                        _budgetBytes -= copy.Length;
                        _frames.Add(copy);
                        _delays.Add(IntervalMs);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Snip GIF frame capture failed: {ex.Message}", typeof(GifRecordingSession));
                        if (_token.IsCancellationRequested)
                        {
                            break;
                        }
                    }

                    if (_token.IsCancellationRequested)
                    {
                        break;
                    }

                    await Task.Delay(IntervalMs, _token).ConfigureAwait(false);
                }
            },
            _token);
    }

    /// <summary>
    /// Stops capturing and encodes the collected frames. Returns the GIF bytes,
    /// or null when too few frames were captured for a meaningful clip.
    /// </summary>
    public async Task<byte[]> StopAndEncodeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_loop != null)
            {
                await _loop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }

        if (_frames.Count < 2)
        {
            return null;
        }

        int w = _physicalRect.Width;
        int h = _physicalRect.Height;
        return await Task.Run(() => GifEncoder.Encode(_frames, _delays, w, h, w * 4)).ConfigureAwait(false);
    }

    public static string BuildFileName()
        => $"Snip-GIF-{DateTime.Now:yyyyMMdd-HHmmss}.gif";

    /// <summary>Releases the capture loop: cancels it first so the pending
    /// <c>Task.Delay</c> unwinds instead of throwing
    /// <see cref="ObjectDisposedException"/> forever, then disposes the CTS.
    /// Frames are discarded; safe to call multiple times.</summary>
    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        _cts.Dispose();
    }
}
