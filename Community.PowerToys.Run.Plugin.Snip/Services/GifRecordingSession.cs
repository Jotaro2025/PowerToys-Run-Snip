// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>Bounded GIF capture with measured frame delays and a separate pending-save state.</summary>
internal sealed class GifRecordingSession : IDisposable
{
    private const int IntervalMs = 80;
    private const int MaxFrames = 300;
    private const long MinBudgetBytes = 24L * 1024 * 1024;
    private const long MaxBudgetBytes = 256L * 1024 * 1024;
    private const int TargetFrames = 80;
    private const int MaxOutputDimension = 960;

    private readonly Int32Rect _physicalRect;
    private readonly ICaptureService _capture;
    private readonly List<byte[]> _frames = new();
    private readonly List<int> _delays = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly CancellationToken _lifetimeToken;
    private readonly object _sync = new();
    private readonly int _width;
    private readonly int _height;
    private readonly int _maxFrames;
    private Task _loop = Task.CompletedTask;
    private Task _encoding = Task.CompletedTask;
    private bool _started;
    private bool _disposed;
    private bool _encodingStarted;
    private long _retainedBytes;
    private int _frameCount;

    public GifRecordingSession(Int32Rect physicalRect, ICaptureService capture, int maxFrames = MaxFrames)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(physicalRect.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(physicalRect.Height);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrames, 2);
        _physicalRect = physicalRect;
        _capture = capture;
        _maxFrames = maxFrames;
        _token = _cts.Token;
        _lifetimeToken = _lifetime.Token;
        double scale = Math.Min(1.0, MaxOutputDimension / (double)Math.Max(physicalRect.Width, physicalRect.Height));
        _width = Math.Max(1, (int)Math.Round(physicalRect.Width * scale));
        _height = Math.Max(1, (int)Math.Round(physicalRect.Height * scale));
        long sourceBytes = checked((long)physicalRect.Width * physicalRect.Height * 4);
        BudgetBytes = Math.Clamp(sourceBytes * TargetFrames, MinBudgetBytes, MaxBudgetBytes);
    }

    public static TimeSpan Interval => TimeSpan.FromMilliseconds(IntervalMs);

    public bool IsRunning => _started && !_token.IsCancellationRequested && !_loop.IsCompleted;

    public bool HasPendingFrames => _started && !_encodingStarted && !_disposed;

    public int FrameCount => Volatile.Read(ref _frameCount);

    public Task Completion => _loop;

    internal long BudgetBytes { get; }

    internal int OutputWidth => _width;

    internal int OutputHeight => _height;

    internal IReadOnlyList<int> FrameDelays => _delays;

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            _loop = Task.Run(CaptureLoopAsync);
        }
    }

    private async Task CaptureLoopAsync()
    {
        var clock = Stopwatch.StartNew();
        long previousFrameMs = 0;
        int frameBytes = checked(_width * _height * 4);
        try
        {
            while (!_token.IsCancellationRequested && FrameCount < _maxFrames && frameBytes <= BudgetBytes - _retainedBytes)
            {
                long captureStart = clock.ElapsedMilliseconds;
                try
                {
                    using var frame = _capture.CaptureScreenRectPixels(
                        _physicalRect.X, _physicalRect.Y, _physicalRect.Width, _physicalRect.Height);
                    _token.ThrowIfCancellationRequested();
                    if (frame != null)
                    {
                        using var tile = frame.ToOwnedPixels();
                        var pixels = new byte[frameBytes];
                        var source = tile.Pixels;
                        for (int y = 0; y < _height; y++)
                        {
                            _token.ThrowIfCancellationRequested();
                            int sourceY = (int)((long)y * tile.Height / _height);
                            for (int x = 0; x < _width; x++)
                            {
                                int sourceX = (int)((long)x * tile.Width / _width);
                                source.Slice((sourceY * tile.Stride) + (sourceX * 4), 4)
                                    .CopyTo(pixels.AsSpan(((y * _width) + x) * 4, 4));
                            }
                        }

                        long now = clock.ElapsedMilliseconds;
                        if (_delays.Count > 0)
                        {
                            _delays[^1] = (int)Math.Clamp(now - previousFrameMs, 10, 1000);
                        }

                        previousFrameMs = now;
                        _frames.Add(pixels);
                        _delays.Add(IntervalMs);
                        _retainedBytes += pixels.Length;
                        Interlocked.Increment(ref _frameCount);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Snip GIF frame capture failed: {ex.Message}", typeof(GifRecordingSession));
                }

                int remaining = (int)Math.Max(0, IntervalMs - (clock.ElapsedMilliseconds - captureStart));
                await Task.Delay(remaining, _token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
        finally
        {
            if (_delays.Count > 0)
            {
                _delays[^1] = (int)Math.Clamp(clock.ElapsedMilliseconds - previousFrameMs, 10, 1000);
            }
        }
    }

    public Task<byte[]> StopAndEncodeAsync()
    {
        lock (_sync)
        {
            BeginEncoding();
            var task = EncodeBytesAsync();
            _encoding = task;
            return task;
        }
    }

    public Task StopCapturingAsync()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _cts.Cancel();
            return _loop;
        }
    }

    private async Task<byte[]> EncodeBytesAsync()
    {
        await _loop.ConfigureAwait(false);
        try
        {
            _lifetimeToken.ThrowIfCancellationRequested();
            if (_frames.Count < 2)
            {
                return null;
            }

            return await Task.Run(
                () =>
                {
                    using var output = new MemoryStream();
                    GifEncoder.EncodeToStream(output, _frames, _delays, _width, _height, _width * 4, cancellationToken: _lifetimeToken);
                    return output.ToArray();
                },
                _lifetimeToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseFrames();
        }
    }

    public Task<bool> StopAndSaveAsync(string path, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            BeginEncoding();
            var task = SaveAsync(path, cancellationToken);
            _encoding = task;
            return task;
        }
    }

    private void BeginEncoding()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_encodingStarted)
        {
            throw new InvalidOperationException("GIF is already being encoded.");
        }

        _encodingStarted = true;
        _cts.Cancel();
    }

    private async Task<bool> SaveAsync(string path, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        var saveToken = linked.Token;
        await _loop.ConfigureAwait(false);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            saveToken.ThrowIfCancellationRequested();
            if (_frames.Count < 2)
            {
                return false;
            }

            await Task.Run(
                () =>
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        GifEncoder.EncodeToStream(output, _frames, _delays, _width, _height, _width * 4, cancellationToken: saveToken);
                    }

                    saveToken.ThrowIfCancellationRequested();
                    File.Move(temporary, path, overwrite: false);
                },
                saveToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            ReleaseFrames();
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static string BuildFileName() => $"Snip-GIF-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.gif";

    private void ReleaseFrames()
    {
        _frames.Clear();
        _delays.Clear();
        _retainedBytes = 0;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            _cts.Cancel();
            _ = ReleaseAsync();
        }
    }

    private async Task ReleaseAsync()
    {
        try
        {
            await _loop.ConfigureAwait(false);
            await _encoding.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Closing the session cancels capture and any in-flight encoder.
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip GIF cleanup: {ex.Message}", typeof(GifRecordingSession));
        }
        finally
        {
            ReleaseFrames();
            _cts.Dispose();
            _lifetime.Dispose();
        }
    }
}
