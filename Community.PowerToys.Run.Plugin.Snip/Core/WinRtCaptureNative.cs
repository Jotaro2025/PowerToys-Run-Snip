// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Windows.Graphics.Capture (WGC) + D3D11 readback implemented with raw COM
/// vtable calls only. Deliberately avoids the CsWinRT projections used by
/// Windows OCR: PowerToys Run loads a host copy of <c>WinRT.Runtime</c> and the
/// plugin may ship a newer one, and two runtimes in one process make the
/// WinRT marshaling global instance fail ("Attempt to update previously set
/// global instance"), which broke every WGC capture inside PowerToys.
/// </summary>
/// <remarks>
/// HDR displays are captured as <c>R16G16B16A16_FLOAT</c> scRGB and tone-mapped
/// to SDR by <see cref="HdrToneMapper"/>; everything else stays on BGRA8, which
/// the GPU writes unchanged. The 80-nit/yellow-border WGC chrome is disabled on
/// the session so screenshots contain only desktop pixels.
/// </remarks>
internal static class WinRtCaptureNative
{
    private const uint RoInitMultithreaded = 1;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11SdkVersion = 7;
    private const uint D3D11UsageStaging = 3;
    private const uint D3D11CpuAccessRead = 0x20000;
    private const uint D3D11MapRead = 1;
    private const int DxgiFormatBgra8Unorm = 87;
    private const int DxgiFormatR16G16B16A16Float = 10;
    private const int CaptureGateTimeoutMs = 5000;
    private const int FrameWaitTimeoutMs = 2000;
    private const int FramePollIntervalMs = 10;

    private const string GraphicsCaptureItemClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
    private const string FramePoolClass = "Windows.Graphics.Capture.Direct3D11CaptureFramePool";

    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidFramePoolStatics2 = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
    private static readonly Guid IidSession2 = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
    private static readonly Guid IidSession3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
    private static readonly Guid IidClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    private static readonly Guid IidDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IidTexture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private static readonly object DeviceLock = new();
    private static readonly SemaphoreSlim CaptureGate = new(1, 1);

    [ThreadStatic]
    private static bool _comInitialized;

    private static IntPtr _itemInteropFactory;
    private static IntPtr _framePoolStatics;
    private static IntPtr _d3dDevice;
    private static IntPtr _d3dContext;
    private static IntPtr _rtDevice;
    private static bool _factoriesFailed;

    /// <summary>True on builds that ship the Graphics Capture API.</summary>
    public static bool IsSupportedPlatform
        => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362);

    /// <summary>Test hook: ensure the capture gate can be entered (not held).</summary>
    internal static bool CaptureGateAvailableForTests => CaptureGate.CurrentCount > 0;

    /// <summary>
    /// Creates the activation factories and the D3D11 device ahead of the first
    /// capture so the first frame skips that cost. Safe no-op when unsupported.
    /// </summary>
    public static void Prewarm()
    {
        if (!IsSupportedPlatform)
        {
            return;
        }

        if (!CaptureGate.Wait(CaptureGateTimeoutMs))
        {
            return;
        }

        try
        {
            EnsureComInitialized();
            _ = EnsureFactories();
            _ = EnsureDevice();
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip WinRT device prewarm failed (will retry on first capture): {ex.Message}", typeof(WinRtCaptureNative));
        }
        finally
        {
            CaptureGate.Release();
        }
    }

    /// <summary>Drops the cached D3D11 device so the next capture recreates it.</summary>
    public static void InvalidateDevice()
    {
        lock (DeviceLock)
        {
            ReleaseIfSet(ref _rtDevice);
            ReleaseIfSet(ref _d3dContext);
            ReleaseIfSet(ref _d3dDevice);
        }
    }

    /// <summary>
    /// Captures one frame of a monitor (<paramref name="key"/> = HMONITOR) or a
    /// top-level window (<paramref name="key"/> = HWND). <paramref name="cropX"/>�?    /// are item-local physical pixels; pass a non-positive width for the whole item.
    /// </summary>
    public static bool TryCapture(
        IntPtr key,
        bool isWindow,
        bool captureCursor,
        int cropX,
        int cropY,
        int cropWidth,
        int cropHeight,
        double whiteScale,
        bool useFp16,
        out CapturedFrame frame)
    {
        frame = null;
        if (key == IntPtr.Zero || !IsSupportedPlatform)
        {
            return false;
        }

        if (!CaptureGate.Wait(CaptureGateTimeoutMs))
        {
            Log.Warn("Snip WinRT capture timed out waiting for capture gate", typeof(WinRtCaptureNative));
            return false;
        }

        try
        {
            EnsureComInitialized();
            return TryCaptureUnlocked(key, isWindow, captureCursor, cropX, cropY, cropWidth, cropHeight, whiteScale, useFp16, out frame);
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip WinRT capture failed: {ex.GetType().Name}: {ex.Message}", typeof(WinRtCaptureNative));
            InvalidateDevice();
            frame = null;
            return false;
        }
        finally
        {
            CaptureGate.Release();
        }
    }

    private static unsafe bool TryCaptureUnlocked(
        IntPtr key,
        bool isWindow,
        bool captureCursor,
        int cropX,
        int cropY,
        int cropWidth,
        int cropHeight,
        double whiteScale,
        bool useFp16,
        out CapturedFrame frame)
    {
        frame = null;
        if (!EnsureFactories())
        {
            return false;
        }

        IntPtr device = EnsureDevice();
        if (device == IntPtr.Zero)
        {
            return false;
        }

        IntPtr item = IntPtr.Zero;
        IntPtr pool = IntPtr.Zero;
        IntPtr session = IntPtr.Zero;
        IntPtr captured = IntPtr.Zero;
        IntPtr surface = IntPtr.Zero;
        IntPtr texture = IntPtr.Zero;
        IntPtr staging = IntPtr.Zero;
        bool mapped = false;
        byte[] pixels = null;
        try
        {
            int hr = isWindow
                ? CreateItemForWindow(_itemInteropFactory, key, out item)
                : CreateItemForMonitor(_itemInteropFactory, key, out item);
            if (hr < 0 || item == IntPtr.Zero)
            {
                Log.Warn($"Snip WinRT CreateFor{(isWindow ? "Window" : "Monitor")} failed hr=0x{hr:X8}", typeof(WinRtCaptureNative));
                return false;
            }

            int sizeHr = GetItemSize(item, out SizeInt32 itemSize);
            if (sizeHr < 0 || itemSize.Width <= 0 || itemSize.Height <= 0)
            {
                Log.Warn(
                    $"Snip WinRT item size invalid hr=0x{sizeHr:X8} size={itemSize.Width}x{itemSize.Height}",
                    typeof(WinRtCaptureNative));
                return false;
            }

            int pixelFormat = useFp16 ? DxgiFormatR16G16B16A16Float : DxgiFormatBgra8Unorm;
            hr = CreateFreeThreadedFramePool(_framePoolStatics, _rtDevice, pixelFormat, itemSize, out pool);
            if (hr < 0 || pool == IntPtr.Zero)
            {
                Log.Warn($"Snip WinRT CreateFreeThreaded failed hr=0x{hr:X8}", typeof(WinRtCaptureNative));
                return false;
            }

            hr = CreateCaptureSession(pool, item, out session);
            if (hr < 0 || session == IntPtr.Zero)
            {
                Log.Warn($"Snip WinRT CreateCaptureSession failed hr=0x{hr:X8}", typeof(WinRtCaptureNative));
                return false;
            }

            ConfigureSession(session, captureCursor);
            hr = StartCapture(session);
            if (hr < 0)
            {
                Log.Warn($"Snip WinRT StartCapture failed hr=0x{hr:X8}", typeof(WinRtCaptureNative));
                return false;
            }

            if (!WaitForFrame(pool, out captured))
            {
                return false;
            }

            int surfaceHr = GetSurface(captured, out surface);
            if (surfaceHr < 0 || surface == IntPtr.Zero)
            {
                Log.Warn($"Snip WinRT frame surface missing hr=0x{surfaceHr:X8}", typeof(WinRtCaptureNative));
                return false;
            }

            hr = GetD3DTexture(surface, out texture);
            if (hr < 0 || texture == IntPtr.Zero)
            {
                Log.Warn($"Snip WinRT IDirect3DDxgiInterfaceAccess::GetInterface failed hr=0x{hr:X8}", typeof(WinRtCaptureNative));
                return false;
            }

            D3D11_TEXTURE2D_DESC desc = GetTextureDesc(texture);
            GetContentSize(captured, out SizeInt32 content);
            int availableWidth = Math.Min((int)desc.Width, content.Width > 0 ? content.Width : itemSize.Width);
            int availableHeight = Math.Min((int)desc.Height, content.Height > 0 ? content.Height : itemSize.Height);

            int sourceX = cropWidth > 0 ? cropX : 0;
            int sourceY = cropHeight > 0 ? cropY : 0;
            int sourceWidth = cropWidth > 0 ? cropWidth : availableWidth;
            int sourceHeight = cropHeight > 0 ? cropHeight : availableHeight;
            if (sourceX < 0 || sourceY < 0
                || sourceWidth <= 0 || sourceHeight <= 0
                || sourceX + sourceWidth > availableWidth
                || sourceY + sourceHeight > availableHeight)
            {
                Log.Info(
                    $"Snip WinRT crop outside frame ({sourceX},{sourceY},{sourceWidth}x{sourceHeight}) frame={availableWidth}x{availableHeight}",
                    typeof(WinRtCaptureNative));
                return false;
            }

            hr = CreateStagingTexture(device, (uint)sourceWidth, (uint)sourceHeight, desc.Format, out staging);
            if (hr < 0 || staging == IntPtr.Zero)
            {
                Log.Warn($"Snip WinRT staging texture failed hr=0x{hr:X8}", typeof(WinRtCaptureNative));
                return false;
            }

            CopySubresource(staging, texture, sourceX, sourceY, sourceWidth, sourceHeight);
            hr = MapStaging(_d3dContext, staging, out D3D11_MAPPED_SUBRESOURCE mappedResource);
            if (hr < 0 || mappedResource.PData == IntPtr.Zero)
            {
                Log.Warn($"Snip WinRT Map failed hr=0x{hr:X8}", typeof(WinRtCaptureNative));
                return false;
            }

            mapped = true;
            int stride = sourceWidth * 4;
            int byteCount = stride * sourceHeight;
            pixels = ArrayPool<byte>.Shared.Rent(byteCount);
            if (desc.Format == DxgiFormatR16G16B16A16Float)
            {
                HdrToneMapper.ConvertRgba16ToBgra8(
                    new ReadOnlySpan<byte>((void*)mappedResource.PData, (int)mappedResource.RowPitch * sourceHeight),
                    (int)mappedResource.RowPitch,
                    pixels.AsSpan(0, byteCount),
                    sourceWidth,
                    sourceHeight,
                    whiteScale);
            }
            else
            {
                CopyBgraRows(mappedResource.PData, (int)mappedResource.RowPitch, pixels, stride, sourceWidth, sourceHeight);
            }

            frame = new CapturedFrame(pixels, sourceWidth, sourceHeight, stride, rentedFromPool: true);
            pixels = null; // ownership transferred
            return true;
        }
        finally
        {
            if (mapped && staging != IntPtr.Zero)
            {
                UnmapStaging(_d3dContext, staging);
            }

            if (pixels != null)
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }

            ReleaseIfSet(ref staging);
            ReleaseIfSet(ref texture);
            ReleaseIfSet(ref surface);
            ReleaseIfSet(ref captured);
            CloseAndRelease(ref session);
            CloseAndRelease(ref pool);
            ReleaseIfSet(ref item);
        }
    }

    private static bool WaitForFrame(IntPtr pool, out IntPtr frame)
    {
        frame = IntPtr.Zero;
        long deadline = Environment.TickCount64 + FrameWaitTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            int hr = TryGetNextFrame(pool, out frame);
            if (hr < 0)
            {
                Log.Warn($"Snip WinRT TryGetNextFrame failed hr=0x{hr:X8}", typeof(WinRtCaptureNative));
                frame = IntPtr.Zero;
                return false;
            }

            if (frame != IntPtr.Zero)
            {
                return true;
            }

            Thread.Sleep(FramePollIntervalMs);
        }

        Log.Warn($"Snip WinRT frame wait timed out after {FrameWaitTimeoutMs}ms", typeof(WinRtCaptureNative));
        return false;
    }

    private static void ConfigureSession(IntPtr session, bool captureCursor)
    {
        // Optional interfaces: absent on older builds �?keep OS defaults.
        if (TryQueryInterface(session, IidSession2, out IntPtr session2))
        {
            SetIsCursorCaptureEnabled(session2, captureCursor);
            Marshal.Release(session2);
        }

        if (TryQueryInterface(session, IidSession3, out IntPtr session3))
        {
            SetIsBorderRequired(session3, false);
            Marshal.Release(session3);
        }
    }

    private static bool EnsureFactories()
    {
        if (_itemInteropFactory != IntPtr.Zero && _framePoolStatics != IntPtr.Zero)
        {
            return true;
        }

        lock (DeviceLock)
        {
            if (_factoriesFailed)
            {
                return false;
            }

            if (_itemInteropFactory == IntPtr.Zero)
            {
                _itemInteropFactory = GetActivationFactory(GraphicsCaptureItemClass, IidItemInterop);
            }

            if (_framePoolStatics == IntPtr.Zero)
            {
                _framePoolStatics = GetActivationFactory(FramePoolClass, IidFramePoolStatics2);
            }

            if (_itemInteropFactory == IntPtr.Zero || _framePoolStatics == IntPtr.Zero)
            {
                _factoriesFailed = true;
                ReleaseIfSet(ref _itemInteropFactory);
                ReleaseIfSet(ref _framePoolStatics);
                return false;
            }

            return true;
        }
    }

    private static IntPtr EnsureDevice()
    {
        lock (DeviceLock)
        {
            if (_d3dDevice != IntPtr.Zero && _d3dContext != IntPtr.Zero && _rtDevice != IntPtr.Zero)
            {
                return _d3dDevice;
            }

            ReleaseIfSet(ref _rtDevice);
            ReleaseIfSet(ref _d3dContext);
            ReleaseIfSet(ref _d3dDevice);

            int hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, D3D11CreateDeviceBgraSupport, null, 0, D3D11SdkVersion, out IntPtr device, out _, out IntPtr context);
            if (hr < 0)
            {
                hr = D3D11CreateDevice(IntPtr.Zero, 5, IntPtr.Zero, D3D11CreateDeviceBgraSupport, null, 0, D3D11SdkVersion, out device, out _, out context);
            }

            if (hr < 0 || device == IntPtr.Zero || context == IntPtr.Zero)
            {
                if (device != IntPtr.Zero)
                {
                    Marshal.Release(device);
                }

                if (context != IntPtr.Zero)
                {
                    Marshal.Release(context);
                }

                Log.Warn($"Snip WinRT D3D11CreateDevice failed hr=0x{hr:X8}", typeof(WinRtCaptureNative));
                return IntPtr.Zero;
            }

            IntPtr rtDevice = CreateDirect3DDevice(device);
            if (rtDevice == IntPtr.Zero)
            {
                Marshal.Release(context);
                Marshal.Release(device);
                return IntPtr.Zero;
            }

            _d3dDevice = device;
            _d3dContext = context;
            _rtDevice = rtDevice;
            return _d3dDevice;
        }
    }

    private static IntPtr CreateDirect3DDevice(IntPtr d3dDevice)
    {
        Guid iidDxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
        int hr = Marshal.QueryInterface(d3dDevice, in iidDxgiDevice, out IntPtr dxgiDevice);
        if (hr < 0)
        {
            Log.Warn($"Snip WinRT QI IDXGIDevice failed hr=0x{hr:X8}", typeof(WinRtCaptureNative));
            return IntPtr.Zero;
        }

        try
        {
            hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr inspectable);
            if (hr < 0)
            {
                Log.Warn($"Snip WinRT CreateDirect3D11DeviceFromDXGIDevice failed hr=0x{hr:X8}", typeof(WinRtCaptureNative));
                return IntPtr.Zero;
            }

            return inspectable;
        }
        finally
        {
            Marshal.Release(dxgiDevice);
        }
    }

    private static IntPtr GetActivationFactory(string className, Guid iid)
    {
        int hr = WindowsCreateString(className, className.Length, out IntPtr hstring);
        if (hr < 0)
        {
            return IntPtr.Zero;
        }

        try
        {
            Guid localIid = iid;
            hr = RoGetActivationFactory(hstring, ref localIid, out IntPtr factory);
            if (hr < 0)
            {
                Log.Warn($"Snip WinRT RoGetActivationFactory({className}) failed hr=0x{hr:X8}", typeof(WinRtCaptureNative));
                return IntPtr.Zero;
            }

            return factory;
        }
        finally
        {
            _ = WindowsDeleteString(hstring);
        }
    }

    private static void EnsureComInitialized()
    {
        if (_comInitialized)
        {
            return;
        }

        // S_FALSE (already initialized) and RPC_E_CHANGED_MODE (host thread is
        // STA) are both fine; only the factories need an initialized apartment.
        _ = RoInitialize(RoInitMultithreaded);
        _comInitialized = true;
    }

    private static bool TryQueryInterface(IntPtr instance, Guid iid, out IntPtr result)
    {
        return Marshal.QueryInterface(instance, in iid, out result) >= 0 && result != IntPtr.Zero;
    }

    private static void ReleaseIfSet(ref IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            _ = Marshal.Release(pointer);
            pointer = IntPtr.Zero;
        }
    }

    private static void CloseAndRelease(ref IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        if (TryQueryInterface(pointer, IidClosable, out IntPtr closable))
        {
            Close(closable);
            Marshal.Release(closable);
        }

        ReleaseIfSet(ref pointer);
    }

    // ---- WinRT ABI calls ----
    // Slots: IUnknown 0-2, IInspectable 3-5, derived interface methods start at 6.
    private static unsafe int CreateItemForWindow(IntPtr factory, IntPtr hwnd, out IntPtr item)
    {
        Guid iid = IidGraphicsCaptureItem;
        void** vtbl = *(void***)factory;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[3];
        IntPtr result;
        int hr = fn(factory, hwnd, &iid, &result);
        item = result;
        return hr;
    }

    private static unsafe int CreateItemForMonitor(IntPtr factory, IntPtr hMonitor, out IntPtr item)
    {
        Guid iid = IidGraphicsCaptureItem;
        void** vtbl = *(void***)factory;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[4];
        IntPtr result;
        int hr = fn(factory, hMonitor, &iid, &result);
        item = result;
        return hr;
    }

    private static unsafe int GetItemSize(IntPtr item, out SizeInt32 size)
    {
        void** vtbl = *(void***)item;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, SizeInt32*, int>)vtbl[7];
        SizeInt32 value;
        int hr = fn(item, &value);
        size = value;
        return hr;
    }

    private static int CreateFreeThreadedFramePool(IntPtr statics, IntPtr device, int pixelFormat, SizeInt32 size, out IntPtr pool)
    {
        // ComImport marshaling instead of a raw function pointer: Windows.Graphics.SizeInt32
        // is passed by value in the ABI and the raw-call/delegate paths AV on that
        // signature (verified in-host). The CLR's COM stub handles it correctly.
        var factory = (IDirect3D11CaptureFramePoolStatics2)Marshal.GetObjectForIUnknown(statics);
        return factory.CreateFreeThreaded(device, pixelFormat, 1, size, out pool);
    }

    private static unsafe int CreateCaptureSession(IntPtr pool, IntPtr item, out IntPtr session)
    {
        void** vtbl = *(void***)pool;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)vtbl[10];
        IntPtr result;
        int hr = fn(pool, item, &result);
        session = result;
        return hr;
    }

    private static unsafe int TryGetNextFrame(IntPtr pool, out IntPtr frame)
    {
        void** vtbl = *(void***)pool;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtbl[7];
        IntPtr result;
        int hr = fn(pool, &result);
        frame = result;
        return hr;
    }

    private static unsafe int StartCapture(IntPtr session)
    {
        void** vtbl = *(void***)session;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[6];
        return fn(session);
    }

    private static unsafe void SetIsCursorCaptureEnabled(IntPtr session2, bool enabled)
    {
        void** vtbl = *(void***)session2;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)vtbl[7];
        _ = fn(session2, enabled ? (byte)1 : (byte)0);
    }

    private static unsafe void SetIsBorderRequired(IntPtr session3, bool required)
    {
        void** vtbl = *(void***)session3;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)vtbl[7];
        _ = fn(session3, required ? (byte)1 : (byte)0);
    }

    private static unsafe int GetSurface(IntPtr frame, out IntPtr surface)
    {
        void** vtbl = *(void***)frame;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtbl[6];
        IntPtr result;
        int hr = fn(frame, &result);
        surface = result;
        return hr;
    }

    private static unsafe void GetContentSize(IntPtr frame, out SizeInt32 size)
    {
        void** vtbl = *(void***)frame;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, SizeInt32*, int>)vtbl[8];
        SizeInt32 value;
        _ = fn(frame, &value);
        size = value;
    }

    private static unsafe void Close(IntPtr closable)
    {
        void** vtbl = *(void***)closable;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[6];
        _ = fn(closable);
    }

    private static unsafe int GetD3DTexture(IntPtr surface, out IntPtr texture)
    {
        if (Marshal.QueryInterface(surface, in IidDxgiInterfaceAccess, out IntPtr access) < 0 || access == IntPtr.Zero)
        {
            texture = IntPtr.Zero;
            return -1;
        }

        try
        {
            Guid iid = IidTexture2D;
            void** vtbl = *(void***)access;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[3];
            IntPtr result;
            int hr = fn(access, &iid, &result);
            texture = result;
            return hr;
        }
        finally
        {
            Marshal.Release(access);
        }
    }

    // ---- D3D11 calls ----
    private static unsafe D3D11_TEXTURE2D_DESC GetTextureDesc(IntPtr texture)
    {
        void** vtbl = *(void***)texture;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, void>)vtbl[10];
        D3D11_TEXTURE2D_DESC desc;
        fn(texture, &desc);
        return desc;
    }

    private static unsafe int CreateStagingTexture(IntPtr device, uint width, uint height, uint format, out IntPtr texture)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new DXGISampleDesc { Count = 1, Quality = 0 },
            Usage = D3D11UsageStaging,
            BindFlags = 0,
            CPUAccessFlags = D3D11CpuAccessRead,
            MiscFlags = 0,
        };

        void** vtbl = *(void***)device;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)vtbl[5];
        IntPtr result;
        int hr = fn(device, &desc, IntPtr.Zero, &result);
        texture = result;
        return hr;
    }

    private static unsafe void CopySubresource(IntPtr destination, IntPtr source, int x, int y, int width, int height)
    {
        var box = new D3D11_BOX
        {
            Left = (uint)x,
            Top = (uint)y,
            Front = 0,
            Right = (uint)(x + width),
            Bottom = (uint)(y + height),
            Back = 1,
        };

        void** vtbl = *(void***)_d3dContext;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, D3D11_BOX*, void>)vtbl[46];
        fn(_d3dContext, destination, 0, 0, 0, 0, source, 0, &box);
    }

    private static unsafe int MapStaging(IntPtr context, IntPtr resource, out D3D11_MAPPED_SUBRESOURCE mapped)
    {
        void** vtbl = *(void***)context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, D3D11_MAPPED_SUBRESOURCE*, int>)vtbl[14];
        D3D11_MAPPED_SUBRESOURCE value;
        int hr = fn(context, resource, 0, D3D11MapRead, 0, &value);
        mapped = value;
        return hr;
    }

    private static unsafe void UnmapStaging(IntPtr context, IntPtr resource)
    {
        void** vtbl = *(void***)context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)vtbl[15];
        fn(context, resource, 0);
    }

    private static void CopyBgraRows(IntPtr source, int sourceStride, byte[] destination, int destinationStride, int width, int height)
    {
        int rowBytes = width * 4;
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(source + (y * sourceStride), destination, y * destinationStride, rowBytes);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeInt32
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGISampleDesc
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public DXGISampleDesc SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_BOX
    {
        public uint Left;
        public uint Top;
        public uint Front;
        public uint Right;
        public uint Bottom;
        public uint Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr PData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    /// <summary>
    /// Used only for <c>CreateFreeThreaded</c>, whose <see cref="SizeInt32"/>
    /// by-value parameter must go through the CLR COM stub (see the caller).
    /// </summary>
    [ComImport]
    [Guid("589B103F-6BBC-5DF5-A991-02E28B3B66D5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3D11CaptureFramePoolStatics2
    {
        void GetIids(out uint iidCount, out IntPtr iids);

        void GetRuntimeClassName(out IntPtr name);

        void GetTrustLevel(out int level);

        [PreserveSig]
        int CreateFreeThreaded(IntPtr device, int pixelFormat, int numberOfBuffers, SizeInt32 size, out IntPtr result);
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoInitialize(uint initType);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        int[] featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
