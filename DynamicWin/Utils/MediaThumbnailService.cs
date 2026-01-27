using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using Windows.Media.Control;
using DynamicWin.Utils;

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
    /// Polling-based: periodically polls WinRT for metadata/thumbnail changes, but prevents concurrent duplicate fetches.
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
                        // Notify subscriber that there is currently no media so UI can clear state immediately
                        value?.Invoke(this, new MediaChangedEventArgs(null, null));
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
        // Poll interval when using polling mode
        private readonly TimeSpan pollInterval = TimeSpan.FromSeconds(1);

        private byte[]? lastBytes;
        private Media? lastMedia;
        private SKBitmap? lastBitmap;

        // Simple guard to prevent concurrent fetches
        private int fetchRunning = 0;

        // Debounce candidate metadata to avoid fetching thumbnails while rapid metadata changes occur
        private Media? pendingMediaCandidate = null;
        private DateTime pendingMediaCandidateAt = DateTime.MinValue;
        private readonly TimeSpan pendingMediaStableDelay = TimeSpan.FromMilliseconds(500);

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
                    callback(null);

                // Ensure a fetch is scheduled to refresh state
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

            // Start polling loop which periodically calls FetchAndUpdateAsync but ensures only one fetch runs at a time
            _ = Task.Run(async () =>
            {
                // Immediate initial fetch
                if (Interlocked.CompareExchange(ref fetchRunning, 1, 0) == 0)
                {
                    try { await FetchAndUpdateAsync().ConfigureAwait(false); } catch { }
                    finally { Interlocked.Exchange(ref fetchRunning, 0); }
                }

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // Wait poll interval (cooperative)
                        try { await Task.Delay(pollInterval, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }

                        // If a fetch is already running, skip this cycle to avoid duplicate work
                        if (Interlocked.CompareExchange(ref fetchRunning, 1, 0) != 0)
                            continue;

                        try
                        {
                            await FetchAndUpdateAsync().ConfigureAwait(false);
                        }
                        catch { /* Swallow */ }
                        finally
                        {
                            Interlocked.Exchange(ref fetchRunning, 0);
                        }
                    }
                    catch { }
                }

                // Clean up on exit
                DisposeCachedBitmap();
                lastBytes = null;
                lastMedia = null;

            }, token);
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
        /// Behaviour: fetch metadata first, and only fetch thumbnail bytes when metadata changed OR we have no cached bytes.
        /// Polling ensures this is called periodically; fetchRunning guard prevents concurrent duplicate fetches.
        /// </summary>
        private async Task FetchAndUpdateAsync()
        {
            // Fetch metadata first
            Media? media = null;
            try
            {
                // Only force refresh if we have no cached media or if debounce is in effect
                bool shouldForce = lastMedia == null;
                media = await MediaInfo.FetchCurrentMediaAsync(forceRefresh: shouldForce).ConfigureAwait(false);
            }
            catch { media = null; }

            // If there is no media, clear all cached metadata and thumbnail, and notify subscribers immediately
            if (media == null)
            {
                DisposeCachedBitmap();
                lastBytes = null;
                lastMedia = null;

                // Notify all subscribers (typed and legacy) that there is no media
                _thumbnailChanged?.Invoke(this, new MediaChangedEventArgs(null, null));
                List<Action<Media?>> snap;
                lock (listLock) { snap = new List<Action<Media?>>(legacyListeners); }
                foreach (var l in snap)
                {
                    try { l(null); } catch { }
                }
                return;
            }

            // Determine whether metadata changed compared to lastMedia (case-insensitive)
            bool metadataChanged = !AreMediaEqual(media, lastMedia);

            // Debounce rapid metadata changes: if metadata changed compared to last known, hold it as a candidate
            // and only proceed to fetch thumbnail bytes once it remains stable for pendingMediaStableDelay.
            if (metadataChanged)
            {
                if (pendingMediaCandidate == null || !AreMediaEqual(pendingMediaCandidate, media))
                {
                    pendingMediaCandidate = media;
                    pendingMediaCandidateAt = DateTime.UtcNow;
                    return;
                }
                if ((DateTime.UtcNow - pendingMediaCandidateAt) < pendingMediaStableDelay)
                {
                    return;
                }
                metadataChanged = true;
                pendingMediaCandidate = null;
            }
            else
            {
                pendingMediaCandidate = null;
            }

            if (!metadataChanged && lastBytes != null)
            {
                return;
            }

            byte[]? bytes = null;
            try
            {
                bytes = await MediaInfo.FetchCurrentThumbnailBytesAsync().ConfigureAwait(false);
            }
            catch { bytes = null; }

            bool bytesChanged = !AreBytesEqual(lastBytes, bytes);

            lastBytes = bytes == null ? null : (byte[])bytes.Clone();
            lastMedia = new Media
            {
                Title = media?.Title,
                Artist = media?.Artist,
                ThumbnailData = lastBytes
            };

            if (bytesChanged || metadataChanged)
            {
                if (bytesChanged)
                {
                    UpdateBitmap(bytes);
                }
                _thumbnailChanged?.Invoke(this, new MediaChangedEventArgs(
                    media == null ? null : new Media { Title = media.Title, Artist = media.Artist },
                    lastBytes
                ));
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

        private bool AreMediaEqual(Media? a, Media? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;

            return string.Equals(a.Title ?? string.Empty, b.Title ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(a.Artist ?? string.Empty, b.Artist ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        public byte[]? GetCurrentThumbnailBytes() => lastBytes == null ? null : (byte[])lastBytes.Clone();
        public SKBitmap? GetCurrentThumbnailBitmap() => lastBitmap; // do not dispose externally
    }
}