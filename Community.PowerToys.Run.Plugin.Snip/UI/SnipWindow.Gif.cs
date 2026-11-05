// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

public sealed partial class SnipWindow
{
    private GifRecordingSession _gifSession;
    private ScrollCaptureStopGuard _gifStopGuard;
    private bool _gifSaving;

    private void OnRecordGifToggled(object sender, EventArgs e)
    {
        if (_disposed || _gifSaving)
        {
            return;
        }

        try
        {
            if (_gifSession != null)
            {
                _ = StopAndSaveGifAsync();
            }
            else if (_hasSelection && _scrollController == null)
            {
                StartGifRecording();
            }
        }
        catch (Exception ex)
        {
            CancelGifSessionDiscard();
            Log.Exception($"SnipWindow GIF toggle failed: {ex.Message}", ex, GetType());
        }
    }

    private void StartGifRecording()
    {
        var physicalRect = DpiCoordinateMapping.DipSelectionToPhysical(_selection, _logicalSize, _physicalBounds);
        var session = new GifRecordingSession(physicalRect, _captureService);
        _gifSession = session;
        _gifStopGuard = new ScrollCaptureStopGuard(
            () => _ = Dispatcher.InvokeAsync(() => _ = StopAndSaveGifAsync()),
            physicalRect,
            _dpiScale,
            Dispatcher,
            isGifRecording: true);
        _gifStopGuard.Start();
        _toolbar.SetGifState(recording: true, saving: false);
        Win32CaptureService.BeginScrollSessionOverlayHide(TryGetHwnd());
        session.Start();
        _ = SaveCompletedGifAsync(session);
    }

    private async Task SaveCompletedGifAsync(GifRecordingSession session)
    {
        try
        {
            await session.Completion.ConfigureAwait(true);
            if (!_disposed && ReferenceEquals(_gifSession, session) && !_gifSaving)
            {
                await StopAndSaveGifAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip GIF completion failed: {ex.Message}", ex, GetType());
            if (ReferenceEquals(_gifSession, session))
            {
                CancelGifSessionDiscard();
            }
        }
    }

    private void RestoreGifOverlay()
    {
        _gifStopGuard?.Dispose();
        _gifStopGuard = null;
        Win32CaptureService.EndScrollSessionOverlayHide(TryGetHwnd());
    }

    private void CancelGifSessionDiscard()
    {
        var session = _gifSession;
        _gifSession = null;
        if (session != null)
        {
            session.Dispose();
            RestoreGifOverlay();
            if (!_disposed)
            {
                _toolbar.SetGifState(recording: false, saving: false);
            }
        }
    }

    private async Task StopAndSaveGifAsync()
    {
        var session = _gifSession;
        if (session == null || _gifSaving)
        {
            return;
        }

        _gifSaving = true;
        _toolbar.SetGifState(recording: false, saving: true);
        try
        {
            await session.StopCapturingAsync().ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (ReferenceEquals(_gifSession, session))
            {
                RestoreGifOverlay();
            }

            string dir = ScreenshotFileSaver.ResolveDirectory(_settings.SaveDirectory, SnipSettings.DefaultSaveFolder);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, GifRecordingSession.BuildFileName());
            bool saved = await session.StopAndSaveAsync(path).ConfigureAwait(true);
            if (!_disposed)
            {
                ShowOverlayMessageBox(
                    saved ? $"{Properties.Resources.gif_saved}: {path}" : Properties.Resources.gif_error,
                    Properties.Resources.tip_record_gif,
                    MessageBoxButton.OK,
                    saved ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            // Session closed while encoding.
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip GIF save failed: {ex.Message}", ex, GetType());
            if (!_disposed)
            {
                ShowOverlayMessageBox(
                    $"{Properties.Resources.gif_error}: {ex.Message}",
                    Properties.Resources.tip_record_gif,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            if (ReferenceEquals(_gifSession, session))
            {
                _gifSession = null;
                RestoreGifOverlay();
            }

            session.Dispose();
            _gifSaving = false;
            if (!_disposed)
            {
                _toolbar.SetGifState(recording: false, saving: false);
            }
        }
    }
}
