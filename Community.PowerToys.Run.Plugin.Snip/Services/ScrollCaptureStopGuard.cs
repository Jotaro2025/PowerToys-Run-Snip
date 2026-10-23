// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Community.PowerToys.Run.Plugin.Snip.UI;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Mid-scroll stop while the snip overlay is session-hidden for Edge:
/// global Esc/Space via message-only <see cref="HwndSource"/> + <c>GetAsyncKeyState</c>
/// poll, and a floating 「停止」strip that is never SW_HIDE'd with the overlay.
/// </summary>
internal sealed class ScrollCaptureStopGuard : IDisposable
{
    /// <summary>Distinct from <see cref="GlobalHotkeyService"/> hotkey id.</summary>
    private const int EscapeHotKeyId = 0x534E45; // 'SNE'

    private const int SpaceHotKeyId = 0x534E50; // 'SNP'

    private static readonly IntPtr HwndMessage = new IntPtr(-3);

    private readonly Action _onStop;
    private readonly Dispatcher _dispatcher;
    private readonly Int32Rect _capturePhysicalPx;
    private readonly double _dpiScale;

    private HwndSource _source;
    private ScrollStopStripWindow _strip;
    private bool _escRegistered;
    private bool _spaceRegistered;
    private bool _escWasDown;
    private bool _spaceWasDown;
    private int _stopFired;
    private bool _disposed;

    public ScrollCaptureStopGuard(
        Action onStop,
        Int32Rect capturePhysicalPx,
        double dpiScale,
        Dispatcher dispatcher)
    {
        _onStop = onStop ?? throw new ArgumentNullException(nameof(onStop));
        _capturePhysicalPx = capturePhysicalPx;
        _dpiScale = dpiScale > 0.01 ? dpiScale : 1.0;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Snapshot so a held Esc/Space from before Begin does not immediate-stop.
        _escWasDown = IsKeyDown(Win32.VkEscape);
        _spaceWasDown = IsKeyDown(Win32.VkSpace);

        EnsureSourceAndHotkeys();
        ShowStrip();
    }

    public void SetTileCount(int tileCount)
    {
        if (_disposed || _strip == null)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                try
                {
                    _strip?.SetTileCount(tileCount);
                }
                catch (Exception ex)
                {
                    SnipVerbose.Scroll($"[scroll] stop strip tile update skipped: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            SnipVerbose.Scroll($"[scroll] stop strip dispatch failed: {ex.Message}");
        }
    }

    public void SetUipiWarning(bool elevated)
    {
        if (_disposed || _strip == null)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                try
                {
                    _strip?.SetUipiWarning(elevated);
                }
                catch (Exception ex)
                {
                    SnipVerbose.Scroll($"[scroll] stop strip UIPI tip skipped: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            SnipVerbose.Scroll($"[scroll] stop strip UIPI dispatch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Poll Esc/Space (edge-triggered). Call from the scroll loop when RegisterHotKey
    /// may have failed or WM_HOTKEY delivery is delayed.
    /// </summary>
    public bool PollStopKeys()
    {
        if (_disposed || _stopFired != 0)
        {
            return _stopFired != 0;
        }

        bool escDown = IsKeyDown(Win32.VkEscape);
        bool spaceDown = IsKeyDown(Win32.VkSpace);
        bool edge = (escDown && !_escWasDown) || (spaceDown && !_spaceWasDown);
        _escWasDown = escDown;
        _spaceWasDown = spaceDown;
        if (edge)
        {
            FireStop("GetAsyncKeyState");
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterHotkeys();
        if (_source != null)
        {
            try
            {
                _source.RemoveHook(WndProc);
                _source.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"Snip scroll stop guard HwndSource dispose: {ex.Message}", typeof(ScrollCaptureStopGuard));
            }

            _source = null;
        }

        CloseStrip();
    }

    private void EnsureSourceAndHotkeys()
    {
        if (_source == null)
        {
            var parameters = new HwndSourceParameters("SnipScrollStopHotkey")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ParentWindow = HwndMessage,
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        }

        // Bare Esc/Space (MOD_NOREPEAT). May fail if another app owns the combo.
        _escRegistered = Win32.RegisterHotKey(
            _source.Handle,
            EscapeHotKeyId,
            HotkeyParser.ModNoRepeat,
            unchecked((uint)Win32.VkEscape));
        if (!_escRegistered)
        {
            Log.Warn(
                $"Snip scroll Esc hotkey register failed (Win32 {Marshal.GetLastWin32Error()}); poll fallback active",
                typeof(ScrollCaptureStopGuard));
        }

        _spaceRegistered = Win32.RegisterHotKey(
            _source.Handle,
            SpaceHotKeyId,
            HotkeyParser.ModNoRepeat,
            unchecked((uint)Win32.VkSpace));
        if (!_spaceRegistered)
        {
            Log.Warn(
                $"Snip scroll Space hotkey register failed (Win32 {Marshal.GetLastWin32Error()}); poll fallback active",
                typeof(ScrollCaptureStopGuard));
        }
    }

    private void UnregisterHotkeys()
    {
        if (_source == null)
        {
            return;
        }

        if (_escRegistered)
        {
            _ = Win32.UnregisterHotKey(_source.Handle, EscapeHotKeyId);
            _escRegistered = false;
        }

        if (_spaceRegistered)
        {
            _ = Win32.UnregisterHotKey(_source.Handle, SpaceHotKeyId);
            _spaceRegistered = false;
        }
    }

    private void ShowStrip()
    {
        _dispatcher.Invoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            _strip = new ScrollStopStripWindow();
            _strip.StopClicked += () => FireStop("stop-strip");
            _strip.PlaceNearCapture(_capturePhysicalPx, _dpiScale);
            _strip.Show();
        });
    }

    private void CloseStrip()
    {
        try
        {
            _dispatcher.Invoke(() =>
            {
                _strip?.SafeClose();
                _strip = null;
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip scroll stop strip close failed: {ex.Message}", typeof(ScrollCaptureStopGuard));
            _strip = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WmHotkey)
        {
            int id = wParam.ToInt32();
            if (id == EscapeHotKeyId || id == SpaceHotKeyId)
            {
                handled = true;
                FireStop(id == EscapeHotKeyId ? "Esc-hotkey" : "Space-hotkey");
            }
        }

        return IntPtr.Zero;
    }

    private void FireStop(string reason)
    {
        if (System.Threading.Interlocked.Exchange(ref _stopFired, 1) != 0)
        {
            return;
        }

        SnipVerbose.Scroll($"[scroll] mid-stop requested via {reason}");
        try
        {
            _onStop();
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip scroll stop guard callback failed: {ex.Message}", ex, typeof(ScrollCaptureStopGuard));
        }
    }

    internal static bool IsKeyDown(int virtualKey)
        => (Win32.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
