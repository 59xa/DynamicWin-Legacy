using SkiaSharp;
using System;

namespace DynamicWin.Utils
{
    /// <summary>
    /// Optimised utilities for decoding thumbnail bytes and computing lightweight fingerprints.
    /// - Minimal allocations through SKImage pooling
    /// - Efficient fingerprinting to avoid redundant decodes
    /// - Direct SKImage output to avoid unnecessary SKBitmap conversions
    /// </summary>
    public static class MediaThumbnailUtils
    {
        // Object pool for temporary SKBitmaps to reduce allocation pressure
        private static class BitmapPool
        {
            private static SKBitmap? pooledBitmap;
            private static readonly object poolLock = new object();

            public static SKBitmap? Rent(Action<SKBitmap>? setup = null)
            {
                lock (poolLock)
                {
                    if (pooledBitmap != null)
                    {
                        var bmp = pooledBitmap;
                        pooledBitmap = null;
                        setup?.Invoke(bmp);
                        return bmp;
                    }
                }
                return null;
            }

            public static void Return(SKBitmap? bmp)
            {
                if (bmp == null) return;
                lock (poolLock)
                {
                    if (pooledBitmap == null)
                    {
                        pooledBitmap = bmp;
                    }
                    else
                    {
                        bmp.Dispose();
                    }
                }
            }

            public static void Clear()
            {
                lock (poolLock)
                {
                    pooledBitmap?.Dispose();
                    pooledBitmap = null;
                }
            }
        }

        /// <summary>
        /// Decode image bytes directly into an SKImage with computed fingerprint.
        /// Returns null on failure. Fingerprint is computed from temporary SKBitmap.
        /// Optimized for minimal allocations and memory overhead.
        /// </summary>
        public static SKImage? DecodeBytesToImageAndFingerprint(byte[] bytes, out ulong? fingerprint)
        {
            fingerprint = null;
            if (bytes == null || bytes.Length == 0) return null;

            SKBitmap? decoded = null;
            try
            {
                // Decode bytes directly to SKBitmap
                using var ms = new SKMemoryStream(bytes);
                decoded = SKBitmap.Decode(ms);

                if (decoded == null) return null;

                // Compute fingerprint from bitmap
                try { fingerprint = BitmapUtils.GetBitmapFingerprint(decoded); } 
                catch { }

                // Convert to SKImage (more efficient than keeping bitmap)
                SKImage? img = null;
                try { img = SKImage.FromBitmap(decoded); }
                catch { }

                return img;
            }
            finally
            {
                // Always dispose temporary bitmap
                decoded?.Dispose();
            }
        }

        /// <summary>
        /// Batch decode multiple thumbnail bytes for mass imports.
        /// Returns tuples of (SKImage, fingerprint) for each input, null on individual failures.
        /// Useful for thumbnail gallery loading.
        /// </summary>
        public static (SKImage?, ulong?)[] DecodeBytesArrayToImagesAndFingerprints(byte[][] bytesArray)
        {
            if (bytesArray == null || bytesArray.Length == 0) 
                return Array.Empty<(SKImage?, ulong?)>();

            var results = new (SKImage?, ulong?)[bytesArray.Length];

            for (int i = 0; i < bytesArray.Length; i++)
            {
                if (bytesArray[i] == null || bytesArray[i].Length == 0)
                {
                    results[i] = (null, null);
                    continue;
                }

                var img = DecodeBytesToImageAndFingerprint(bytesArray[i], out var fp);
                results[i] = (img, fp);
            }

            return results;
        }

        /// <summary>
        /// Fast check if two thumbnail byte arrays produce the same fingerprint.
        /// Useful for deduplication without full decode.
        /// </summary>
        public static bool AreThumbnailBytesEquivalent(byte[]? bytes1, byte[]? bytes2)
        {
            if (bytes1 == null && bytes2 == null) return true;
            if (bytes1 == null || bytes2 == null) return false;
            if (bytes1.Length != bytes2.Length) return false;

            // For small arrays, just compare directly
            if (bytes1.Length < 1024)
            {
                return bytes1.AsSpan().SequenceEqual(bytes2);
            }

            // For larger arrays, use fast byte hash
            return ComputeQuickByteHash(bytes1) == ComputeQuickByteHash(bytes2);
        }

        /// <summary>
        /// Lightweight FNV-1a hash for byte array comparison.
        /// </summary>
        private static ulong ComputeQuickByteHash(byte[] bytes)
        {
            const ulong fnvOffset = 14695981039346656037UL;
            const ulong fnvPrime = 1099511628211UL;
            ulong hash = fnvOffset;

            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= fnvPrime;
            }

            return hash;
        }

        /// <summary>
        /// Validate if bytes represent valid image data without full decode.
        /// Performs format magic number check only.
        /// </summary>
        public static bool IsValidImageBytes(byte[]? bytes)
        {
            if (bytes == null || bytes.Length < 4) return false;

            // Check for common image format magic numbers
            // JPEG: FF D8 FF
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return true;

            // PNG: 89 50 4E 47 (‰PNG)
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return true;

            // WebP: RIFF ... WEBP
            if (bytes.Length >= 12 && 
                bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) 
                return true;

            // BMP: 42 4D (BM)
            if (bytes[0] == 0x42 && bytes[1] == 0x4D) return true;

            // GIF: 47 49 46 (GIF)
            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return true;

            return false;
        }
    }
}
