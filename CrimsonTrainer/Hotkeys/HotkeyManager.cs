using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CrimsonTrainer.Native;

namespace CrimsonTrainer.Hotkeys;

/// <summary>System-wide hotkeys (RegisterHotKey) delivered through the main window's message loop.</summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly nint _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public HotkeyManager(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd) ?? throw new InvalidOperationException("Window has no HwndSource yet.");
        _source.AddHook(WndProc);
    }

    /// <summary>Returns the registration id, or null (with a message) when Windows refuses the combination.</summary>
    public int? Register(Hotkey hotkey, Action action, out string? error)
    {
        uint mods = User32.MOD_NOREPEAT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Control)) mods |= User32.MOD_CONTROL;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= User32.MOD_ALT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= User32.MOD_SHIFT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= User32.MOD_WIN;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);

        int id = _nextId++;
        if (!User32.RegisterHotKey(_hwnd, id, mods, vk))
        {
            int code = Marshal.GetLastPInvokeError();
            error = code == ErrorHotkeyAlreadyRegistered
                ? $"{hotkey} is already taken by another program."
                : new Win32Exception(code).Message;
            return null;
        }
        _actions[id] = action;
        error = null;
        return id;
    }

    public void Unregister(int id)
    {
        if (_actions.Remove(id)) User32.UnregisterHotKey(_hwnd, id);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == User32.WM_HOTKEY && _actions.TryGetValue((int)wParam, out var action))
        {
            action();
            handled = true;
        }
        return 0;
    }

    public void Dispose()
    {
        foreach (int id in _actions.Keys.ToList()) User32.UnregisterHotKey(_hwnd, id);
        _actions.Clear();
        _source.RemoveHook(WndProc);
    }
}
