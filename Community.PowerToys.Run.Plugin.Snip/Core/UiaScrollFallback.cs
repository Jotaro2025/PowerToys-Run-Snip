// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// UI Automation <see cref="ScrollPattern"/> fallback when SendInput / WM_MOUSEWHEEL
/// cannot move the underlay (some elevated or non-Chromium hosts).
/// Runs on a dedicated MTA worker: UIA COM marshals cross-process calls there while
/// its own internal message pump keeps the UI STA free (a dispatcher.Invoke here
/// used to freeze the UI for the duration of a slow provider call).
/// </summary>
internal static class UiaScrollFallback
{
    private static readonly Lazy<Thread> Worker = new Lazy<Thread>(StartWorker);

    private static readonly BlockingWorkQueue Queue = new BlockingWorkQueue();

    /// <summary>
    /// Tries to scroll the UI Automation element at a physical screen point.
    /// Positive <paramref name="delta"/> scrolls up (content moves down); negative scrolls down.
    /// Blocks the caller until the worker finishes (bounded by <see cref="WorkTimeout"/>).
    /// </summary>
    public static bool TryScrollAt(int physicalX, int physicalY, int delta)
    {
        if (delta == 0)
        {
            return false;
        }

        _ = Worker.Value;
        var done = new ManualResetEventSlim(false);
        bool result = false;
        if (!Queue.Enqueue(() => ScrollCore(physicalX, physicalY, delta), r =>
        {
            result = r;
            done.Set();
        }))
        {
            return false;
        }

        if (!done.Wait(WorkTimeout))
        {
            Log.Warn($"Snip scroll UIA worker timeout after {WorkTimeout.TotalMilliseconds:F0}ms", typeof(UiaScrollFallback));
            return false;
        }

        return result;
    }

    private static TimeSpan WorkTimeout => TimeSpan.FromSeconds(3);

    private static Thread StartWorker()
    {
        var thread = new Thread(WorkerLoop)
        {
            Name = "Snip UIA scroll (MTA)",
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return thread;
    }

    private static void WorkerLoop()
    {
        while (Queue.TryTake(out var work, Timeout.Infinite))
        {
            bool result = false;
            try
            {
                result = work.Invoke();
            }
            catch (Exception ex)
            {
                Log.Warn($"Snip scroll UIA worker: {ex.Message}", typeof(UiaScrollFallback));
            }
            finally
            {
                work.OnDone(result);
            }
        }
    }

    private static bool ScrollCore(int physicalX, int physicalY, int delta)
    {
        try
        {
            var point = new Point(physicalX, physicalY);
            AutomationElement element = AutomationElement.FromPoint(point);
            if (element == null)
            {
                return false;
            }

            ScrollPattern scroll = FindScrollPattern(element);
            if (scroll == null)
            {
                Log.Info(
                    $"Snip scroll UIA: no ScrollPattern at ({physicalX},{physicalY})",
                    typeof(UiaScrollFallback));
                return false;
            }

            bool vertical = scroll.Current.VerticallyScrollable;
            bool horizontal = scroll.Current.HorizontallyScrollable;
            if (!vertical && !horizontal)
            {
                return false;
            }

            // Match wheel: positive delta → scroll up (SmallDecrement on vertical).
            ScrollAmount amount = delta > 0 ? ScrollAmount.SmallDecrement : ScrollAmount.SmallIncrement;
            if (vertical)
            {
                scroll.Scroll(ScrollAmount.NoAmount, amount);
            }
            else
            {
                scroll.Scroll(amount, ScrollAmount.NoAmount);
            }

            Log.Info(
                $"Snip scroll UIA ScrollPattern at ({physicalX},{physicalY}) delta={delta} vertical={vertical}",
                typeof(UiaScrollFallback));
            return true;
        }
        catch (ElementNotAvailableException ex)
        {
            Log.Warn($"Snip scroll UIA element gone: {ex.Message}", typeof(UiaScrollFallback));
            return false;
        }
        catch (InvalidOperationException ex)
        {
            Log.Warn($"Snip scroll UIA invalid op: {ex.Message}", typeof(UiaScrollFallback));
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip scroll UIA failed: {ex.Message}", typeof(UiaScrollFallback));
            return false;
        }
    }

    private static ScrollPattern FindScrollPattern(AutomationElement start)
    {
        AutomationElement current = start;
        for (int depth = 0; depth < 12 && current != null; depth++)
        {
            if (current.TryGetCurrentPattern(ScrollPattern.Pattern, out object pattern)
                && pattern is ScrollPattern scroll)
            {
                return scroll;
            }

            try
            {
                current = TreeWalker.ControlViewWalker.GetParent(current);
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Serialized hand-off to the MTA worker: enqueue wakes it, TryTake blocks it
    /// until work arrives. Callers wait on their own reset event with a timeout.
    /// </summary>
    private sealed class BlockingWorkQueue
    {
        private readonly object _gate = new();
        private readonly Queue<(Func<bool> Invoke, Action<bool> Done)> _items = new();

        public bool Enqueue(Func<bool> invoke, Action<bool> onDone)
        {
            lock (_gate)
            {
                _items.Enqueue((invoke, onDone));
                Monitor.Pulse(_gate);
            }

            return true;
        }

        public bool TryTake(out (Func<bool> Invoke, Action<bool> OnDone) work, int millisecondsTimeout)
        {
            lock (_gate)
            {
                while (_items.Count == 0)
                {
                    if (!Monitor.Wait(_gate, millisecondsTimeout))
                    {
                        work = default;
                        return false;
                    }
                }

                var item = _items.Dequeue();
                work = (item.Invoke, item.Done);
                return true;
            }
        }
    }
}
