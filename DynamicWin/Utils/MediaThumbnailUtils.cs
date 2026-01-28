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
        [Obsolete("Use BitmapUtils.GetBitmapFingerprint instead.")]
        public static ulong ComputeFingerprint(SKBitmap bmp)
        {
            // Forward to BitmapUtils for consistency
            return BitmapUtils.GetBitmapFingerprint(bmp) ?? 0ul;
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

                try { fingerprint = BitmapUtils.GetBitmapFingerprint(bmp); } catch { fingerprint = null; }

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
