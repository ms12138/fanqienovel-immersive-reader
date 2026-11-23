using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FanqieReader.Services;

public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkQ = 0x51;
    private const int HotkeyId = 0x4647;

    private readonly Window _window;
    private HwndSource? _source;
    private bool _registered;

    public event Action? Pressed;

    public HotkeyManager(Window window)
    {
        _window = window;
    }

    public bool Register()
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return false;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
        _registered = RegisterHotKey(handle, HotkeyId, ModAlt | ModShift | ModNoRepeat, VkQ);
        return _registered;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered)
        {
            try { UnregisterHotKey(new WindowInteropHelper(_window).Handle, HotkeyId); }
            catch { }
            _registered = false;
        }
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
