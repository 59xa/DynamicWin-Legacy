using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;
using WindowsMediaController;
using static WindowsMediaController.MediaManager;

namespace DynamicWin.Utils
{
    /*
    *   Overview:
    *    - Allow user to interact with media controls inside a widget that implements it.
    *    - Provide separate APIs for metadata and thumbnail bytes to avoid fetching thumbnails when not required.
    *    
    *   Author:                 Florian Butz
    *   GitHub:                 https://github.com/FlorianButz
    *   Implementation Date:    3 August 2024
    *   Last Modified:          31 December 2025
    */

    public class MediaController
    {
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
        private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
        private const byte VK_MEDIA_PREV_TRACK = 0xB1;

        public void PlayPause()
        {
            keybd_event(VK_MEDIA_PLAY_PAUSE, 0, 0, 0);
        }

        public void Next()
        {
            keybd_event(VK_MEDIA_NEXT_TRACK, 0, 0, 0);
        }

        public void Previous()
        {
            keybd_event(VK_MEDIA_PREV_TRACK, 0, 0, 0);
        }
    }

    /*
    *   Overview:
    *    - Allows the fetching of currently playing media metadata (title/artist) separately from thumbnail bytes.
    *    - Provides FetchCurrentMediaAsync that returns metadata-only (ThumbnailData = null) and
    *      FetchCurrentThumbnailBytesAsync for fetching thumbnail bytes alone.
    *    - This separation reduces work for consumers that only need text metadata.
    *    
    *   Author:                 59xa
    *   GitHub:                 https://github.com/59xa
    *   Implementation Date:    19 May 2025
    *   Last Modified:          10 January 2026
    */

    public class MediaInfo
    {
        private static MediaInfo? _i;
        private static MediaManager _m;
        private static bool _started = false;
        private static readonly SemaphoreSlim _fetchLock = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim _startLock = new SemaphoreSlim(1, 1);
        private static DateTime _lastFetch = DateTime.MinValue;
        private static readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(1); // Cache for 1s to reduce work

        // Dedicated small cache & lock for timeline-only fetches so UI can poll frequently
        private static readonly SemaphoreSlim _timelineLock = new SemaphoreSlim(1, 1);
        private static DateTime _lastTimelineFetch = DateTime.MinValue;
        private static readonly TimeSpan _timelineCacheDuration = TimeSpan.FromMilliseconds(250);
        private static MediaTimeline? _timelineCache = null;

        public static MediaInfo Instance => _i ??= new MediaInfo();

        public static Media? Current { get; private set; }

        /// <summary>
        /// Ensures the MediaManager instance exists and has been started. Returns true when ready.
        /// This method is safe to call concurrently and will return false on failure.
        /// </summary>
        private static async Task<bool> EnsureManagerStartedAsync()
        {
            if (_m == null)
            {
                try
                {
                    _m = new MediaManager();
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine("[MEDIA CONTROLLER] MediaManager constructor failed: " + ex.Message);
#endif
                    _m = null;
                    _started = false;
                    _lastFetch = DateTime.UtcNow;
                    return false;
                }
            }

            if (_started) return true;

            await _startLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_started) return true;

                try
                {
                    await _m.StartAsync().ConfigureAwait(false);
                    _started = true;
                    return true;
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine("[MEDIA CONTROLLER] MediaManager failed to start: " + ex.Message);
#endif
                    // On failure, avoid retrying too aggressively
                    _lastFetch = DateTime.UtcNow;
                    Current = null;
                    _started = false;
                    try
                    {
                        if (_m is IDisposable d) d.Dispose();
                    }
                    catch { }
                    _m = null;
                    return false;
                }
            }
            finally
            {
                _startLock.Release();
            }
        }

        /// <summary>
        /// Initialise the MediaManager on a dedicated STA background thread. This helps avoid startup races
        /// where Windows Media Controller requires STA/COM context.
        /// This method is safe to call multiple times; it will simply start initialisation if not already started.
        /// </summary>
        public static void Initialize()
        {
            try
            {
                // If already started, nothing to do
                if (_started) return;

                // Start initialisation on an STA thread to satisfy COM/WinRT requirements
                var t = new Thread(() =>
                {
                    try
                    {
                        // Call the private async starter synchronously on this STA thread
                        EnsureManagerStartedAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
#if DEBUG
                        Debug.WriteLine("[MEDIA CONTROLLER] MediaInfo.Initialize STA thread failed: " + ex.Message);
#endif
                    }
                });
                t.IsBackground = true;
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine("[MEDIA CONTROLLER] MediaInfo.Initialize failed: " + ex.Message);
#endif
            }
        }

        /// <summary>
        /// Fetch metadata (Title, Artist) for the currently focused session. This method intentionally does NOT
        /// fetch or return the thumbnail bytes to keep it lightweight for callers that only need text metadata.
        /// </summary>
        public static async Task<Media?> FetchCurrentMediaAsync()
        {
            // Return cached result if recent
            if (Current != null && (DateTime.UtcNow - _lastFetch) < _cacheDuration)
                return Current;

            // Ensure manager exists and is started only once
            if (!await EnsureManagerStartedAsync().ConfigureAwait(false))
            {
                return null;
            }

            await _fetchLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Re-check cache after acquiring lock
                if (Current != null && (DateTime.UtcNow - _lastFetch) < _cacheDuration)
                    return Current;

                var _s = _m?.GetFocusedSession();
                if (_s == null)
                {
                    Current = null;
                    _lastFetch = DateTime.UtcNow;
                    return null;
                }

                // Await media properties instead of blocking
                var control = _s.ControlSession;
                if (control == null)
                {
                    Current = null;
                    _lastFetch = DateTime.UtcNow;
                    return null;
                }

                var _p = await control.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
                if (_p == null)
                {
                    Current = null;
                    _lastFetch = DateTime.UtcNow;
                    return null;
                }

                var _t = control.GetTimelineProperties();

                var _i = control.GetPlaybackInfo();

                // Note: intentionally do not read thumbnail stream here - keep metadata-only
                var result = new Media 
                { 
                    Title = _p.Title,
                    Artist = _p.Artist,
                    ThumbnailData = null,
                };

                Current = result;
                _lastFetch = DateTime.UtcNow;

#if DEBUG
                Debug.WriteLine($"[MEDIA CONTROLLER] TITLE: {_p.Title}, ARTIST: {_p.Artist}, START: {_t.StartTime}, END: {_t.EndTime}, POSITION: {_t.Position}, PLAYBACK STATUS: {_i.PlaybackStatus}");
#endif
                return result;
            }
            finally
            {
                _fetchLock.Release();
            }
        }

        /// <summary>
        /// Fetches only timeline properties (position/start/end/playback status) for the currently focused session.
        /// This uses a small cache and separate lock to allow frequent polling (e.g. every 250ms) without impacting
        /// metadata or thumbnail fetches.
        /// </summary>
        public static async Task<MediaTimeline?> FetchCurrentTimelineAsync()
        {
            // Return cached timeline if recent
            if (_timelineCache != null && (DateTime.UtcNow - _lastTimelineFetch) < _timelineCacheDuration)
                return _timelineCache;

            if (!await EnsureManagerStartedAsync().ConfigureAwait(false))
                return null;

            await _timelineLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_timelineCache != null && (DateTime.UtcNow - _lastTimelineFetch) < _timelineCacheDuration)
                    return _timelineCache;

                var _s = _m?.GetFocusedSession();
                if (_s == null)
                {
                    _timelineCache = null;
                    _lastTimelineFetch = DateTime.UtcNow;
                    return null;
                }

                var control = _s.ControlSession;
                if (control == null)
                {
                    _timelineCache = null;
                    _lastTimelineFetch = DateTime.UtcNow;
                    return null;
                }

                try
                {
                    var _t = control.GetTimelineProperties();
                    var _i = control.GetPlaybackInfo();

                    var tl = new MediaTimeline
                    {
                        Position = _t.Position,
                        StartTime = _t.StartTime,
                        EndTime = _t.EndTime,
                        PlaybackStatus = _i?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed
                    };

                    _timelineCache = tl;
                    _lastTimelineFetch = DateTime.UtcNow;
                    return tl;
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine("[MEDIA CONTROLLER] FetchCurrentTimelineAsync error: " + ex.Message);
#endif
                    _timelineCache = null;
                    _lastTimelineFetch = DateTime.UtcNow;
                    return null;
                }
            }
            finally
            {
                _timelineLock.Release();
            }
        }

        /// <summary>
        /// Fetches only the thumbnail bytes for the currently focused session. This is a cheap separate call so
        /// consumers can subscribe to thumbnails without forcing every metadata fetch to read binary streams.
        /// Returns null when there is no thumbnail available or on failure.
        /// </summary>
        public static async Task<byte[]?> FetchCurrentThumbnailBytesAsync()
        {
            // Ensure manager exists and has been started only once
            if (!await EnsureManagerStartedAsync().ConfigureAwait(false))
            {
                return null;
            }

            // Do not use the same _fetchLock as metadata - allow thumbnail fetches to proceed concurrently
            try
            {
                var _s = _m?.GetFocusedSession();
                if (_s == null) return null;

                var control = _s.ControlSession;
                if (control == null) return null;

                var _p = await control.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
                if (_p == null) return null;

                if (_p.Thumbnail == null) return null;

                try
                {
                    using var streamRef = await _p.Thumbnail.OpenReadAsync().AsTask().ConfigureAwait(false);
                    using var stream = streamRef.AsStreamForRead();
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms).ConfigureAwait(false);
                    return ms.ToArray();
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine("[MEDIA CONTROLLER] Failed to read thumbnail bytes: " + ex.Message);
#endif
                    return null;
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine("[MEDIA CONTROLLER] FetchCurrentThumbnailBytesAsync error: " + ex.Message);
#endif
                return null;
            }
        }

        /// <summary>
        /// Attempts to set the playback position for the currently focused session.
        /// Returns true when the request completes successfully.
        /// </summary>
        public static async Task<bool> SeekCurrentSessionAsync(TimeSpan position)
        {
            if (!await EnsureManagerStartedAsync().ConfigureAwait(false))
                return false;

            try
            {
                var _s = _m?.GetFocusedSession();
                if (_s == null) return false;

                var control = _s.ControlSession;
                if (control == null) return false;

                try
                {
                    // Use TimeSpan ticks (100-nanosecond units) if the wrapper expects a long representing ticks
                    var op = control.TryChangePlaybackPositionAsync(position.Ticks);
                    await op.AsTask().ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine("[MEDIA CONTROLLER] SeekCurrentSessionAsync failed: " + ex.Message);
#endif
                    return false;
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine("[MEDIA CONTROLLER] SeekCurrentSessionAsync error: " + ex.Message);
#endif
                return false;
            }
        }
    }

    /// <summary>
    /// Lightweight timeline-only container used by FetchCurrentTimelineAsync.
    /// </summary>
    public class MediaTimeline
    {
        public TimeSpan Position { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus { get; set; }
    }

    public class Media
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public byte[]? ThumbnailData { get; set; }
    }
}
