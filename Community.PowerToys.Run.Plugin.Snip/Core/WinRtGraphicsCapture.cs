// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;

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
    private static readonly object DeviceGate = new object();
    private static IDirect3DDevice _device;
    private static bool? _supported;

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

        try
        {
            var item = CreateItemForMonitor(hMonitor);
            if (item == null)
            {
                return false;
            }

            frame = CaptureItem(item, captureCursor);
            return frame != null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip WinRT monitor capture failed: {ex.Message}", typeof(WinRtGraphicsCapture));
            frame = null;
            return false;
        }
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

        try
        {
            var item = CreateItemForWindow(hwnd);
            if (item == null)
            {
                return false;
            }

            frame = CaptureItem(item, captureCursor);
            return frame != null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip WinRT window capture failed: {ex.Message}", typeof(WinRtGraphicsCapture));
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

        using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            1,
            size);
        using var session = framePool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = captureCursor;

        using var arrived = new ManualResetEventSlim(false);
        Direct3D11CaptureFrame captured = null;
        framePool.FrameArrived += (_, _) =>
        {
            try
            {
                captured ??= framePool.TryGetNextFrame();
            }
            finally
            {
                arrived.Set();
            }
        };

        session.StartCapture();
        if (!arrived.Wait(TimeSpan.FromSeconds(2)) || captured == null)
        {
            captured?.Dispose();
            return null;
        }

        try
        {
            return CopySurfaceToFrame(captured.Surface, size.Width, size.Height);
        }
        finally
        {
            captured.Dispose();
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
            var pixels = new byte[byteCount];
            soft.CopyToBuffer(pixels.AsBuffer());
            return new CapturedFrame(pixels, width, height, stride);
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
