using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FanqieReader.Services;

public enum EdgeSide { None, Left, Right, Top }

/// <summary>
/// Slides the window off the left/right/top screen edge and keeps a thin reveal
/// strip on screen.
///
/// Design notes (rewritten from the previous 40 ms / 34 px stepper):
///  * A top level window can only be moved with SetWindowPos, so the animation runs
///    on the HWND with an eased, time based curve instead of fixed steps.
///  * "Docked" means "close enough to the edge"; the window is snapped flush once the
///    drag ends, so the user no longer has to land on the exact pixel before hiding.
///  * Hiding is debounced: the pointer has to stay outside for a moment and is
///    checked again right before the slide starts.
///  * Every animation can be cancelled by a single call, so the window can never be
///    left half way off screen.
/// </summary>
public sealed class EdgeHideController : IDisposable
{
    private const int RevealStrip = 5;
    private const int DockTolerance = 26;
    private const int WakeMargin = 3;
    private const double HideDelayMs = 420;
    private const double SlideMs = 190;

    private readonly Window _window;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _slide;
    private readonly Stopwatch _slideClock = new();

    private bool _enabled;
    private bool _suppressed;
    private bool _dragging;
    private bool _hidden;
    private bool _animating;
    private bool _disposed;

    private EdgeSide _side = EdgeSide.None;
    private RECT _shown;
    private RECT _slideFrom;
    private RECT _slideTo;

    private bool _cursorInside;
    private long _leftAt;
    private bool _hidePending;

    public EdgeHideController(Window window)
    {
        _window = window;

        _poll = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(45)
        };
        _poll.Tick += (_, _) => Poll();

        _slide = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(15)
        };
        _slide.Tick += (_, _) => StepSlide();
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (value)
            {
                _leftAt = Now;
                _poll.Start();
            }
            else
            {
                RestoreNow();
                _side = EdgeSide.None;
                _poll.Stop();
            }
        }
    }

    /// <summary>Pauses edge hiding (used while the window is not in a normal state).</summary>
    public bool Suppressed
    {
        get => _suppressed;
        set
        {
            if (_suppressed == value) return;
            _suppressed = value;
            if (value)
            {
                _hidePending = false;
                RestoreNow();
            }
        }
    }

    public void Start()
    {
        if (_enabled && !_disposed)
        {
            _leftAt = Now;
            _poll.Start();
        }
    }

    public void Stop()
    {
        _hidePending = false;
        _poll.Stop();
    }

    public void NotifyDragStarted()
    {
        _dragging = true;
        _hidePending = false;
        _poll.Start();
    }

    public void NotifyDragEnded()
    {
        _dragging = false;
        _leftAt = Now;
        // Let the next poll snap the window flush to the nearest edge.
    }

    // ------------------------------------------------------------------
    // polling / state machine
    // ------------------------------------------------------------------

    private void Poll()
    {
        if (_disposed || !_enabled || _suppressed) return;
        if (_window.WindowState != WindowState.Normal || !_window.IsVisible) return;
        if (_animating) return;

        var handle = Handle;
        if (handle == IntPtr.Zero) return;
        if (!GetWindowRect(handle, out var rect) || rect.Width < 80 || rect.Height < 80) return;
        if (!GetCursorPos(out var cursor)) return;

        var work = GetWorkArea(handle, rect);

        if (_hidden)
        {
            // If something else moved the window (keyboard, OS snap, a second
            // monitor), the saved rectangle is stale: come back into view instead
            // of trying to "restore" a position that no longer exists.
            if (!IsSameRect(rect, _slideTo))
            {
                _hidden = false;
                _side = EdgeSide.None;
                _cursorInside = true;
                return;
            }

            if (IsCursorOnStrip(cursor, work))
            {
                SlideTo(_shown);
            }
            return;
        }

        // 1. Where would the window dock, if anywhere?
        var side = _dragging ? EdgeSide.None : DetectDock(rect, work);
        if (side != EdgeSide.None)
        {
            _side = side;
            // 2. Snap flush so the reveal strip is exactly on the screen edge.
            //    This is what makes "接近边缘" good enough instead of pixel perfect.
            if (!_dragging && !IsFlush(rect, work, side))
            {
                rect = SnapFlush(rect, work, side);
                SetWindowPos(handle, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
            }
            _shown = rect;
        }
        else if (!_dragging)
        {
            _side = EdgeSide.None;
            _hidePending = false;
        }

        // 3. Pointer bookkeeping for the debounced hide.
        var inside = cursor.X >= rect.Left - 1 && cursor.X <= rect.Right + 1
                     && cursor.Y >= rect.Top - 1 && cursor.Y <= rect.Bottom + 1;

        if (inside)
        {
            _cursorInside = true;
            _hidePending = false;
            _shown = rect;
            return;
        }

        if (_cursorInside)
        {
            _cursorInside = false;
            _leftAt = Now;
        }

        // The pointer may already have been outside when the window was dropped on
        // the edge; that still counts, otherwise the first hide never happened.
        _hidePending = _side != EdgeSide.None;
        if (!_hidePending) return;
        if (_dragging || Now - _leftAt < HideDelayMs) return;

        // 4. Second look right before moving: the pointer may have come back.
        if (!GetCursorPos(out var recheck)) return;
        if (recheck.X >= rect.Left - 8 && recheck.X <= rect.Right + 8
            && recheck.Y >= rect.Top - 8 && recheck.Y <= rect.Bottom + 8)
        {
            _cursorInside = true;
            _hidePending = false;
            return;
        }

        _hidePending = false;
        HideToEdge(rect, work, _side);
    }

    private void HideToEdge(RECT rect, RECT work, EdgeSide side)
    {
        _shown = rect;
        var target = side switch
        {
            EdgeSide.Left => new RECT
            {
                Left = work.Left - rect.Width + RevealStrip,
                Top = rect.Top,
                Right = work.Left + RevealStrip,
                Bottom = rect.Bottom
            },
            EdgeSide.Right => new RECT
            {
                Left = work.Right - RevealStrip,
                Top = rect.Top,
                Right = work.Right + rect.Width - RevealStrip,
                Bottom = rect.Bottom
            },
            EdgeSide.Top => new RECT
            {
                Left = rect.Left,
                Top = work.Top - rect.Height + RevealStrip,
                Right = rect.Right,
                Bottom = work.Top + RevealStrip
            },
            _ => rect
        };

        SlideTo(target);
    }

    // ------------------------------------------------------------------
    // animation
    // ------------------------------------------------------------------

    private void SlideTo(RECT target)
    {
        var handle = Handle;
        if (handle == IntPtr.Zero) return;
        if (!GetWindowRect(handle, out var current)) return;

        _slideFrom = current;
        _slideTo = target;

        if (current.Left == target.Left && current.Top == target.Top
            && current.Width == target.Width && current.Height == target.Height)
        {
            FinishSlide();
            return;
        }

        _animating = true;
        _hidePending = false;
        _slideClock.Restart();
        _slide.Start();
    }

    private void StepSlide()
    {
        if (!_animating)
        {
            _slide.Stop();
            return;
        }

        var handle = Handle;
        if (handle == IntPtr.Zero)
        {
            _animating = false;
            _slide.Stop();
            return;
        }

        var t = Math.Clamp(_slideClock.Elapsed.TotalMilliseconds / SlideMs, 0.0, 1.0);
        var eased = EaseOutCubic(t);

        var x = Lerp(_slideFrom.Left, _slideTo.Left, eased);
        var y = Lerp(_slideFrom.Top, _slideTo.Top, eased);
        var w = Lerp(_slideFrom.Width, _slideTo.Width, eased);
        var h = Lerp(_slideFrom.Height, _slideTo.Height, eased);

        SetWindowPos(handle, IntPtr.Zero, x, y, Math.Max(80, w), Math.Max(80, h),
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING);

        if (t >= 1.0) FinishSlide();
    }

    private void FinishSlide()
    {
        _animating = false;
        _slide.Stop();

        var handle = Handle;
        if (handle != IntPtr.Zero)
        {
            // Land exactly on the target; rounding during the slide can leave a
            // one pixel gap that made the old implementation look "stuck".
            SetWindowPos(handle, IntPtr.Zero, _slideTo.Left, _slideTo.Top, _slideTo.Width, _slideTo.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
        }

        _hidden = !IsSameRect(_slideTo, _shown);
        if (!_hidden)
        {
            _side = EdgeSide.None;
            _leftAt = Now;
            _cursorInside = true;
        }
    }

    /// <summary>Cancels any slide and puts the window back at its visible rectangle.</summary>
    public void RestoreNow()
    {
        _animating = false;
        _slide.Stop();
        _hidePending = false;

        var handle = Handle;
        if (handle == IntPtr.Zero) return;
        if (!_hidden && !GetWindowRect(handle, out var rect)) return;
        if (_shown.Width < 80 || _shown.Height < 80) return;

        if (_hidden)
        {
            SetWindowPos(handle, IntPtr.Zero, _shown.Left, _shown.Top, _shown.Width, _shown.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
        }

        _hidden = false;
        _cursorInside = true;
        _leftAt = Now;
    }

    public void Dispose()
    {
        _disposed = true;
        _animating = false;
        _slide.Stop();
        _poll.Stop();
    }

    // ------------------------------------------------------------------
    // geometry helpers
    // ------------------------------------------------------------------

    private static double EaseOutCubic(double t)
    {
        var inverse = 1 - t;
        return 1 - inverse * inverse * inverse;
    }

    private static int Lerp(int from, int to, double t) =>
        (int)Math.Round(from + (to - from) * t, MidpointRounding.AwayFromZero);

    private static bool IsSameRect(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Width == b.Width && a.Height == b.Height;

    private static EdgeSide DetectDock(RECT rect, RECT work)
    {
        if (Math.Abs(rect.Left - work.Left) <= DockTolerance) return EdgeSide.Left;
        if (Math.Abs(rect.Right - work.Right) <= DockTolerance) return EdgeSide.Right;
        if (Math.Abs(rect.Top - work.Top) <= DockTolerance) return EdgeSide.Top;
        return EdgeSide.None;
    }

    private static bool IsFlush(RECT rect, RECT work, EdgeSide side) => side switch
    {
        EdgeSide.Left => rect.Left == work.Left,
        EdgeSide.Right => rect.Right == work.Right,
        EdgeSide.Top => rect.Top == work.Top,
        _ => false
    };

    private static RECT SnapFlush(RECT rect, RECT work, EdgeSide side) => side switch
    {
        EdgeSide.Left => new RECT { Left = work.Left, Top = rect.Top, Right = work.Left + rect.Width, Bottom = rect.Bottom },
        EdgeSide.Right => new RECT { Left = work.Right - rect.Width, Top = rect.Top, Right = work.Right, Bottom = rect.Bottom },
        EdgeSide.Top => new RECT { Left = rect.Left, Top = work.Top, Right = rect.Right, Bottom = work.Top + rect.Height },
        _ => rect
    };

    private bool IsCursorOnStrip(POINT cursor, RECT work)
    {
        var spanTop = _shown.Top;
        var spanBottom = _shown.Bottom;

        return _side switch
        {
            EdgeSide.Left => cursor.X >= work.Left - 1
                             && cursor.X <= work.Left + RevealStrip + WakeMargin
                             && cursor.Y >= spanTop && cursor.Y <= spanBottom,
            EdgeSide.Right => cursor.X >= work.Right - RevealStrip - WakeMargin
                              && cursor.X <= work.Right + 1
                              && cursor.Y >= spanTop && cursor.Y <= spanBottom,
            EdgeSide.Top => cursor.Y >= work.Top - 1
                            && cursor.Y <= work.Top + RevealStrip + WakeMargin
                            && cursor.X >= _shown.Left && cursor.X <= _shown.Right,
            _ => false
        };
    }

    private long Now => Environment.TickCount64;

    private IntPtr Handle => new WindowInteropHelper(_window).Handle;

    private static RECT GetWorkArea(IntPtr handle, RECT windowRect)
    {
        // Use the monitor the reader window is on (multi monitor / mixed DPI safe).
        var monitor = handle == IntPtr.Zero
            ? IntPtr.Zero
            : MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);

        if (monitor == IntPtr.Zero)
        {
            var center = new POINT
            {
                X = (windowRect.Left + windowRect.Right) / 2,
                Y = (windowRect.Top + windowRect.Bottom) / 2
            };
            monitor = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref info)) return info.rcWork;

        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    // ------------------------------------------------------------------
    // native
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOSENDCHANGING = 0x0400;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
