// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Registers a process-wide Win32 hotkey via a message-only <see cref="HwndSource"/>.
/// Must be created / used on an STA thread with a dispatcher (plugin UI thread).
/// </summary>
internal sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0x534E49; // 'SNI'
    private const int WmHotkey = 0x0312;
    private static readonly IntPtr HwndMessage = new IntPtr(-3);

    private HwndSource _source;
    private Action _onHotkey;
    private bool _registered;
    private bool _disposed;
    private string _currentHotkey = string.Empty;

    /// <summary>Last successfully registered hotkey display string (empty = none).</summary>
    public string CurrentHotkey => _currentHotkey;

    /// <summary>
    /// Unregisters any previous hotkey, then registers <paramref name="hotkey"/>.
    /// Empty string disables the hotkey (success with no registration).
    /// </summary>
    public bool TryRegister(string hotkey, Action onFired, out string error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(onFired);

        UnregisterCore();
        _onHotkey = null;
        _currentHotkey = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(hotkey))
        {
            return true;
        }

        if (!HotkeyParser.TryParse(hotkey, out var modifiers, out var virtualKey, out error))
        {
            return false;
        }

        EnsureSource();
        _onHotkey = onFired;

        // MOD_NOREPEAT avoids key-repeat spam while held.
        var mods = modifiers | HotkeyParser.ModNoRepeat;
        if (!Win32.RegisterHotKey(_source.Handle, HotkeyId, mods, virtualKey))
        {
            var code = Marshal.GetLastWin32Error();
            error = $"RegisterHotKey failed (Win32 {code}). The combination may be in use.";
            Log.Warn($"Snip global hotkey register failed for '{hotkey}': {error}", typeof(GlobalHotkeyService));
            return false;
        }

        _registered = true;
        _currentHotkey = HotkeyParser.Normalize(hotkey);
        Log.Info($"Snip global hotkey registered: {_currentHotkey}", typeof(GlobalHotkeyService));
        return true;
    }

    public void Unregister()
    {
        if (_disposed)
        {
            return;
        }

        UnregisterCore();
        _onHotkey = null;
        _currentHotkey = string.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        UnregisterCore();
        _onHotkey = null;
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }

        _disposed = true;
    }

    private void EnsureSource()
    {
        if (_source != null)
        {
            return;
        }

        var parameters = new HwndSourceParameters("SnipGlobalHotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = HwndMessage,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private void UnregisterCore()
    {
        if (!_registered || _source == null)
        {
            _registered = false;
            return;
        }

        Win32.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            try
            {
                _onHotkey?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Exception($"Snip global hotkey handler failed: {ex.Message}", ex, typeof(GlobalHotkeyService));
            }
        }

        return IntPtr.Zero;
    }
}
