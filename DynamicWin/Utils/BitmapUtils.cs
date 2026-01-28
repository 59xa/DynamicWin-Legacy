using SkiaSharp;
using System;

namespace DynamicWin.Utils
{
    public static class BitmapUtils
    {
        /// <summary>
        /// Computes a simple fingerprint for a bitmap by combining width, height, and a hash of sampled pixel data.
        /// This method is efficient and does not dispose or modify the bitmap.
        /// </summary>
        public static ulong? GetBitmapFingerprint(SKBitmap bmp)
        {
            if (bmp == null) return null;
            unchecked
            {
                ulong hash = (ulong)bmp.Width * 397UL ^ (ulong)bmp.Height;
                if (bmp.BytesPerPixel > 0 && bmp.ByteCount > 0)
                {
                    var span = bmp.GetPixelSpan();
                    for (int i = 0; i < span.Length; i += Math.Max(1, span.Length / 32)) // sample up to 32 bytes
                    {
                        hash = (hash * 31UL) ^ span[i];
                    }
                }
                return hash;
            }
        }
    }
}
