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

            double windowWidth = window is { ActualWidth: > 0 } ? window.ActualWidth : window.Width;

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

            double targetLeft = (bounds.Left + (bounds.Width - windowWidth * scaleX) / 2.0) / scaleX;
            double targetTop = bounds.Top / scaleY;

            window.Left = targetLeft;
            window.Top = targetTop;
        }
    }
}
