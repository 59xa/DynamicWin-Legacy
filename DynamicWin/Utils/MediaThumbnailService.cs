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

                        // If we have metadata but no bytes cached, schedule a one-shot fetch for the thumbnail
                        if (snapMedia != null && (snapBytes == null || snapBytes.Length == 0))
                        {
                            // Schedule a debounced fetch with forceRefresh
                            RequestFetchAndUpdate(forceThumbnailRefresh: true);
                        }
                    }
                    else
                    {
                        // Notify subscriber that there is currently no media so UI can clear state immediately
                        value?.Invoke(this, new MediaChangedEventArgs(null, null));
                        // Kick off quick fetch for new subscriber (debounced)
                        RequestFetchAndUpdate();
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
        private ulong? lastBitmapFingerprint = null;

        // Keep a cheap fingerprint of the encoded thumbnail bytes to avoid repeated decodes
        private ulong? lastEncodedFingerprint = null;

        // Simple guard to prevent concurrent fetches
        private int fetchRunning = 0;

        // Debounce fetch trigger
        private int fetchRequested = 0;
        private DateTime lastFetchRequest = DateTime.MinValue;
        private readonly TimeSpan fetchDebounceDelay = TimeSpan.FromMilliseconds(150);
        private Task? debounceTask = null;

        // Flag to request force refresh of thumbnail bytes on next fetch
        private int forceThumbnailRefreshFlag = 0;

        public ulong? GetCurrentThumbnailFingerprint() => lastBitmapFingerprint;

        // Debounce candidate metadata to avoid fetching thumbnails while rapid metadata changes occur
        private Media? pendingMediaCandidate = null;
        private DateTime pendingMediaCandidateAt = DateTime.MinValue;
        private readonly TimeSpan pendingMediaStableDelay = TimeSpan.FromMilliseconds(500);

        // Add playback status tracking for widgets
        private GlobalSystemMediaTransportControlsSessionPlaybackStatus? _lastPlaybackStatus = null;
        public GlobalSystemMediaTransportControlsSessionPlaybackStatus? LastPlaybackStatus => _lastPlaybackStatus;
        // Track when playback transitioned from playing to a non-playing state
        private DateTime? lastPlaybackNotPlayingAt = null;
        // Whether we've observed playback status at least once since service started
        private bool playbackStateInitialized = false;
        // Track if we've already notified about being paused for longer than 30 seconds
        private bool hasNotifiedPausedLongThreshold = false;

        /// <summary>
        /// Returns true if media has been in a non-playing state for at least the provided duration.
        /// </summary>
        public bool IsPausedLongerThan(TimeSpan duration)
        {
            if (lastPlaybackNotPlayingAt == null) return false;
            try
            {
                return (DateTime.UtcNow - lastPlaybackNotPlayingAt.Value) >= duration;
            }
            catch { return false; }
        }

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
                RequestFetchAndUpdate();

                // If we have metadata but no bytes cached, schedule a one-shot fetch for the thumbnail
                if (lastMedia != null && (lastBytes == null || lastBytes.Length == 0))
                {
                    RequestFetchAndUpdate(forceThumbnailRefresh: true);
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

        private void StartLoop()
        {
            if (cts != null) return;
            cts = new CancellationTokenSource();
            var token = cts.Token;

            // Start polling loop which periodically calls RequestFetchAndUpdate but ensures only one fetch runs at a time
            _ = Task.Run(async () =>
            {
                // Immediate initial fetch
                RequestFetchAndUpdate();

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // Wait poll interval (cooperative)
                        try { await Task.Delay(pollInterval, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                        RequestFetchAndUpdate();
                    }
                    catch { }
                }

                // Clean up on exit
                DisposeCachedBitmap();
                lastBytes = null;
                lastMedia = null;
                lastEncodedFingerprint = null;

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
        private async Task FetchAndUpdateAsync(bool forceRefreshThumbnail = false)
        {
            // Ensure only one fetch runs at a time (additional guard in Debounce loop too)
            if (Interlocked.CompareExchange(ref fetchRunning, 1, 0) != 0)
                return;

            try
            {
                // Fetch metadata first
                Media? media = null;
                try
                {
                    // Only force refresh if we have no cached media
                    bool shouldForce = lastMedia == null;
                    media = await MediaInfo.FetchCurrentMediaAsync(forceRefresh: shouldForce).ConfigureAwait(false);
                }
                catch { media = null; }

                // Fetch timeline/playback status
                GlobalSystemMediaTransportControlsSessionPlaybackStatus? playbackStatus = null;
                try
                {
                    var timeline = await MediaInfo.FetchCurrentTimelineAsync(forceRefresh: false).ConfigureAwait(false);
                    if (timeline != null)
                        playbackStatus = timeline.PlaybackStatus;
                }
                catch { }
                // Update last playback status and record when it became non-playing
                var previousPlaybackStatus = _lastPlaybackStatus;
                try
                {
                    _lastPlaybackStatus = playbackStatus;

                    // If this is the first observed playback state since service start, treat a non-playing
                    // state as having been not-playing for longer than the idle threshold so widgets that
                    // should hide on startup will collapse immediately. Subsequent non-playing transitions
                    // use the normal timestamping behaviour (mark time when transition from playing occurs).
                    if (!playbackStateInitialized)
                    {
                        playbackStateInitialized = true;
                        if (playbackStatus.HasValue && playbackStatus.Value == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            lastPlaybackNotPlayingAt = null;
                        }
                        else
                        {
                            // Mark as having been not-playing for a while so startup widgets can hide immediately
                            lastPlaybackNotPlayingAt = DateTime.UtcNow.AddSeconds(-31);
                        }
                    }
                    else
                    {
                        if (playbackStatus.HasValue && playbackStatus.Value == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            lastPlaybackNotPlayingAt = null;
                            hasNotifiedPausedLongThreshold = false;
                        }
                        else
                        {
                            if (previousPlaybackStatus.HasValue && previousPlaybackStatus.Value == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                            {
                                // Just transitioned from playing to not-playing
                                lastPlaybackNotPlayingAt = DateTime.UtcNow;
                                hasNotifiedPausedLongThreshold = false;
                            }
                            else if (!previousPlaybackStatus.HasValue && playbackStatus.HasValue && playbackStatus.Value != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                            {
                                // Started already not-playing (fallback)
                                lastPlaybackNotPlayingAt = DateTime.UtcNow;
                                hasNotifiedPausedLongThreshold = false;
                            }
                        }
                    }
                }
                catch { _lastPlaybackStatus = playbackStatus; }

                // If there is no media, clear all cached metadata and thumbnail, and notify subscribers immediately
                if (media == null)
                {
                    DisposeCachedBitmap();
                    lastBytes = null;
                    lastMedia = null;
                    lastEncodedFingerprint = null;

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

                if (!metadataChanged && lastBytes != null && !forceRefreshThumbnail)
                {
                    // Before returning, check if playback status change or pause threshold crossing must be notified
                    try
                    {
                        if (playbackStatus != null && lastMedia != null)
                        {
                            bool statusChanged = !Equals(previousPlaybackStatus, playbackStatus);
                            bool isPausedLong = IsPausedLongerThan(TimeSpan.FromSeconds(30));
                            bool shouldNotify = false;
                            
                            // Check if playback status changed
                            if (statusChanged)
                            {
                                shouldNotify = true;
                            }
                            // Check if 30-second pause threshold was crossed
                            else if (isPausedLong && !hasNotifiedPausedLongThreshold)
                            {
                                // Just crossed into "paused long" territory
                                hasNotifiedPausedLongThreshold = true;
                                shouldNotify = true;
                            }
                            else if (!isPausedLong && hasNotifiedPausedLongThreshold)
                            {
                                // Transitioned back to "not paused long" (resumed playing)
                                hasNotifiedPausedLongThreshold = false;
                                shouldNotify = true;
                            }
                            
                            if (shouldNotify)
                            {
                                _thumbnailChanged?.Invoke(this, new MediaChangedEventArgs(
                                    new Media { Title = lastMedia.Title, Artist = lastMedia.Artist },
                                    lastBytes
                                ));

                                List<Action<Media?>> snap3;
                                lock (listLock) { snap3 = new List<Action<Media?>>(legacyListeners); }
                                foreach (var l in snap3)
                                {
                                    try { l(lastMedia); } catch { }
                                }
                            }
                        }
                    }
                    catch { }
                    return;
                }

                byte[]? bytes = null;
                try
                {
                    bytes = await MediaInfo.FetchCurrentThumbnailBytesAsync(forceRefresh: forceRefreshThumbnail).ConfigureAwait(false);
                }
                catch { bytes = null; }

                // Capture previous fingerprint before attempting to update
                var prevFingerprint = lastBitmapFingerprint;

                // Compute encoded-bytes fingerprint to avoid repeated decodes when bytes unchanged
                ulong? encodedFp = null;
                if (bytes != null && bytes.Length > 0)
                {
                    try { encodedFp = GetEncodedFingerprint(bytes); } catch { encodedFp = null; }
                }

                // Update cached lastBytes
                lastBytes = bytes == null ? null : (byte[])bytes.Clone();

                ulong? newFingerprint = null;

                // Only decode image and compute visual fingerprint if encoded bytes changed or we have no cached bitmap
                if (encodedFp.HasValue && lastEncodedFingerprint.HasValue && encodedFp.Value == lastEncodedFingerprint.Value && lastBitmap != null)
                {
                    // Encoded bytes identical to previous: avoid decode
                    newFingerprint = lastBitmapFingerprint;
                }
                else
                {
                    // Either bytes changed or we don't have a cached bitmap; attempt decode/update
                    newFingerprint = UpdateBitmap(bytes);
                }

                // Remember encoded fingerprint when we successfully processed bytes
                if (encodedFp.HasValue)
                    lastEncodedFingerprint = encodedFp;
                else
                    lastEncodedFingerprint = null;

                lastMedia = new Media
                {
                    Title = media?.Title,
                    Artist = media?.Artist,
                    ThumbnailData = lastBytes
                };

                // If playback status changed during this fetch, notify subscribers so widgets can re-evaluate
                try
                {
                    if (playbackStatus != null && lastMedia != null)
                    {
                        // Check if playback status changed
                        bool statusChanged = !Equals(previousPlaybackStatus, playbackStatus);
                        
                        // If playback status changed, notify subscribers
                        if (statusChanged)
                        {
                            _thumbnailChanged?.Invoke(this, new MediaChangedEventArgs(
                                new Media { Title = lastMedia.Title, Artist = lastMedia.Artist },
                                lastBytes
                            ));

                            List<Action<Media?>> snap2;
                            lock (listLock) { snap2 = new List<Action<Media?>>(legacyListeners); }
                            foreach (var l in snap2)
                            {
                                try { l(lastMedia); } catch { }
                            }
                        }
                    }
                }
                catch { }

                bool bytesChanged = false;
                if (newFingerprint.HasValue || prevFingerprint.HasValue)
                {
                    bytesChanged = !(newFingerprint.HasValue && prevFingerprint.HasValue && newFingerprint.Value == prevFingerprint.Value);
                }
                else
                {
                    // Both null -> no image
                    bytesChanged = false;
                }

                if (bytesChanged || metadataChanged)
                {
                    // If metadata changed but visual image is identical, avoid sending bytes to prevent consumers animating; send metadata-only (null bytes)
                    byte[]? notifyBytes = null;
                    if (bytesChanged) notifyBytes = lastBytes;

                    _thumbnailChanged?.Invoke(this, new MediaChangedEventArgs(
                        media == null ? null : new Media { Title = media.Title, Artist = media.Artist },
                        notifyBytes
                    ));

                    List<Action<Media?>> snap;
                    lock (listLock) { snap = new List<Action<Media?>>(legacyListeners); }
                    foreach (var l in snap)
                    {
                        try { l(lastMedia); } catch { }
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref fetchRunning, 0);
            }
        }

        /// <summary>
        /// Fast non-cryptographic fingerprint of encoded bytes (FNV-1a 64-bit).
        /// </summary>
        private static ulong GetEncodedFingerprint(byte[] bytes)
        {
            const ulong fnvOffset = 1469598103934665603UL;
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
        /// Decode bytes and update canonical cached SKBitmap only when fingerprint differs.
        /// Returns the computed fingerprint (or null on failure).
        /// </summary>
        private ulong? UpdateBitmap(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                // Clear cached bitmap and fingerprint
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
            catch { decoded = null; }

            if (decoded == null) return null;

            ulong? fp = null;
            try { fp = BitmapUtils.GetBitmapFingerprint(decoded); } catch { fp = null; }

            // If fingerprint equals existing, discard decoded and keep existing canonical bitmap
            if (fp.HasValue && lastBitmapFingerprint.HasValue && fp.Value == lastBitmapFingerprint.Value)
            {
                try { decoded.Dispose(); } catch { }
                return fp;
            }

            // Replace canonical bitmap
            DisposeCachedBitmap();
            lastBitmap = decoded;
            lastBitmapFingerprint = fp;

            return fp;
        }

        private void DisposeCachedBitmap()
        {
            if (lastBitmap != null)
            {
                try { lastBitmap.Dispose(); } catch { }
                lastBitmap = null;
            }
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

        /// <summary>
        /// Force re-notification of the current thumbnail and media to all subscribers.
        /// Useful after UI/menu switches to ensure widgets re-sync.
        /// </summary>
        public void ForceNotifyCurrentThumbnail()
        {
            lock (listLock)
            {
                var snapMedia = lastMedia;
                var snapBytes = lastBytes;
                _thumbnailChanged?.Invoke(this, new MediaChangedEventArgs(
                    snapMedia == null ? null : new Media { Title = snapMedia.Title, Artist = snapMedia.Artist },
                    snapBytes
                ));
                foreach (var l in legacyListeners)
                {
                    try { l(snapMedia); } catch { }
                }

                // If we have metadata but no bytes cached, schedule a one-shot fetch and re-notify when done
                if (snapMedia != null && (snapBytes == null || snapBytes.Length == 0))
                {
                    RequestFetchAndUpdate(forceThumbnailRefresh: true);
                }
            }
        }

        /// <summary>
        /// Debounced fetch trigger. Coalesces rapid requests and ensures only one fetch runs at a time.
        /// </summary>
        private void RequestFetchAndUpdate(bool forceThumbnailRefresh = false)
        {
            // Ensure service loop is running
            if (cts == null) StartLoop();

            if (forceThumbnailRefresh)
                Interlocked.Exchange(ref forceThumbnailRefreshFlag, 1);

            // Mark a fetch as requested
            Interlocked.Exchange(ref fetchRequested, 1);
            lastFetchRequest = DateTime.UtcNow;

            // Only one debounce task at a time
            lock (listLock)
            {
                if (debounceTask != null && !debounceTask.IsCompleted)
                    return;
                debounceTask = DebounceFetchAsync(cts!.Token);
            }
        }

        private async Task DebounceFetchAsync(CancellationToken token)
        {
            while (true)
            {
                // Wait for debounce delay (cancellable)
                var now = DateTime.UtcNow;
                var wait = fetchDebounceDelay - (now - lastFetchRequest);
                try
                {
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                if (token.IsCancellationRequested) break;

                // If another fetch was requested during the wait, proceed
                if (Interlocked.Exchange(ref fetchRequested, 0) == 1)
                {
                    // Invoke fetch; FetchAndUpdateAsync itself ensures only one fetch runs at a time
                    var force = Interlocked.Exchange(ref forceThumbnailRefreshFlag, 0) == 1;
                    try { await FetchAndUpdateAsync(force).ConfigureAwait(false); } catch { }

                    // Check if another fetch was requested during the fetch
                    if (Interlocked.CompareExchange(ref fetchRequested, 0, 0) == 1)
                        continue;
                }

                break;
            }
        }
    }
}