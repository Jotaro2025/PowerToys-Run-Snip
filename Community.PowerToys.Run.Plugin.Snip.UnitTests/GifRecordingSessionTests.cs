// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class GifRecordingSessionTests
{
    private sealed class FakeCapture : ICaptureService
    {
        public int CaptureCount;

        public int CaptureDelayMs { get; init; }

        public IReadOnlyList<ScreenInfo> GetScreens() => [Screen(8, 8)];

        public System.Windows.Media.Imaging.WriteableBitmap CaptureScreen(ScreenInfo screen) => null;

        public CapturedFrame CaptureScreenPixels(ScreenInfo screen)
            => CaptureScreenRectPixels(0, 0, (int)screen.Bounds.Width, (int)screen.Bounds.Height);

        public CapturedFrame CaptureScreenRectPixels(int physicalX, int physicalY, int width, int height)
        {
            Interlocked.Increment(ref CaptureCount);
            if (CaptureDelayMs > 0)
            {
                Thread.Sleep(CaptureDelayMs);
            }

            return new CapturedFrame(new byte[width * 4 * height], width, height, width * 4);
        }
    }

    private static ScreenInfo Screen(int w, int h) => new(
        IntPtr.Zero,
        "Fake",
        new Rect(0, 0, w, h),
        new Rect(0, 0, w, h),
        1.0,
        IsPrimary: true);

    [TestMethod]
    public async System.Threading.Tasks.Task StartCapturesFramesThenStopEncodes()
    {
        var capture = new FakeCapture();
        using var session = new GifRecordingSession(new Int32Rect(0, 0, 8, 8), capture);
        session.Start();
        await System.Threading.Tasks.Task.Delay(300);
        Assert.IsTrue(session.IsRunning, "loop should still run before stop");

        byte[] gif = await session.StopAndEncodeAsync();
        Assert.IsNotNull(gif, "should encode after collecting frames");
        Assert.IsTrue(session.FrameCount >= 2, $"expected >=2 frames, got {session.FrameCount}");
        Assert.IsFalse(session.IsRunning);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task DisposeStopsTheCaptureLoop()
    {
        var capture = new FakeCapture();
        var session = new GifRecordingSession(new Int32Rect(0, 0, 8, 8), capture);
        session.Start();
        await System.Threading.Tasks.Task.Delay(200);
        int countAtDispose = Volatile.Read(ref capture.CaptureCount);

        session.Dispose(); // cancel / close path: no StopAndEncodeAsync call

        // Give the loop time to unwind; it must stop grabbing frames.
        await System.Threading.Tasks.Task.Delay(400);
        int countAfter = Volatile.Read(ref capture.CaptureCount);
        Assert.IsTrue(
            countAfter <= countAtDispose + 2,
            $"capture loop kept running after Dispose: {countAtDispose} -> {countAfter}");
        Assert.IsFalse(session.IsRunning);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task DisposeTwiceIsSafe()
    {
        var capture = new FakeCapture();
        var session = new GifRecordingSession(new Int32Rect(0, 0, 8, 8), capture);
        session.Start();
        await System.Threading.Tasks.Task.Delay(120);
        session.Dispose();
        session.Dispose();
        Assert.IsFalse(session.IsRunning);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task StopAndEncodeTooFewFramesReturnsNull()
    {
        var capture = new FakeCapture();
        using var session = new GifRecordingSession(new Int32Rect(0, 0, 8, 8), capture);
        // No Start: zero frames.
        byte[] gif = await session.StopAndEncodeAsync();
        Assert.IsNull(gif);
    }

    [TestMethod]
    public void FrameBudgetScalesWithResolution()
    {
        var capture = new FakeCapture();

        // Tiny regions keep the 24 MB floor.
        using var small = new GifRecordingSession(new Int32Rect(0, 0, 8, 8), capture);
        Assert.AreEqual(24L * 1024 * 1024, small.BudgetBytes);

        // 400×300 (~480 KB per frame) scales to ~80 frames rather than the floor.
        using var medium = new GifRecordingSession(new Int32Rect(0, 0, 400, 300), capture);
        Assert.IsTrue(medium.BudgetBytes >= 50L * 400 * 300 * 4, $"medium budget holds {medium.BudgetBytes / (400L * 300 * 4)} frames");

        // Full screen must not stop after two frames: 3440×1440 is ~19.8 MB per
        // frame, so the budget has to grow (capped at 256 MB).
        using var full = new GifRecordingSession(new Int32Rect(0, 0, 3440, 1440), capture);
        Assert.IsTrue(
            full.BudgetBytes >= 10L * 3440 * 1440 * 4,
            $"full-screen budget only holds {full.BudgetBytes / (3440L * 1440 * 4)} frames");
        Assert.IsTrue(full.BudgetBytes <= 256L * 1024 * 1024);
        Assert.IsTrue(full.BudgetBytes / ((long)full.OutputWidth * full.OutputHeight * 4) >= 100, "scaled capture should retain at least 8 seconds of frames");
    }

    [TestMethod]
    public async Task AutoStoppedSessionKeepsFramesUntilSaved()
    {
        string path = Path.Combine(Path.GetTempPath(), GifRecordingSession.BuildFileName());
        try
        {
            using var session = new GifRecordingSession(new Int32Rect(0, 0, 1920, 1080), new FakeCapture(), maxFrames: 2);
            session.Start();
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(session.IsRunning);
            Assert.IsTrue(session.HasPendingFrames);
            Assert.AreEqual(2, session.FrameCount);
            Assert.IsTrue(await session.StopAndSaveAsync(path).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(session.HasPendingFrames);

            using var stream = File.OpenRead(path);
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.AreEqual(2, decoder.Frames.Count);
            Assert.AreEqual(960, decoder.Frames[0].PixelWidth);
            Assert.AreEqual(540, decoder.Frames[0].PixelHeight);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task CaptureLatencyIsIncludedInFrameDelays()
    {
        using var session = new GifRecordingSession(new Int32Rect(0, 0, 8, 8), new FakeCapture { CaptureDelayMs = 140 }, maxFrames: 2);
        session.Start();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, session.FrameCount);
        Assert.IsTrue(session.FrameDelays[0] >= 130, $"capture time was lost: {session.FrameDelays[0]} ms");
    }

    [TestMethod]
    public async Task CancelledSaveDoesNotPublishOrLeaveTemporaryFile()
    {
        string path = Path.Combine(Path.GetTempPath(), GifRecordingSession.BuildFileName());
        using var session = new GifRecordingSession(new Int32Rect(0, 0, 8, 8), new FakeCapture(), maxFrames: 2);
        session.Start();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => session.StopAndSaveAsync(path, cts.Token));
        Assert.IsFalse(File.Exists(path));
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".*.tmp").Length);
    }

    [TestMethod]
    public async Task DisposeWaitsForInFlightCaptureBeforeReleasingFrames()
    {
        var capture = new GatedCapture();
        using var session = new GifRecordingSession(new Int32Rect(0, 0, 8, 8), capture);
        session.Start();
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Dispose();
        Assert.IsFalse(session.IsRunning);
        Assert.IsFalse(session.Completion.IsCompleted, "capture still owns its frame");
        capture.Release.TrySetResult();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, session.FrameCount, "a cancelled capture must not be appended");
        Assert.ThrowsExactly<ObjectDisposedException>(() => capture.Frame.ToOwnedPixels());
    }

    [TestMethod]
    public async Task SaveDoesNotOverwriteExistingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), GifRecordingSession.BuildFileName());
        try
        {
            File.WriteAllText(path, "existing recording");
            using var session = new GifRecordingSession(new Int32Rect(0, 0, 8, 8), new FakeCapture(), maxFrames: 2);
            session.Start();
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsExactlyAsync<IOException>(() => session.StopAndSaveAsync(path));
            Assert.AreEqual("existing recording", File.ReadAllText(path));
            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".*.tmp").Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class GatedCapture : ICaptureService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CapturedFrame Frame { get; } = new(new byte[256], 8, 8, 32);

        public IReadOnlyList<ScreenInfo> GetScreens() => [];

        public WriteableBitmap CaptureScreen(ScreenInfo screen) => throw new NotSupportedException();

        public CapturedFrame CaptureScreenPixels(ScreenInfo screen) => throw new NotSupportedException();

        public CapturedFrame CaptureScreenRectPixels(int physicalX, int physicalY, int width, int height)
        {
            Started.TrySetResult();
            Release.Task.Wait(TimeSpan.FromSeconds(5));
            return Frame;
        }
    }
}
