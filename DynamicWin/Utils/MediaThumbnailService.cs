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

        // Event backing
        private EventHandler<MediaChangedEventArgs>? _thumbnailChanged;
        public event EventHandler<MediaChangedEventArgs>? ThumbnailChanged
        {
            add
            {
                lock (listLock)
                {
                    _thumbnailChanged += value;
                    if (cts == null) StartLoop();

                    // Immediately fire cached data if available
                    if (lastMedia != null || lastBytes != null)
                    {
                        var snapMedia = lastMedia;
                        var snapBytes = lastBytes;
                        value?.Invoke(this, new MediaChangedEventArgs(
                            snapMedia == null ? null : new Media { Title = snapMedia.Title, Artist = snapMedia.Artist },
                            snapBytes
                        ));
                    }
                    else
                    {
                        // Kick off quick fetch for new subscriber
                        _ = FetchAndUpdateAsync();
                    }
                }
            }
            remove
            {
                lock (listLock)
                {
                    _thumbnailChanged -= value;
                    if (_thumbnailChanged == null && legacyListeners.Count == 0)
                        StopLoop();
                }
            }
        }

        // Legacy callback support
        private readonly List<Action<Media?>> legacyListeners = new List<Action<Media?>>();

        private CancellationTokenSource? cts;
        private readonly object listLock = new object();
        private readonly TimeSpan interval = TimeSpan.FromSeconds(1);

        private byte[]? lastBytes;
        private Media? lastMedia;
        private SKBitmap? lastBitmap;

        private MediaThumbnailService() { }

        public void Subscribe(Action<Media?> callback)
        {
            if (callback == null) return;
            lock (listLock)
            {
                legacyListeners.Add(callback);
                if (cts == null) StartLoop();

                if (lastMedia != null || lastBytes != null)
                    callback(new Media
                    {
                        Title = lastMedia?.Title,
                        Artist = lastMedia?.Artist,
                        ThumbnailData = lastBytes
                    });
                else
                    _ = FetchAndUpdateAsync();
            }
        }

        public void Unsubscribe(Action<Media?> callback)
        {
            if (callback == null) return;
            lock (listLock)
            {
                legacyListeners.Remove(callback);
                if (_thumbnailChanged == null && legacyListeners.Count == 0)
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
                        await FetchAndUpdateAsync();
                    }
                    catch { /* Swallow fetch exceptions */ }

                    try { await Task.Delay(interval, token); } catch { }
                }

                // Clean up on exit
                DisposeCachedBitmap();
                lastBytes = null;
                lastMedia = null;
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
        /// Fetch current media + thumbnail bytes and update caches.
        /// Only raises events if bytes have changed.
        /// </summary>
        private async Task FetchAndUpdateAsync()
        {
            var mediaTask = MediaInfo.FetchCurrentMediaAsync();
            var thumbTask = MediaInfo.FetchCurrentThumbnailBytesAsync();

            await Task.WhenAll(mediaTask, thumbTask).ConfigureAwait(false);

            var media = mediaTask.Result;
            var bytes = thumbTask.Result;

            bool bytesChanged = !AreBytesEqual(lastBytes, bytes);

            // Update caches
            lastBytes = bytes == null ? null : (byte[])bytes.Clone();
            lastMedia = new Media
            {
                Title = media?.Title,
                Artist = media?.Artist,
                ThumbnailData = lastBytes
            };

            if (bytesChanged)
            {
                // Decode bitmap once for all subscribers
                UpdateBitmap(bytes);

                // Raise typed event directly (no Task.Run)
                _thumbnailChanged?.Invoke(this, new MediaChangedEventArgs(
                    media == null ? null : new Media { Title = media.Title, Artist = media.Artist },
                    lastBytes
                ));

                // Notify legacy listeners directly
                List<Action<Media?>> snap;
                lock (listLock) { snap = new List<Action<Media?>>(legacyListeners); }
                foreach (var l in snap)
                {
                    try { l(lastMedia); } catch { }
                }
            }
        }

        private void UpdateBitmap(byte[]? bytes)
        {
            DisposeCachedBitmap();

            if (bytes == null || bytes.Length == 0) return;

            try
            {
                using var ms = new SKMemoryStream(bytes);
                lastBitmap = SKBitmap.Decode(ms);
            }
            catch { lastBitmap = null; }
        }

        private void DisposeCachedBitmap()
        {
            if (lastBitmap != null)
            {
                try { lastBitmap.Dispose(); } catch { }
                lastBitmap = null;
            }
        }

        private bool AreBytesEqual(byte[]? a, byte[]? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;

            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;

            return true;
        }

        public byte[]? GetCurrentThumbnailBytes() => lastBytes == null ? null : (byte[])lastBytes.Clone();
        public SKBitmap? GetCurrentThumbnailBitmap() => lastBitmap; // do not dispose externally
    }
}