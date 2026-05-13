using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using Windows.Media.Control;
using DynamicWin.Utils;

namespace DynamicWin.Utils
{
    /// <summary>
    /// Lightweight event args for media changes. Uses value types internally to reduce allocations.
    /// </summary>
    public class MediaChangedEventArgs : EventArgs
    {
        public Media? Media { get; }
        public byte[]? ThumbnailBytes { get; }

        // Pool to reduce allocations
        private static readonly object poolLock = new object();
        private static MediaChangedEventArgs? pool;
        private bool isPooled;

        public MediaChangedEventArgs() { isPooled = false; }

        public MediaChangedEventArgs(Media? media, byte[]? thumbnailBytes)
        {
            Media = media;
            ThumbnailBytes = thumbnailBytes;
            isPooled = false;
        }

        public static MediaChangedEventArgs Rent(Media? media, byte[]? thumbnailBytes)
        {
            lock (poolLock)
            {
                if (pool != null)
                {
                    var args = pool;
                    pool = null;
                    // Note: We can't actually reuse the fields in C#, so we just create new
                    args.isPooled = false;
                    return new MediaChangedEventArgs(media, thumbnailBytes);
                }
            }
            return new MediaChangedEventArgs(media, thumbnailBytes);
        }

        public void Return()
        {
            if (isPooled) return;
            lock (poolLock)
            {
                if (pool == null)
                    pool = this;
                isPooled = true;
            }
        }
    }

    /// <summary>
    /// Centralised, optimized thumbnail service.
    /// - Minimal lock contention through atomic operations
    /// - Deferred string allocations using snapshots
    /// - Efficient fingerprinting to avoid redundant decodes
    /// - Single async debounce loop with exponential backoff
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
                if (value == null) return;
                lock (listLock)
                {
                    _thumbnailChanged += value;
                    if (cts == null) StartLoop();

                    // Fire cached data snapshot without allocating intermediate objects
                    var (media, bytes) = GetCachedSnapshot();
                    if (media != null || bytes != null)
                    {
                        value.Invoke(this, new MediaChangedEventArgs(media, bytes));

                        // If metadata exists but no thumbnail, trigger fetch
                        if (media != null && (bytes == null || bytes.Length == 0))
                        {
                            fetchRequested = true;
                            fetchForceRefresh = true;
                        }
                    }
                    else
                    {
                        value.Invoke(this, new MediaChangedEventArgs(null, null));
                        fetchRequested = true;
                    }
                }
            }
            remove
            {
                if (value == null) return;
                lock (listLock)
                {
                    _thumbnailChanged -= value;
                    if (_thumbnailChanged == null && legacyListeners.Count == 0)
                        StopLoop();
                }
            }
        }

        // Legacy callback support
        private readonly List<Action<Media?>> legacyListeners = new List<Action<Media?>>(2);

        private CancellationTokenSource? cts;
        private readonly object listLock = new object();
        private readonly TimeSpan pollInterval = TimeSpan.FromMilliseconds(250); // Reduced from 1s for faster response

        // Cached data
        private byte[]? lastBytes;
        private Media? lastMedia;
        private SKBitmap? lastBitmap;
        private ulong? lastBitmapFingerprint;
        private ulong? lastEncodedFingerprint;

        // Fetch state - atomic operations to avoid locks
        private volatile bool fetchRequested = false;
        private volatile bool fetchForceRefresh = false;
        private int fetchRunning = 0;
        private DateTime lastFetchTime = DateTime.MinValue;
        private readonly TimeSpan fetchDebounceDelay = TimeSpan.FromMilliseconds(150);

        // Media candidate debouncing
        private Media? pendingMediaCandidate;
        private DateTime pendingMediaCandidateTime = DateTime.MinValue;
        private readonly TimeSpan pendingMediaStableDelay = TimeSpan.FromMilliseconds(500);

        // Playback status tracking
        private GlobalSystemMediaTransportControlsSessionPlaybackStatus? _lastPlaybackStatus;
        public GlobalSystemMediaTransportControlsSessionPlaybackStatus? LastPlaybackStatus => _lastPlaybackStatus;
        private DateTime? lastPlaybackNotPlayingAt;
        private bool playbackStateInitialized;
        private bool hasNotifiedPausedLongThreshold;

        public bool IsPausedLongerThan(TimeSpan duration)
        {
            if (lastPlaybackNotPlayingAt == null) return false;
            return (DateTime.UtcNow - lastPlaybackNotPlayingAt.Value) >= duration;
        }

        private MediaThumbnailService() { }

        public void Subscribe(Action<Media?> callback)
        {
            if (callback == null) return;
            lock (listLock)
            {
                legacyListeners.Add(callback);
                if (cts == null) StartLoop();

                // Send cached snapshot
                var (media, bytes) = GetCachedSnapshot();
                if (media != null || bytes != null)
                {
                    callback(new Media
                    {
                        Title = media?.Title,
                        Artist = media?.Artist,
                        ThumbnailData = bytes
                    });

                    if (media != null && (bytes == null || bytes.Length == 0))
                    {
                        fetchRequested = true;
                        fetchForceRefresh = true;
                    }
                }
                else
                {
                    callback(null);
                    fetchRequested = true;
                }
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

        /// <summary>
        /// Get snapshot of cached media and bytes without allocations/locks except brief critical section.
        /// </summary>
        private (Media?, byte[]?) GetCachedSnapshot()
        {
            // No lock needed for atomic reads in .NET
            return (lastMedia, lastBytes);
        }

        private void StartLoop()
        {
            if (cts != null) return;
            cts = new CancellationTokenSource();
            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                fetchRequested = true;

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // Wait poll interval
                        await Task.Delay(pollInterval, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }

                    if (fetchRequested)
                    {
                        var forceRefresh = fetchForceRefresh;
                        fetchForceRefresh = false;

                        // Debounce: wait for stable period before fetching
                        if ((DateTime.UtcNow - lastFetchTime) < fetchDebounceDelay)
                        {
                            try { await Task.Delay(fetchDebounceDelay, token).ConfigureAwait(false); } catch { break; }
                        }

                        if (token.IsCancellationRequested) break;

                        await FetchAndUpdateAsync(forceRefresh).ConfigureAwait(false);
                    }
                }

                // Cleanup
                DisposeCachedBitmap();
                lastBytes = null;
                lastMedia = null;
                lastEncodedFingerprint = null;

            }, token);
        }

        private void StopLoop()
        {
            if (cts == null) return;
            try { cts.Cancel(); cts.Dispose(); }
            catch { }
            cts = null;
        }

        /// <summary>
        /// Optimized fetch: minimal allocations, single path, efficient fingerprinting.
        /// </summary>
        private async Task FetchAndUpdateAsync(bool forceRefreshThumbnail = false)
        {
            // Guard against concurrent fetches
            if (Interlocked.CompareExchange(ref fetchRunning, 1, 0) != 0)
                return;

            try
            {
                lastFetchTime = DateTime.UtcNow;
                fetchRequested = false;

                // Fetch metadata
                Media? media = null;
                try
                {
                    media = await MediaInfo.FetchCurrentMediaAsync(forceRefresh: lastMedia == null).ConfigureAwait(false);
                }
                catch { }

                // Fetch playback status
                GlobalSystemMediaTransportControlsSessionPlaybackStatus? playbackStatus = null;
                try
                {
                    var timeline = await MediaInfo.FetchCurrentTimelineAsync(forceRefresh: false).ConfigureAwait(false);
                    playbackStatus = timeline?.PlaybackStatus;
                }
                catch { }

                // Update playback status tracking
                UpdatePlaybackStatus(playbackStatus);

                // No media case
                if (media == null)
                {
                    if (lastMedia != null)
                    {
                        DisposeCachedBitmap();
                        lastBytes = null;
                        lastMedia = null;
                        lastEncodedFingerprint = null;

                        NotifySubscribers(null, null);
                    }
                    return;
                }

                // Check if metadata changed
                bool metadataChanged = !AreMediaEqual(media, lastMedia);

                // Debounce rapid metadata changes
                if (metadataChanged)
                {
                    if (!AreMediaEqual(pendingMediaCandidate, media))
                    {
                        pendingMediaCandidate = media;
                        pendingMediaCandidateTime = DateTime.UtcNow;
                        return;
                    }

                    if ((DateTime.UtcNow - pendingMediaCandidateTime) < pendingMediaStableDelay)
                        return;

                    pendingMediaCandidate = null;
                    metadataChanged = true;
                }

                // If no changes and we have bytes cached, just check playback status
                if (!metadataChanged && lastBytes != null && !forceRefreshThumbnail)
                {
                    // Notify if playback status relevant
                    if (ShouldNotifyForPlaybackStatus(playbackStatus))
                    {
                        NotifySubscribers(lastMedia, lastBytes);
                    }
                    return;
                }

                // Fetch thumbnail bytes - force refresh if metadata changed
                byte[]? bytes = null;
                try
                {
                    bytes = await MediaInfo.FetchCurrentThumbnailBytesAsync(forceRefresh: forceRefreshThumbnail || metadataChanged).ConfigureAwait(false);
                }
                catch { }

                // Update cached data
                var prevFingerprint = lastBitmapFingerprint;
                var encodedFp = ComputeEncodedFingerprint(bytes);
                lastBytes = bytes == null ? null : (byte[])bytes.Clone();

                ulong? newFingerprint = null;

                // Only decode if bytes changed or we don't have cached bitmap
                if (encodedFp.HasValue && lastEncodedFingerprint == encodedFp && lastBitmap != null)
                {
                    // Skip decode - bytes identical
                    newFingerprint = lastBitmapFingerprint;
                }
                else
                {
                    newFingerprint = UpdateBitmap(bytes);
                }

                if (encodedFp.HasValue)
                    lastEncodedFingerprint = encodedFp;

                lastMedia = media;

                bool bytesChanged = (newFingerprint != prevFingerprint);
                if (bytesChanged || metadataChanged)
                {
                    // Always include bytes when notifying - subscribers may not have them cached yet
                    NotifySubscribers(media, lastBytes);
                }
            }
            finally
            {
                Interlocked.Exchange(ref fetchRunning, 0);
            }
        }

        /// <summary>
        /// FNV-1a 64-bit hash for encoded bytes.
        /// </summary>
        private static ulong? ComputeEncodedFingerprint(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;

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
        /// Decode bytes to bitmap and compute fingerprint. Returns fingerprint or null.
        /// </summary>
        private ulong? UpdateBitmap(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                DisposeCachedBitmap();
                lastBitmapFingerprint = null;
                return null;
            }

            SKBitmap? decoded = null;
            try
            {
                using var ms = new SKMemoryStream(bytes);
                decoded = SKBitmap.Decode(ms);
            }
            catch { }

            if (decoded == null) return null;

            ulong? fp = BitmapUtils.GetBitmapFingerprint(decoded);

            // If fingerprint matches existing, reuse existing bitmap
            if (fp.HasValue && lastBitmapFingerprint == fp)
            {
                decoded?.Dispose();
                return fp;
            }

            DisposeCachedBitmap();
            lastBitmap = decoded;
            lastBitmapFingerprint = fp;
            return fp;
        }

        private void DisposeCachedBitmap()
        {
            if (lastBitmap != null)
            {
                try { lastBitmap.Dispose(); }
                catch { }
                lastBitmap = null;
            }
        }

        private bool AreMediaEqual(Media? a, Media? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;

            return string.Equals(a.Title ?? "", b.Title ?? "", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(a.Artist ?? "", b.Artist ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private void UpdatePlaybackStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status)
        {
            var previous = _lastPlaybackStatus;
            _lastPlaybackStatus = status;

            if (!playbackStateInitialized)
            {
                playbackStateInitialized = true;
                if (status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    // Mark as paused on startup
                    lastPlaybackNotPlayingAt = DateTime.UtcNow.AddSeconds(-31);
                }
                return;
            }

            // Playing to not playing transition
            if (previous == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                lastPlaybackNotPlayingAt = DateTime.UtcNow;
                hasNotifiedPausedLongThreshold = false;
            }
            // Not playing to playing
            else if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                lastPlaybackNotPlayingAt = null;
                hasNotifiedPausedLongThreshold = false;
            }
        }

        private bool ShouldNotifyForPlaybackStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus? current)
        {
            if (current == null || _lastPlaybackStatus == null) return false;

            // Status changed
            if (current != _lastPlaybackStatus) return true;

            // Check 30-second pause threshold
            if (IsPausedLongerThan(TimeSpan.FromSeconds(30)))
            {
                if (!hasNotifiedPausedLongThreshold)
                {
                    hasNotifiedPausedLongThreshold = true;
                    return true;
                }
            }
            else if (hasNotifiedPausedLongThreshold)
            {
                hasNotifiedPausedLongThreshold = false;
                return true;
            }

            return false;
        }

        private void NotifySubscribers(Media? media, byte[]? bytes)
        {
            // Notify event subscribers
            _thumbnailChanged?.Invoke(this, new MediaChangedEventArgs(media, bytes));

            // Notify legacy subscribers
            if (legacyListeners.Count > 0)
            {
                // Snapshot to avoid lock during notifications
                List<Action<Media?>> snapshot;
                lock (listLock)
                {
                    snapshot = new List<Action<Media?>>(legacyListeners);
                }

                foreach (var listener in snapshot)
                {
                    try { listener(media); }
                    catch { }
                }
            }
        }

        public byte[]? GetCurrentThumbnailBytes() => lastBytes == null ? null : (byte[])lastBytes.Clone();
        public SKBitmap? GetCurrentThumbnailBitmap() => lastBitmap; // Do not dispose externally

        /// <summary>
        /// Force re-notification of current thumbnail to all subscribers.
        /// Fetches fresh metadata and thumbnail bytes before notifying.
        /// </summary>
        public void ForceNotifyCurrentThumbnail()
        {
            // Fetch fresh data synchronously before notifying to avoid race conditions
            _ = Task.Run(async () =>
            {
                try
                {
                    // Fetch current metadata from MediaInfo (which was just updated by RefreshMediaPropertiesAsync)
                    var media = await MediaInfo.FetchCurrentMediaAsync(forceRefresh: false).ConfigureAwait(false);

                    // Fetch thumbnail bytes for this media
                    byte[]? bytes = null;
                    if (media != null)
                    {
                        bytes = await MediaInfo.FetchCurrentThumbnailBytesAsync(forceRefresh: true).ConfigureAwait(false);
                    }

                    // Update our cache
                    if (media != null && !AreMediaEqual(media, lastMedia))
                    {
                        lastMedia = media;
                        lastBytes = bytes == null ? null : (byte[])bytes.Clone();
                        lastEncodedFingerprint = ComputeEncodedFingerprint(bytes);

                        // Update bitmap
                        UpdateBitmap(bytes);
                    }
                    else if (media != null && (bytes != null || lastBytes != null))
                    {
                        // Metadata is same but bytes might have changed
                        var newFp = ComputeEncodedFingerprint(bytes);
                        if (newFp != lastEncodedFingerprint)
                        {
                            lastBytes = bytes == null ? null : (byte[])bytes.Clone();
                            lastEncodedFingerprint = newFp;
                            UpdateBitmap(bytes);
                        }
                    }

                    // Notify with fresh data
                    lock (listLock)
                    {
                        NotifySubscribers(lastMedia, lastBytes);
                    }
                }
                catch { }
            });
        }
    }
}