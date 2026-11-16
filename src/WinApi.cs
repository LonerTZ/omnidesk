using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace OmniDesk
{
    // Window enumeration and placement used by the multi-monitor modes.
    static class WinApi
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        const uint GW_OWNER = 4;
        const int GWL_EXSTYLE = -20;
        const long WS_EX_TOOLWINDOW = 0x80;
        const long WS_EX_APPWINDOW = 0x40000;
        const int DWMWA_CLOAKED = 14;
        const uint SWP_NOZORDER = 0x4;
        const uint SWP_NOACTIVATE = 0x10;
        const int SW_MAXIMIZE = 3;
        const int SW_RESTORE = 9;

        static readonly string[] ShellClasses =
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
            "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow", "Windows.UI.Core.CoreWindow"
        };

        public static List<IntPtr> TopLevel()
        {
            var list = new List<IntPtr>();
            EnumWindows(delegate(IntPtr h, IntPtr l) { list.Add(h); return true; }, IntPtr.Zero);
            return list;
        }

        // Roughly the windows that appear in Alt+Tab: visible, unowned, not a tool window, titled, not part of the shell.
        public static bool Eligible(IntPtr hwnd, int ownProcessId)
        {
            if (!IsWindowVisible(hwnd)) return false;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;

            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
            if (GetWindowTextLength(hwnd) == 0) return false;

            // Windows on another desktop are cloaked by the shell (2). Anything else cloaked is hidden by its own app.
            int cloaked;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0 && cloaked != 2) return false;

            var cls = new StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            string c = cls.ToString();
            foreach (string s in ShellClasses) if (c == s) return false;

            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if ((int)pid == ownProcessId) return false;
            return true;
        }

        public static bool IsMinimized(IntPtr hwnd) { return IsIconic(hwnd); }
        public static bool IsMaximized(IntPtr hwnd) { return IsZoomed(hwnd); }

        public static Rectangle Rect(IntPtr hwnd)
        {
            RECT r;
            if (!GetWindowRect(hwnd, out r)) return Rectangle.Empty;
            return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        public static void Place(IntPtr hwnd, Rectangle r, bool maximize)
        {
            if (maximize)
            {
                // A maximised window only moves to another monitor if it is restored first.
                ShowWindow(hwnd, SW_RESTORE);
                SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE);
                ShowWindow(hwnd, SW_MAXIMIZE);
            }
            else
            {
                SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }
    }
}
