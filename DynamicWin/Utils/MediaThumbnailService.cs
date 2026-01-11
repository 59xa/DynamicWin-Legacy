using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace DynamicWin.Utils
{
    public class MediaChangedEventArgs : EventArgs
    {
        public Media? Media { get; }
        public byte[]? ThumbnailBytes { get; }

        public MediaChangedEventArgs(Media? media, byte[]? thumbnailBytes)
        {
            Media = media;
            ThumbnailBytes = thumbnailBytes;
        }
    }

    /// <summary>
    /// Centralised thumbnail fetcher.
    /// Periodically queries MediaInfo.FetchCurrentThumbnailBytesAsync and notifies subscribers when the media (thumbnail bytes) changes.
    /// This keeps thumbnail polling in one place so multiple consumers can reuse it.
    /// </summary>
    public class MediaThumbnailService
    {
        private static MediaThumbnailService? _instance;
        public static MediaThumbnailService Instance => _instance ??= new MediaThumbnailService();

        // Backing field for event so we can detect subscriptions
        private EventHandler<MediaChangedEventArgs>? _thumbnailChanged;
        public event EventHandler<MediaChangedEventArgs>? ThumbnailChanged
        {
            add
            {
                lock (listLock)
                {
                    _thumbnailChanged += value;
                    if (cts == null)
                        StartLoop();

                    // If we already have cached data, invoke immediately on threadpool
                    if (lastMedia != null || lastBytes != null)
                    {
                        var snapMedia = lastMedia;
                        var snapBytes = lastBytes == null ? null : (byte[])lastBytes.Clone();
                        Task.Run(() => value?.Invoke(this, new MediaChangedEventArgs(snapMedia == null ? null : new Media { Title = snapMedia.Title, Artist = snapMedia.Artist }, snapBytes)));
                    }
                    else
                    {
                        // Kick off a one-shot fetch so subscribers get data quickly
                        Task.Run(async () =>
                        {
                            try
                            {
                                var meta = await MediaInfo.FetchCurrentMediaAsync().ConfigureAwait(false);
                                var tbytes = await MediaInfo.FetchCurrentThumbnailBytesAsync().ConfigureAwait(false);

                                lock (listLock)
                                {
                                    lastBytes = tbytes == null ? null : (byte[])tbytes.Clone();
                                    lastMedia = meta == null ? null : new Media { Title = meta.Title, Artist = meta.Artist, ThumbnailData = lastBytes };
                                }

                                value?.Invoke(this, new MediaChangedEventArgs(meta == null ? null : new Media { Title = meta.Title, Artist = meta.Artist }, tbytes));
                            }
                            catch { }
                        });
                    }
                }
            }
            remove
            {
                lock (listLock)
                {
                    _thumbnailChanged -= value;
                    if (legacyListeners.Count == 0 && _thumbnailChanged == null)
                        StopLoop();
                }
            }
        }

        // Backwards-compatible simple subscribe/unsubscribe (calls event internally)
        private readonly List<Action<Media?>> legacyListeners = new List<Action<Media?>>();

        private CancellationTokenSource? cts;
        private readonly object listLock = new object();
        private readonly TimeSpan interval = TimeSpan.FromSeconds(1);

        private byte[]? lastBytes = null;
        private Media? lastMedia = null;
        private SKBitmap? lastBitmap = null; // cached decoded bitmap (owned by service)

        private MediaThumbnailService() { }

        public void Subscribe(Action<Media?> callback)
        {
            if (callback == null) return;
            lock (listLock)
            {
                legacyListeners.Add(callback);
                if (cts == null)
                    StartLoop();

                if (lastMedia != null || lastBytes != null)
                {
                    var snapMedia = lastMedia;
                    var snapBytes = lastBytes == null ? null : (byte[])lastBytes.Clone();
                    try { Task.Run(() => callback(new Media { Title = snapMedia?.Title, Artist = snapMedia?.Artist, ThumbnailData = snapBytes })); } catch { }
                }
                else
                {
                    // One-shot fetch to populate cache and notify this new subscriber quickly
                    Task.Run(async () =>
                    {
                        try
                        {
                            var meta = await MediaInfo.FetchCurrentMediaAsync().ConfigureAwait(false);
                            var tbytes = await MediaInfo.FetchCurrentThumbnailBytesAsync().ConfigureAwait(false);

                            lock (listLock)
                            {
                                lastBytes = tbytes == null ? null : (byte[])tbytes.Clone();
                                lastMedia = meta == null ? null : new Media { Title = meta.Title, Artist = meta.Artist, ThumbnailData = lastBytes };
                            }

                            callback(new Media { Title = meta?.Title, Artist = meta?.Artist, ThumbnailData = tbytes });
                        }
                        catch { }
                    });
                }
            }
        }

        public void Unsubscribe(Action<Media?> callback)
        {
            if (callback == null) return;
            lock (listLock)
            {
                legacyListeners.Remove(callback);
                if (legacyListeners.Count == 0 && _thumbnailChanged == null)
                    StopLoop();
            }
        }

        private void StartLoop()
        {
            if (cts != null) return;
            cts = new CancellationTokenSource();
            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // Fetch metadata and thumbnail bytes concurrently
                        var metaTask = MediaInfo.FetchCurrentMediaAsync();
                        var thumbTask = MediaInfo.FetchCurrentThumbnailBytesAsync();

                        await Task.WhenAll(metaTask, thumbTask).ConfigureAwait(false);

                        var media = metaTask.Result;
                        byte[]? bytes = thumbTask.Result;

                        bool changed = false;

                        if (bytes == null && lastBytes == null)
                        {
                            changed = false;
                        }
                        else if (bytes == null && lastBytes != null)
                        {
                            changed = true;
                        }
                        else if (bytes != null && lastBytes == null)
                        {
                            changed = true;
                        }
                        else if (bytes != null && lastBytes != null)
                        {
                            if (bytes.Length != lastBytes.Length)
                                changed = true;
                            else
                            {
                                for (int i = 0; i < bytes.Length; i++)
                                {
                                    if (bytes[i] != lastBytes[i])
                                    {
                                        changed = true; break;
                                    }
                                }
                            }
                        }

                        if (changed)
                        {
                            // Update cached bytes and bitmap
                            try
                            {
                                if (lastBitmap != null)
                                {
                                    try { lastBitmap.Dispose(); } catch { }
                                    lastBitmap = null;
                                }

                                if (bytes != null && bytes.Length > 0)
                                {
                                    try
                                    {
                                        using var ms = new SKMemoryStream(bytes);
                                        var bmp = SKBitmap.Decode(ms);
                                        lastBitmap = bmp;
                                    }
                                    catch
                                    {
                                        lastBitmap = null;
                                    }
                                }
                            }
                            catch { }

                            lastBytes = bytes == null ? null : (byte[])bytes.Clone();
                            // Store lastMedia as metadata+bytes for legacy consumers
                            lastMedia = new Media { Title = media?.Title, Artist = media?.Artist, ThumbnailData = lastBytes };

                            // Raise typed event using backing field
                            try
                            {
                                _thumbnailChanged?.Invoke(this, new MediaChangedEventArgs(media == null ? null : new Media { Title = media.Title, Artist = media.Artist }, lastBytes));
                            }
                            catch { }

                            // Call legacy listeners for compatibility, provide Media with ThumbnailData filled
                            List<Action<Media?>> snap;
                            lock (listLock) { snap = new List<Action<Media?>>(legacyListeners); }
                            var notifyMedia = new Media { Title = media?.Title, Artist = media?.Artist, ThumbnailData = lastBytes };
                            foreach (var l in snap)
                            {
                                try { Task.Run(() => l(notifyMedia)); } catch { }
                            }
                        }
                        else
                        {
                            // Update lastMedia even if bytes unchanged so new subscribers get metadata (keep bytes)
                            if (media != null)
                                lastMedia = new Media { Title = media.Title, Artist = media.Artist, ThumbnailData = lastBytes };
                        }
                    }
                    catch (Exception ex)
                    {
#if DEBUG
                        try { System.Diagnostics.Debug.WriteLine($"[MediaThumbnailService] fetch error: {ex}"); } catch { }
#endif
                    }

                    int totalMs = (int)interval.TotalMilliseconds;
                    int waited = 0;
                    const int step = 250;
                    while (waited < totalMs && !token.IsCancellationRequested)
                    {
                        int delay = Math.Min(step, totalMs - waited);
                        try { await Task.Delay(delay).ConfigureAwait(false); } catch { }
                        waited += delay;
                    }
                }

                // Loop exiting, clear lastMedia/bytes/bitmap to free memory
                lastBytes = null;
                lastMedia = null;
                if (lastBitmap != null)
                {
                    try { lastBitmap.Dispose(); } catch { }
                    lastBitmap = null;
                }
            });
        }

        private void StopLoop()
        {
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
            cts = null;
        }

        /// <summary>
        /// Synchronously returns the last fetched thumbnail bytes (may be null).
        /// </summary>
        public byte[]? GetCurrentThumbnailBytes()
        {
            return lastBytes == null ? null : (byte[])lastBytes.Clone();
        }

        /// <summary>
        /// Returns a reference to the internal cached decoded SKBitmap. Do NOT dispose the returned bitmap.
        /// If you need an owned bitmap, clone it on your side.
        /// </summary>
        public SKBitmap? GetCurrentThumbnailBitmap()
        {
            return lastBitmap; // Note: caller must not dispose
        }
    }
}
