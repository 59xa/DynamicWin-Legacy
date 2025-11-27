using SkiaSharp;
using System;
using System.Collections.Concurrent;

namespace DynamicWin.Utils
{
    /// <summary>
    /// Simple GPU texture cache keyed by string
    /// Stores SKImage instances created from GPU surfaces so they can be reused without recreating GPU resources
    /// Thread-affine: SKImage/GRContext are bound to the thread where they were created; ensure access is from UI thread
    /// </summary>
    public static class GPUTextureCache
    {
        private static ConcurrentDictionary<string, SKImage> cache = new ConcurrentDictionary<string, SKImage>();

        public static SKImage? GetOrCreate(string key, Func<SKImage?> create)
        {
            if (cache.TryGetValue(key, out var img))
                return img;

            var newImg = create();
            if (newImg != null)
            {
                cache[key] = newImg;
            }

            return newImg;
        }

        public static void Remove(string key)
        {
            if (cache.TryRemove(key, out var img))
            {
                try { img.Dispose(); } catch { }
            }
        }

        public static void Clear()
        {
            foreach (var kv in cache)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            cache.Clear();
        }
    }
}
