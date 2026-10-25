// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;

using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Optional Windows.Graphics.Capture path (HDR-aware composition, optional cursor).
/// Falls back to GDI when unsupported or when a call fails. Builds on WinRT projections
/// already used by Windows OCR; no extra NuGet packages.
/// </summary>
/// <remarks>
/// Limitations (also in README):
/// <list type="bullet">
/// <item>Requires Windows 10 1903+ and <see cref="GraphicsCaptureSession.IsSupported"/>.</item>
/// <item>One monitor or one window per capture — multi-monitor virtual desktop stays on GDI.</item>
/// <item>Scroll tiles / arbitrary sub-rects stay on GDI (latency).</item>
/// <item>Pixels are converted to SDR BGRA32 for the editor (HDR tone-mapped by the OS copy).</item>
/// <item>Protected / DRM content may yield black frames; GDI is then used.</item>
/// </list>
/// </remarks>
internal static class WinRtGraphicsCapture
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>
    /// Serializes all WinRT capture work. Concurrent CreateFreeThreaded / activation races
    /// surface as CsWinRT "Attempt to update previously set global instance".
    /// </summary>
    private static readonly SemaphoreSlim CaptureGate = new SemaphoreSlim(1, 1);

    private static readonly object DeviceGate = new object();
    private static IDirect3DDevice _device;
    private static Direct3D11CaptureFramePool _reusableFramePool;
    private static SizeInt32 _reusablePoolSize;
    private static bool? _supported;

    // Reused capture item + started session (caller holds CaptureGate). Keeping the
    // session alive across scroll tiles avoids the per-tile CreateCaptureSession /
    // StartCapture latency (DWM re-arms the graph each time, 1-2 frames).
    private static GraphicsCaptureItem _reusableItem;
    private static GraphicsCaptureSession _reusableSession;
    private static IntPtr _reusableItemKey;
    private static bool _reusableSessionCaptureCursor;

    /// <summary>True when the OS reports Graphics Capture is available.</summary>
    public static bool IsSupported
    {
        get
        {
            if (_supported.HasValue)
            {
                return _supported.Value;
            }

            try
            {
                _supported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)
                    && GraphicsCaptureSession.IsSupported();
            }
            catch (Exception ex)
            {
                Log.Warn($"Snip WinRT Graphics Capture probe failed: {ex.Message}", typeof(WinRtGraphicsCapture));
                _supported = false;
            }

            return _supported.Value;
        }
    }

    /// <summary>
    /// Creates the cached D3D device (adapter enumeration + device creation) ahead of
    /// the first capture so the first frame skips that cost. Safe no-op when capture
    /// is unsupported or the device already exists; a failed probe leaves the cache
    /// empty so the next capture retries normally.
    /// </summary>
    public static void PrewarmDevice()
    {
        if (!IsSupported)
        {
            return;
        }

        try
        {
            _ = GetOrCreateDevice();
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip WinRT device prewarm failed (will retry on first capture): {ex.Message}", typeof(WinRtGraphicsCapture));
        }
    }

    /// <summary>
    /// Capture a physical monitor (HMONITOR) into a managed BGRA <see cref="CapturedFrame"/>.
    /// Returns false on any failure (caller should use GDI).
    /// </summary>
    public static bool TryCaptureMonitor(IntPtr hMonitor, bool captureCursor, out CapturedFrame frame)
    {
        frame = null;
        if (hMonitor == IntPtr.Zero || !IsSupported)
        {
            return false;
        }

        if (!CaptureGate.Wait(TimeSpan.FromSeconds(5)))
        {
            Log.Warn("Snip WinRT monitor capture timed out waiting for capture gate", typeof(WinRtGraphicsCapture));
            return false;
        }

        try
        {
            return TryCaptureMonitorUnlocked(hMonitor, captureCursor, out frame);
        }
        finally
        {
            CaptureGate.Release();
        }
    }

    private static bool TryCaptureMonitorUnlocked(IntPtr hMonitor, bool captureCursor, out CapturedFrame frame)
    {
        frame = null;
        try
        {
            var item = GetOrReuseItem(hMonitor, captureCursor, () => CreateItemForMonitor(hMonitor));
            if (item == null)
            {
                return false;
            }

            frame = CaptureItemWithDeviceRetry(item, captureCursor);
            return frame != null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip WinRT monitor capture failed: {ex.Message}", typeof(WinRtGraphicsCapture));
            InvalidateDevice();
            frame = null;
            return false;
        }
    }

    /// <summary>
    /// Returns the cached item+session for <paramref name="itemKey"/> when the target,
    /// size, and cursor flag all match; otherwise recreates the item and restarts the
    /// session. Caller holds <see cref="CaptureGate"/>.
    /// </summary>
    private static GraphicsCaptureItem GetOrReuseItem(
        IntPtr itemKey,
        bool captureCursor,
        Func<GraphicsCaptureItem> createItem)
    {
        if (_reusableItem != null
            && _reusableSession != null
            && _reusableItemKey == itemKey
            && _reusableSessionCaptureCursor == captureCursor)
        {
            return _reusableItem;
        }

        DisposeReusableSession();
        var item = createItem();
        if (item != null)
        {
            _reusableItem = item;
            _reusableItemKey = itemKey;
            _reusableSessionCaptureCursor = captureCursor;
        }

        return item;
    }

    private static void DisposeReusableSession()
    {
        if (_reusableSession != null)
        {
            try
            {
                _reusableSession.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"Snip WinRT reusable session dispose: {ex.Message}", typeof(WinRtGraphicsCapture));
            }

            _reusableSession = null;
        }

        if (_reusableItem != null)
        {
            // GraphicsCaptureItem has no Dispose — the CsWinRT projection releases
            // its COM reference via the finalizer; dropping the reference suffices.
            _reusableItem = null;
        }

        _reusableItemKey = IntPtr.Zero;
    }

    /// <summary>
    /// Capture a top-level window into a managed BGRA <see cref="CapturedFrame"/>.
    /// Returns false on any failure (caller should use GDI).
    /// </summary>
    public static bool TryCaptureWindow(IntPtr hwnd, bool captureCursor, out CapturedFrame frame)
    {
        frame = null;
        if (hwnd == IntPtr.Zero || !IsSupported)
        {
            return false;
        }

        if (!CaptureGate.Wait(TimeSpan.FromSeconds(5)))
        {
            Log.Warn("Snip WinRT window capture timed out waiting for capture gate", typeof(WinRtGraphicsCapture));
            return false;
        }

        try
        {
            return TryCaptureWindowUnlocked(hwnd, captureCursor, out frame);
        }
        finally
        {
            CaptureGate.Release();
        }
    }

    private static bool TryCaptureWindowUnlocked(IntPtr hwnd, bool captureCursor, out CapturedFrame frame)
    {
        frame = null;
        try
        {
            var item = GetOrReuseItem(hwnd, captureCursor, () => CreateItemForWindow(hwnd));
            if (item == null)
            {
                return false;
            }

            frame = CaptureItemWithDeviceRetry(item, captureCursor);
            return frame != null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip WinRT window capture failed: {ex.Message}", typeof(WinRtGraphicsCapture));
            InvalidateDevice();
            frame = null;
            return false;
        }
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr hMonitor)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid iid = GraphicsCaptureItemIid;
        IntPtr abi = interop.CreateForMonitor(hMonitor, ref iid);
        if (abi == IntPtr.Zero)
        {
            return null;
        }

        return MarshalInspectable<GraphicsCaptureItem>.FromAbi(abi);
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid iid = GraphicsCaptureItemIid;
        IntPtr abi = interop.CreateForWindow(hwnd, ref iid);
        if (abi == IntPtr.Zero)
        {
            return null;
        }

        return MarshalInspectable<GraphicsCaptureItem>.FromAbi(abi);
    }

    /// <summary>
    /// One attempt, then invalidate+recreate device and retry once on failure
    /// (device loss / CsWinRT global-instance races after a bad session).
    /// Caller must hold <see cref="CaptureGate"/>.
    /// </summary>
    private static CapturedFrame CaptureItemWithDeviceRetry(GraphicsCaptureItem item, bool captureCursor)
    {
        try
        {
            return CaptureItem(item, captureCursor);
        }
        catch (Exception ex)
        {
            Log.Warn(
                $"Snip WinRT capture attempt failed ({ex.GetType().Name}): {ex.Message}; recreating D3D device",
                typeof(WinRtGraphicsCapture));
            InvalidateDevice();
            return CaptureItem(item, captureCursor);
        }
    }

    /// <summary>
    /// Grabs one frame, starting the capture session on first use for this item and
    /// keeping it alive across calls (scroll tiles). Must run under <see cref="CaptureGate"/>
    /// so only one free-threaded pool is live at a time (avoids CsWinRT global-instance races).
    /// </summary>
    private static CapturedFrame CaptureItem(GraphicsCaptureItem item, bool captureCursor)
    {
        IDirect3DDevice device = GetOrCreateDevice();
        if (device == null)
        {
            return null;
        }

        SizeInt32 size = item.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return null;
        }

        Direct3D11CaptureFramePool framePool = GetOrRecreateFramePool(device, size);
        Direct3D11CaptureFrame captured = null;
        TypedEventHandler<Direct3D11CaptureFramePool, object> arrivedHandler = null;
        try
        {
            // Session survives across captures of the same target; recreate only when
            // the pool was rebuilt (device loss / size change) or the target changed.
            if (_reusableSession == null)
            {
                _reusableSession = framePool.CreateCaptureSession(item);
                _reusableSession.IsCursorCaptureEnabled = captureCursor;
                _reusableSession.StartCapture();
            }

            using var arrived = new ManualResetEventSlim(false);

            // Keep the delegate so a reused pool can be unsubscribed below; a leaked
            // handler steals frames from the next capture (captured ??= race).
            arrivedHandler = (pool, _) =>
            {
                try
                {
                    captured ??= pool.TryGetNextFrame();
                }
                finally
                {
                    arrived.Set();
                }
            };
            framePool.FrameArrived += arrivedHandler;

            if (!arrived.Wait(TimeSpan.FromSeconds(2)) || captured == null)
            {
                return null;
            }

            return CopySurfaceToFrame(captured.Surface, size.Width, size.Height);
        }
        finally
        {
            captured?.Dispose();

            // Drain frames queued by the lingering session before the next wait,
            // so stale frames never satisfy it.
            while (framePool.TryGetNextFrame() is { } leftover)
            {
                leftover.Dispose();
            }

            if (arrivedHandler != null)
            {
                framePool.FrameArrived -= arrivedHandler;
            }
        }
    }

    private static CapturedFrame CopySurfaceToFrame(IDirect3DSurface surface, int width, int height)
    {
        // Synchronous wait: capture already runs on a worker via CaptureFactory callers.
        SoftwareBitmap soft = SoftwareBitmap.CreateCopyFromSurfaceAsync(surface)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        try
        {
            if (soft.BitmapPixelFormat != BitmapPixelFormat.Bgra8
                || soft.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
            {
                var converted = SoftwareBitmap.Convert(
                    soft,
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);
                soft.Dispose();
                soft = converted;
            }

            int stride = CaptureBuffers.BgraStride(width);
            int byteCount = CaptureBuffers.BgraByteCount(width, height);

            // Rent from ArrayPool instead of new byte[] — CapturedFrame returns on Dispose.
            byte[] pixels = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                soft.CopyToBuffer(pixels.AsBuffer());
                var frame = new CapturedFrame(pixels, width, height, stride, rentedFromPool: true);
                pixels = null; // ownership transferred
                return frame;
            }
            finally
            {
                if (pixels != null)
                {
                    ArrayPool<byte>.Shared.Return(pixels);
                }
            }
        }
        finally
        {
            soft.Dispose();
        }
    }

    private static IDirect3DDevice GetOrCreateDevice()
    {
        lock (DeviceGate)
        {
            if (_device != null)
            {
                return _device;
            }

            _device = CreateD3DDevice();
            return _device;
        }
    }

    /// <summary>
    /// Reuse one free-threaded pool while size matches (caller holds CaptureGate).
    /// Recreate when monitor/window size changes.
    /// </summary>
    private static Direct3D11CaptureFramePool GetOrRecreateFramePool(IDirect3DDevice device, SizeInt32 size)
    {
        if (_reusableFramePool != null
            && _reusablePoolSize.Width == size.Width
            && _reusablePoolSize.Height == size.Height)
        {
            return _reusableFramePool;
        }

        DisposeReusableFramePool();
        _reusableFramePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            1,
            size);
        _reusablePoolSize = size;
        return _reusableFramePool;
    }

    private static void DisposeReusableFramePool()
    {
        // Session and item bind to the pool; drop them with it.
        DisposeReusableSession();

        if (_reusableFramePool == null)
        {
            return;
        }

        try
        {
            _reusableFramePool.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip WinRT reusable frame pool dispose: {ex.Message}", typeof(WinRtGraphicsCapture));
        }

        _reusableFramePool = null;
        _reusablePoolSize = default;
    }

    /// <summary>Drop the cached D3D device so the next capture recreates it.</summary>
    private static void InvalidateDevice()
    {
        // Pool is tied to the device; must drop under CaptureGate (caller) + DeviceGate.
        DisposeReusableFramePool();

        lock (DeviceGate)
        {
            if (_device == null)
            {
                return;
            }

            try
            {
                _device.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"Snip WinRT D3D device dispose: {ex.Message}", typeof(WinRtGraphicsCapture));
            }

            _device = null;
        }
    }

    /// <summary>Test hook: ensure capture gate can be entered (not held).</summary>
    internal static bool CaptureGateAvailableForTests => CaptureGate.CurrentCount > 0;

    private static IDirect3DDevice CreateD3DDevice()
    {
        // D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT omitted —
        // BGRA_SUPPORT (0x20) is required for WinRT surfaces.
        const uint createFlags = 0x20;
        int hr = D3D11CreateDevice(
            IntPtr.Zero,
            1, // D3D_DRIVER_TYPE_HARDWARE
            IntPtr.Zero,
            createFlags,
            null,
            0,
            7, // D3D11_SDK_VERSION
            out IntPtr device,
            out _,
            out IntPtr context);
        if (hr < 0 || device == IntPtr.Zero)
        {
            // WARP fallback
            hr = D3D11CreateDevice(
                IntPtr.Zero,
                5, // D3D_DRIVER_TYPE_WARP
                IntPtr.Zero,
                createFlags,
                null,
                0,
                7,
                out device,
                out _,
                out context);
            if (hr < 0 || device == IntPtr.Zero)
            {
                return null;
            }
        }

        try
        {
            Guid iidDxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C"); // IDXGIDevice
            hr = Marshal.QueryInterface(device, in iidDxgiDevice, out IntPtr dxgiDevice);
            if (hr < 0 || dxgiDevice == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr inspectable);
                if (hr < 0 || inspectable == IntPtr.Zero)
                {
                    return null;
                }

                return MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            if (context != IntPtr.Zero)
            {
                Marshal.Release(context);
            }

            Marshal.Release(device);
        }
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        int driverType,
        IntPtr software,
        uint flags,
        int[] featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out IntPtr ppDevice,
        out int pFeatureLevel,
        out IntPtr ppImmediateContext);

    [DllImport(
        "d3d11.dll",
        EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
        SetLastError = true,
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);

        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }
}
