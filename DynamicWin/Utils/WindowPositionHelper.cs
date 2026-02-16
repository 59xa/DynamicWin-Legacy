using System;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;

namespace DynamicWin.Utils
{
    public static class WindowPositionHelper
    {
        public static void CenterWindowOnMonitor(Window window, int monitorIndex)
        {
            if (window == null) return;
            var screens = Screen.AllScreens;
            int clampedIndex = Math.Clamp(monitorIndex, 0, screens.Length - 1);
            var screen = screens[clampedIndex];
            var bounds = screen.Bounds;

            // Get DPI scaling for the target monitor
            double dpiX = 96.0, dpiY = 96.0;
            var source = PresentationSource.FromVisual(window);
            if (source != null)
            {
                dpiX = source.CompositionTarget.TransformToDevice.M11 * 96.0;
                dpiY = source.CompositionTarget.TransformToDevice.M22 * 96.0;
            }
            else
            {
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                {
                    dpiX = g.DpiX;
                    dpiY = g.DpiY;
                }
            }

            double scaleX = dpiX / 96.0;
            double scaleY = dpiY / 96.0;

            // Aggressively place window at the very top and full width of the physical screen (ignoring taskbar)
            var screenBounds = screen.Bounds;
            window.Left = screenBounds.Left / scaleX;
            window.Top = screenBounds.Top / scaleY;
            window.Width = screenBounds.Width / scaleX;
            window.Height = screenBounds.Height / scaleY;
        }
    }
}
