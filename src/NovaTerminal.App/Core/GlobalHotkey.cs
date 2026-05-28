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
        private bool _disposed;

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
            if (_oldWndProc != IntPtr.Zero) return; // already hooked

            // _wndProcDelegate must stay rooted for as long as the window's WNDPROC slot
            // points at its thunk. It is a field, so it lives as long as this instance does.
            // RemoveHook() restores the original WNDPROC *before* this instance (and the
            // delegate) can be collected — see the comment there for why that ordering is
            // load-bearing.
            _wndProcDelegate = WndProc;
            IntPtr newWndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
            _oldWndProc = SetWindowLongPtr(_handle, GWLP_WNDPROC, newWndProcPtr);
        }

        private void RemoveHook()
        {
            // Restore the original window procedure BEFORE dropping the managed delegate.
            //
            // Skipping this was a confirmed crash (ExecutionEngineException on the
            // GlobalHotkey.WndProc -> CallWindowProc frame, captured in a WER dump): on
            // window teardown the WNDPROC slot still pointed at our managed thunk, and once
            // this instance became collectable the GC freed _wndProcDelegate. The next window
            // message Win32 dispatched (WM_DESTROY / WM_NCDESTROY, or a stray WM_HOTKEY) then
            // called into freed memory and aborted the process with no managed exception.
            // Heavy terminal output (e.g. an agent streaming) supplies the GC pressure that
            // collects the delegate, which is why it surfaced as a random
            // "window vanishes instantly".
            if (_handle == IntPtr.Zero || _oldWndProc == IntPtr.Zero || !OperatingSystem.IsWindows())
            {
                return;
            }

            SetWindowLongPtr(_handle, GWLP_WNDPROC, _oldWndProc);
            _oldWndProc = IntPtr.Zero;
            _wndProcDelegate = null;
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                // An exception escaping a reverse-P/Invoke WndProc unwinds into native Win32
                // (DispatchMessage), which is undefined behavior and crashes the process.
                // Swallow handler exceptions at this boundary; toggling visibility is
                // best-effort and must never take the app down.
                try
                {
                    OnHotkeyPressed?.Invoke();
                }
                catch
                {
                    // Intentionally ignored — see comment above.
                }

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
            if (_disposed) return;
            _disposed = true;

            Unregister();
            RemoveHook();
        }
    }
}
