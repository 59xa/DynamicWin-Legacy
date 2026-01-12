using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicWin.Utils
{
    public class ContextMenuUtils
    {
        public static Image LoadMenuIcon(string path, int size = 16)
        {
            return new Image
            {
                Source = new BitmapImage(new Uri($"pack://application:,,,/{path}", UriKind.Absolute)),
                Width = size,
                Height = size,
                SnapsToDevicePixels = true
            };
        }

        public static System.Drawing.Bitmap LoadTrayBitmap(string path)
        {
            var uri = new Uri($"pack://application:,,,/{path}", UriKind.Absolute);

            var bmp = new BitmapImage(uri);
            bmp.Freeze();

            using var ms = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            encoder.Save(ms);

            ms.Position = 0;
            return new System.Drawing.Bitmap(ms);
        }
    }
}
