using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;

namespace DynamicWin.Utils
{
    public static class AppBarManager
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public int lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        private const uint ABM_NEW = 0x00000000;
        private const uint ABM_REMOVE = 0x00000001;
        private const uint ABM_QUERYPOS = 0x00000002;
        private const uint ABM_SETPOS = 0x00000003;
        private const uint ABE_TOP = 1;

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        private static bool _isRegistered = false;
        private static IntPtr _registeredHwnd = IntPtr.Zero;

        public static bool IsRegistered => _isRegistered;

        public static void Register(Window window, int screenIndex, int reserveHeight = 38)
        {
            if (_isRegistered) return;

            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                var screens = System.Windows.Forms.Screen.AllScreens;
                int idx = Math.Clamp(screenIndex, 0, screens.Length - 1);
                var bounds = screens[idx].Bounds;

                double dpiScale = GetDpiScale(window);
                int physicalHeight = (int)Math.Round(reserveHeight * dpiScale);

                var abd = new APPBARDATA
                {
                    cbSize = Marshal.SizeOf(typeof(APPBARDATA)),
                    hWnd = hwnd,
                    uEdge = ABE_TOP,
                    rc = new RECT
                    {
                        Left = bounds.Left,
                        Top = bounds.Top,
                        Right = bounds.Right,
                        Bottom = bounds.Top + physicalHeight
                    }
                };

                SHAppBarMessage(ABM_NEW, ref abd);
                SHAppBarMessage(ABM_QUERYPOS, ref abd);
                SHAppBarMessage(ABM_SETPOS, ref abd);

                _registeredHwnd = hwnd;
                _isRegistered = true;
            }
            catch { }
        }

        public static void Unregister()
        {
            if (!_isRegistered) return;

            try
            {
                var abd = new APPBARDATA
                {
                    cbSize = Marshal.SizeOf(typeof(APPBARDATA)),
                    hWnd = _registeredHwnd
                };

                SHAppBarMessage(ABM_REMOVE, ref abd);
            }
            catch { }
            finally
            {
                _isRegistered = false;
                _registeredHwnd = IntPtr.Zero;
            }
        }

        public static void Apply(Window window, int screenIndex, bool enable, int reserveHeight = 38)
        {
            if (enable)
                Register(window, screenIndex, reserveHeight);
            else
                Unregister();
        }

        private static double GetDpiScale(Visual visual)
        {
            try
            {
                var source = PresentationSource.FromVisual(visual);
                if (source != null)
                    return source.CompositionTarget.TransformToDevice.M11;
            }
            catch { }

            using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            return g.DpiX / 96.0;
        }
    }
}