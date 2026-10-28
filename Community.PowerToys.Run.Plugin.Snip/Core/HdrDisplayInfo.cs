// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Queries <c>QueryDisplayConfig</c> for the advanced-color state and SDR white
/// level of the display that owns a given monitor or window. Results are cached
/// briefly because scroll capture asks once per tile.
/// </summary>
internal static class HdrDisplayInfo
{
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const uint GetSourceDeviceName = 1;
    private const uint GetAdvancedColorInfoType = 9;
    private const uint GetSdrWhiteLevelType = 11;
    private const int ErrorInsufficientBuffer = 122;
    private const int CacheTtlMs = 2000;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<IntPtr, CacheEntry> Cache = new();
    private static long _nextCleanupTick;

    /// <summary>State for the display containing <paramref name="hwnd"/>.</summary>
    public static HdrDisplayState GetForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return HdrDisplayState.Sdr;
        }

        IntPtr monitor = Win32.MonitorFromWindow(hwnd, Win32.MonitorDefaultToNearest);
        return monitor == IntPtr.Zero ? HdrDisplayState.Sdr : GetForMonitor(monitor);
    }

    /// <summary>State for <paramref name="hMonitor"/> (HMONITOR).</summary>
    public static HdrDisplayState GetForMonitor(IntPtr hMonitor)
    {
        if (hMonitor == IntPtr.Zero)
        {
            return HdrDisplayState.Sdr;
        }

        long now = Environment.TickCount64;
        lock (CacheLock)
        {
            if (Cache.TryGetValue(hMonitor, out CacheEntry cached) && now - cached.TimestampMs < CacheTtlMs)
            {
                return cached.State;
            }
        }

        HdrDisplayState state = Query(hMonitor);
        lock (CacheLock)
        {
            Cache[hMonitor] = new CacheEntry(state, now);
            if (now >= _nextCleanupTick)
            {
                PruneExpired(now);
                _nextCleanupTick = now + CacheTtlMs;
            }
        }

        return state;
    }

    /// <summary>Drops cached values (tests / display mode changes).</summary>
    internal static void InvalidateCache()
    {
        lock (CacheLock)
        {
            Cache.Clear();
        }
    }

    private static HdrDisplayState Query(IntPtr hMonitor)
    {
        try
        {
            var info = new Win32.MONITORINFOEX { CbSize = Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (!Win32.GetMonitorInfo(hMonitor, ref info) || string.IsNullOrEmpty(info.SzDevice))
            {
                return HdrDisplayState.Sdr;
            }

            if (!TryGetPaths(out DISPLAYCONFIG_PATH_INFO[] paths))
            {
                return HdrDisplayState.Sdr;
            }

            foreach (DISPLAYCONFIG_PATH_INFO path in paths)
            {
                if (!TryGetSourceDeviceName(path, out string viewGdiDeviceName)
                    || !string.Equals(viewGdiDeviceName, info.SzDevice, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool advancedColorEnabled = TryGetAdvancedColorEnabled(path, out bool enabled) && enabled;
                _ = TryGetSdrWhiteLevel(path, out uint whiteLevel);
                return new HdrDisplayState(advancedColorEnabled, whiteLevel);
            }
        }
        catch (Exception)
        {
            // Display topology can change mid-query; SDR (no tone mapping) is the safe default.
        }

        return HdrDisplayState.Sdr;
    }

    private static bool TryGetPaths(out DISPLAYCONFIG_PATH_INFO[] paths)
    {
        paths = [];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int hr = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount);
            if (hr != 0 || pathCount == 0)
            {
                return false;
            }

            var pathArray = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modeArray = new DISPLAYCONFIG_MODE_INFO[modeCount];
            uint pathsUsed = pathCount;
            uint modesUsed = modeCount;
            hr = QueryDisplayConfig(QdcOnlyActivePaths, ref pathsUsed, pathArray, ref modesUsed, modeArray, IntPtr.Zero);
            if (hr == ErrorInsufficientBuffer)
            {
                continue;
            }

            if (hr != 0)
            {
                return false;
            }

            paths = pathArray[..(int)pathsUsed];
            return paths.Length > 0;
        }

        return false;
    }

    private static bool TryGetSourceDeviceName(DISPLAYCONFIG_PATH_INFO path, out string deviceName)
    {
        deviceName = string.Empty;
        var packet = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            Header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                Type = GetSourceDeviceName,
                Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                AdapterId = path.SourceInfo.AdapterId,
                Id = path.SourceInfo.Id,
            },
        };

        if (DisplayConfigGetDeviceInfo(ref packet) != 0 || string.IsNullOrEmpty(packet.ViewGdiDeviceName))
        {
            return false;
        }

        deviceName = packet.ViewGdiDeviceName;
        return true;
    }

    private static bool TryGetAdvancedColorEnabled(DISPLAYCONFIG_PATH_INFO path, out bool enabled)
    {
        enabled = false;
        var packet = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            Header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                Type = GetAdvancedColorInfoType,
                Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                AdapterId = path.TargetInfo.AdapterId,
                Id = path.TargetInfo.Id,
            },
        };

        if (DisplayConfigGetDeviceInfo(ref packet) != 0)
        {
            return false;
        }

        enabled = (packet.Value & 0x2) != 0;
        return true;
    }

    private static bool TryGetSdrWhiteLevel(DISPLAYCONFIG_PATH_INFO path, out uint whiteLevel)
    {
        whiteLevel = HdrDisplayState.NominalSdrWhiteLevel;
        var packet = new DISPLAYCONFIG_SDR_WHITE_LEVEL
        {
            Header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                Type = GetSdrWhiteLevelType,
                Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SDR_WHITE_LEVEL>(),
                AdapterId = path.TargetInfo.AdapterId,
                Id = path.TargetInfo.Id,
            },
        };

        if (DisplayConfigGetDeviceInfo(ref packet) != 0 || packet.SDRWhiteLevel == 0)
        {
            return false;
        }

        whiteLevel = packet.SDRWhiteLevel;
        return true;
    }

    private static void PruneExpired(long now)
    {
        List<IntPtr> stale = null;
        foreach (KeyValuePair<IntPtr, CacheEntry> pair in Cache)
        {
            if (now - pair.Value.TimestampMs >= CacheTtlMs)
            {
                stale ??= new List<IntPtr>();
                stale.Add(pair.Key);
            }
        }

        if (stale == null)
        {
            return;
        }

        foreach (IntPtr key in stale)
        {
            Cache.Remove(key);
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SDR_WHITE_LEVEL requestPacket);

    private readonly struct CacheEntry
    {
        public CacheEntry(HdrDisplayState state, long timestampMs)
        {
            State = state;
            TimestampMs = timestampMs;
        }

        public HdrDisplayState State { get; }

        public long TimestampMs { get; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshRateNumerator;
        public uint RefreshRateDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO SourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_2DREGION
    {
        public uint Cx;
        public uint Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    {
        public ulong PixelRate;
        public DISPLAYCONFIG_RATIONAL HSyncFreq;
        public DISPLAYCONFIG_RATIONAL VSyncFreq;
        public DISPLAYCONFIG_2DREGION ActiveSize;
        public DISPLAYCONFIG_2DREGION TotalSize;
        public uint VideoStandard;
        public uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_TARGET_MODE
    {
        public DISPLAYCONFIG_VIDEO_SIGNAL_INFO TargetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)]
        public uint InfoType;

        [FieldOffset(4)]
        public uint Id;

        [FieldOffset(8)]
        public LUID AdapterId;

        [FieldOffset(16)]
        public DISPLAYCONFIG_TARGET_MODE TargetMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint Type;
        public uint Size;
        public LUID AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SDR_WHITE_LEVEL
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public uint SDRWhiteLevel;
    }
}
