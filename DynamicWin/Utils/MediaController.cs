using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WindowsMediaController;
using static WindowsMediaController.MediaManager;
using System.Threading;

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
    *   Last Modified:          01 January 2026
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
                    Debug.WriteLine("MediaManager constructor failed: " + ex.Message);
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
                    Debug.WriteLine("MediaManager failed to start: " + ex.Message);
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

                // Note: intentionally do not read thumbnail stream here - keep metadata-only
                var result = new Media { Title = _p.Title, Artist = _p.Artist, ThumbnailData = null };

                Current = result;
                _lastFetch = DateTime.UtcNow;

#if DEBUG
                Debug.WriteLine("[MEDIA CONTROLLER] TITLE: {0}, ARTIST: {1}", _p.Title, _p.Artist);
#endif
                return result;
            }
            finally
            {
                _fetchLock.Release();
            }
        }

        /// <summary>
        /// Fetches only the thumbnail bytes for the currently focused session. This is a cheap separate call so
        /// consumers can subscribe to thumbnails without forcing every metadata fetch to read binary streams.
        /// Returns null when there is no thumbnail available or on failure.
        /// </summary>
        public static async Task<byte[]?> FetchCurrentThumbnailBytesAsync()
        {
            // Ensure manager exists and is started only once
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
                    Debug.WriteLine("Failed to read thumbnail bytes: " + ex.Message);
                    return null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("FetchCurrentThumbnailBytesAsync error: " + ex.Message);
                return null;
            }
        }
    }

    public class Media
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public byte[]? ThumbnailData { get; set; }
    }
}
