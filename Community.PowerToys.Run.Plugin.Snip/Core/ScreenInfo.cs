// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Describes one physical display, or the synthetic virtual-desktop entry that
/// covers the union of all monitors. Bounds are in virtual-desktop pixels.
/// </summary>
public sealed record ScreenInfo(
    IntPtr Handle,
    string DeviceName,
    Rect Bounds,
    Rect WorkingArea,
    double DpiScale,
    bool IsPrimary);
