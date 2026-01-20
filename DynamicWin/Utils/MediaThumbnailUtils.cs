using SkiaSharp;
using System;

namespace DynamicWin.Utils
{
    /// <summary>
    /// Utilities for decoding thumbnail bytes and computing lightweight fingerprints for bitmaps.
    /// Extracted from MediaPlayer to allow reuse.
    /// </summary>
    public static class MediaThumbnailUtils
    {
        // Lightweight fingerprint for bitmap equality: sample a few pixels and dimensions
        public static ulong ComputeFingerprint(SKBitmap bmp)
        {
            if (bmp == null) return 0ul;
            unchecked
            {
                ulong h = 1469598103934665603UL; // FNV offset basis
                h ^= (ulong)bmp.Width; h *= 1099511628211UL;
                h ^= (ulong)bmp.Height; h *= 1099511628211UL;

                int w = Math.Max(1, bmp.Width);
                int hgt = Math.Max(1, bmp.Height);
                (int x, int y)[] samples = new (int, int)[] {
                    (0,0), (w-1,0), (0,hgt-1), (w-1,hgt-1),
                    (w/2, hgt/2), (w/2,0), (w/2,hgt-1), (0,hgt/2)
                };

                foreach (var s in samples)
                {
                    try
                    {
                        var c = bmp.GetPixel(Math.Max(0, Math.Min(s.x, w-1)), Math.Max(0, Math.Min(s.y, hgt-1)));
                        ulong val = ((ulong)c.Alpha << 24) | ((ulong)c.Red << 16) | ((ulong)c.Green << 8) | (ulong)c.Blue;
                        h ^= val; h *= 1099511628211UL;
                    }
                    catch
                    {
                        // ignore
                    }
                }

                return h;
            }
        }

        /// <summary>
        /// Decode image bytes into an owned SKImage and compute fingerprint from a temporary SKBitmap.
        /// Returns null on failure.
        /// </summary>
        public static SKImage? DecodeBytesToImageAndFingerprint(byte[] bytes, out ulong? fingerprint)
        {
            fingerprint = null;
            if (bytes == null || bytes.Length == 0) return null;

            try
            {
                using var ms = new SKMemoryStream(bytes);
                var bmp = SKBitmap.Decode(ms);
                if (bmp == null) return null;

                try { fingerprint = ComputeFingerprint(bmp); } catch { fingerprint = null; }

                SKImage? img = null;
                try
                {
                    img = SKImage.FromBitmap(bmp);
                }
                catch
                {
                    img = null;
                }

                try { bmp.Dispose(); } catch { }

                return img;
            }
            catch
            {
                return null;
            }
        }
    }
}
