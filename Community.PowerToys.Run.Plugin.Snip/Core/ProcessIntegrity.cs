// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Best-effort process integrity (UIPI) checks so scroll capture can tip the user
/// when the underlay is higher-IL than the plugin host (SendInput / UIA blocked).
/// </summary>
internal static class ProcessIntegrity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    // Well-known mandatory label RIDs (SID S-1-16-*).
    internal const int RidUntrusted = 0x0000;
    internal const int RidLow = 0x1000;
    internal const int RidMedium = 0x2000;
    internal const int RidMediumPlus = 0x2100;
    internal const int RidHigh = 0x3000;
    internal const int RidSystem = 0x4000;

    /// <summary>
    /// True when <paramref name="hwnd"/>'s process integrity is strictly higher than
    /// this process (or the target token cannot be opened — often UIPI). False when
    /// equal/lower, hwnd is zero, or the check fails for a non-elevation reason.
    /// </summary>
    public static bool IsHigherThanCurrent(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        _ = Win32.GetWindowThreadProcessId(hwnd, out uint targetPid);
        if (targetPid == 0)
        {
            return false;
        }

        uint selfPid;
        try
        {
            selfPid = (uint)Environment.ProcessId;
        }
        catch (Exception)
        {
            selfPid = 0;
        }

        if (targetPid == selfPid)
        {
            return false;
        }

        if (!TryGetIntegrityRid(targetPid, out int targetRid, out bool targetAccessDenied))
        {
            // ACCESS_DENIED opening a peer process is a common elevated-target signal.
            if (targetAccessDenied)
            {
                Log.Info(
                    $"Snip UIPI: cannot query target pid={targetPid} (access denied) — treating as higher integrity",
                    typeof(ProcessIntegrity));
                return true;
            }

            return false;
        }

        if (!TryGetIntegrityRid(selfPid == 0 ? (uint)Environment.ProcessId : selfPid, out int selfRid, out _))
        {
            return false;
        }

        if (targetRid > selfRid)
        {
            Log.Info(
                $"Snip UIPI: target pid={targetPid} rid=0x{targetRid:X} > self rid=0x{selfRid:X}",
                typeof(ProcessIntegrity));
            return true;
        }

        return false;
    }

    /// <summary>Read the mandatory integrity RID for <paramref name="pid"/>.</summary>
    internal static bool TryGetIntegrityRid(uint pid, out int rid, out bool accessDenied)
    {
        rid = 0;
        accessDenied = false;
        if (pid == 0)
        {
            return false;
        }

        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            accessDenied = err == 5; // ERROR_ACCESS_DENIED
            return false;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out IntPtr token) || token == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                accessDenied = err == 5;
                return false;
            }

            try
            {
                return TryReadIntegrityRid(token, out rid);
            }
            finally
            {
                _ = CloseHandle(token);
            }
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    private static bool TryReadIntegrityRid(IntPtr token, out int rid)
    {
        rid = 0;
        GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int needed);
        if (needed <= 0)
        {
            return false;
        }

        IntPtr buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _))
            {
                return false;
            }

            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label; } — Label.Sid is first pointer-sized field.
            IntPtr sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero || !IsValidSid(sid))
            {
                return false;
            }

            int subAuthCount = ReadSidSubAuthorityCount(sid);
            if (subAuthCount <= 0)
            {
                return false;
            }

            IntPtr subAuth = GetSidSubAuthority(sid, subAuthCount - 1);
            if (subAuth == IntPtr.Zero)
            {
                return false;
            }

            rid = Marshal.ReadInt32(subAuth);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(IntPtr pSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr pSid, int nSubAuthority);

    // Returns a pointer to a BYTE (sub-authority count).
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);

    private static int ReadSidSubAuthorityCount(IntPtr pSid)
    {
        IntPtr pCount = GetSidSubAuthorityCount(pSid);
        return pCount == IntPtr.Zero ? 0 : Marshal.ReadByte(pCount);
    }
}
