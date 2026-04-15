using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DynamicWin.Utils
{
    public static class AppBarHelper
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uCallbackMessage;
            public int uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        private enum ABMsg : int
        {
            ABM_NEW = 0,
            ABM_REMOVE = 1,
            ABM_QUERYPOS = 2,
            ABM_SETPOS = 3,
            ABM_GETSTATE = 4,
            ABM_GETTASKBARPOS = 5,
            ABM_ACTIVATE = 6,
            ABM_GETAUTOHIDEBAR = 7,
            ABM_SETAUTOHIDEBAR = 8,
            ABM_WINDOWPOSCHANGED = 9,
            ABM_SETSTATE = 10
        }

        private enum ABEdge : int
        {
            ABE_LEFT = 0,
            ABE_TOP = 1,
            ABE_RIGHT = 2,
            ABE_BOTTOM = 3
        }

        [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr SHAppBarMessage(int dwMessage, ref APPBARDATA pData);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        private static bool _isRegistered = false;

        private static IntPtr _lastRegisteredHWnd = IntPtr.Zero;

        public static void RegisterAppBar(Window window, int height)
        {
            if (_isRegistered) UnregisterAppBar(window);

            var helper = new WindowInteropHelper(window);
            IntPtr hWnd = helper.Handle;
            _lastRegisteredHWnd = hWnd;

            // Get the monitor where the window is located
            var screen = System.Windows.Forms.Screen.FromHandle(hWnd);

            APPBARDATA abd = new APPBARDATA();
            abd.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            abd.hWnd = hWnd;
            abd.uCallbackMessage = 0x8000 + 101; // Custom message

            SHAppBarMessage((int)ABMsg.ABM_NEW, ref abd);
            _isRegistered = true;

            abd.rc.top = screen.Bounds.Top;
            abd.rc.left = screen.Bounds.Left;
            abd.rc.right = screen.Bounds.Right;
            abd.rc.bottom = screen.Bounds.Top + height;
            abd.uEdge = (int)ABEdge.ABE_TOP;

            SHAppBarMessage((int)ABMsg.ABM_QUERYPOS, ref abd);
            SHAppBarMessage((int)ABMsg.ABM_SETPOS, ref abd);
        }

        public static void UnregisterAppBar(Window window)
        {
            var helper = new WindowInteropHelper(window);
            UnregisterAppBar(helper.Handle);
        }

        public static void UnregisterAppBar(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;

            APPBARDATA abd = new APPBARDATA();
            abd.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            abd.hWnd = hWnd;

            SHAppBarMessage((int)ABMsg.ABM_REMOVE, ref abd);
            _isRegistered = false;
            
            if (hWnd == _lastRegisteredHWnd)
                _lastRegisteredHWnd = IntPtr.Zero;
        }

        public static void ForceUnregisterLast()
        {
            if (_lastRegisteredHWnd != IntPtr.Zero)
            {
                UnregisterAppBar(_lastRegisteredHWnd);
            }
        }

        public static void SetAppBarPos(Window window, int height)
        {
            if (!_isRegistered) return;

            var helper = new WindowInteropHelper(window);
            APPBARDATA abd = new APPBARDATA();
            abd.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            abd.hWnd = helper.Handle;
            abd.uEdge = (int)ABEdge.ABE_TOP;

            // Get the screen bounds
            var screen = System.Windows.Forms.Screen.FromHandle(helper.Handle);
            
            abd.rc.left = screen.Bounds.Left;
            abd.rc.right = screen.Bounds.Right;
            abd.rc.top = screen.Bounds.Top;
            abd.rc.bottom = screen.Bounds.Top + height;

            // Query the system for approval
            SHAppBarMessage((int)ABMsg.ABM_QUERYPOS, ref abd);

            // Set the position
            SHAppBarMessage((int)ABMsg.ABM_SETPOS, ref abd);
        }
    }
}
