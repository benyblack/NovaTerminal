using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace NovaTerminal.Core
{
    public class GlobalHotkey : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 9000;

        // Modifiers: Alt = 1, Ctrl = 2, Shift = 4, Win = 8
        // VK_OEM_3 is Tilde (~) / Backtick (`) on US keyboards -> 0xC0 (192)

        private IntPtr _handle;
        private bool _registered;

        public event Action? OnHotkeyPressed;

        public GlobalHotkey(Window window)
        {
            if (OperatingSystem.IsWindows())
            {
                var platformHandle = window.TryGetPlatformHandle();
                if (platformHandle != null)
                {
                    _handle = platformHandle.Handle;
                }
            }
        }



        public void Unregister()
        {
            if (_handle == IntPtr.Zero || !_registered) return;

            UnregisterHotKey(_handle, HOTKEY_ID);
            _registered = false;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private WndProcDelegate? _wndProcDelegate;
        private IntPtr _oldWndProc;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const int GWLP_WNDPROC = -4;

        public void SetupHook()
        {
            if (_handle == IntPtr.Zero || !OperatingSystem.IsWindows()) return;

            _wndProcDelegate = WndProc; // Keep reference to prevent GC
            IntPtr newWndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
            _oldWndProc = SetWindowLongPtr(_handle, GWLP_WNDPROC, newWndProcPtr);
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                OnHotkeyPressed?.Invoke();
                return IntPtr.Zero; // Handled
            }

            return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        }

        // Modified Register to call SetupHook if needed
        public void Register(uint modifiers, uint key)
        {
            if (_handle == IntPtr.Zero || !OperatingSystem.IsWindows()) return;

            if (_oldWndProc == IntPtr.Zero) SetupHook();

            if (_registered) Unregister();

            // Alt + ~ (0xC0)
            _registered = RegisterHotKey(_handle, HOTKEY_ID, modifiers, key);
        }

        public void Dispose()
        {
            Unregister();
        }
    }
}
