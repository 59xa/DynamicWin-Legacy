using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;
using WindowsMediaController;
using static WindowsMediaController.MediaManager;
using DynamicWin.Utils;

namespace DynamicWin.Utils
{
    /*
    *   Overview:
    *    - Allow user to interact with media controls inside a widget that implements it.
    *    - Provide separate APIs for metadata and thumbnail bytes to avoid fetching thumbnails when not required.
    *    - Added: session manager event-based monitoring to raise MediaChanged events when WinRT notifies changes.
    *    - Debounces rapid WinRT events to avoid duplicate fetches.
    *    
    *   Author:                 Florian Butz & 59xa
    *   GitHub:                 https://github.com/FlorianButz
    *   Implementation Date:    3 August 2024
    *   Last Modified:          27 January 2026
    */

    public class MediaController
    {
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
        private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
        private const byte VK_MEDIA_PREV_TRACK = 0xB1;

        public void PlayPause() => SafeMediaAction(MediaInfo.TryTogglePlayPauseAsync, VK_MEDIA_PLAY_PAUSE);
        public void Next() => SafeMediaAction(MediaInfo.TryNextAsync, VK_MEDIA_NEXT_TRACK);
        public void Previous() => SafeMediaAction(MediaInfo.TryPreviousAsync, VK_MEDIA_PREV_TRACK);

        private void SafeMediaAction(Func<Task<bool>> winRtAction, byte fallbackKey)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!await winRtAction().ConfigureAwait(false))
                        keybd_event(fallbackKey, 0, 0, 0);
                }
                catch
                {
                    keybd_event(fallbackKey, 0, 0, 0);
                }
            });
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
    *   Last Modified:          27 January 2026
    */

    public class MediaInfo
    {
        private static MediaInfo? _instance;
        public static MediaInfo Instance => _instance ??= new MediaInfo();

        // Cached data (for consumers to read)
        public static Media? Current { get; private set; }
        public static bool HasCurrentSession => _currentSession != null;
        private static MediaTimeline? _timelineCache;
        private static byte[]? _thumbnailBytesCache;
        public static event Action<MediaTimeline?>? TimelineChanged;

        // WinRT Objects
        // Keep these alive so we don't recreate them constantly
        private static GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
        private static GlobalSystemMediaTransportControlsSession? _currentSession;

        // Locks and state
        private static readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private static bool _isInitialized = false;

        // Debounce timer for rapid WinRT events
        private static System.Timers.Timer? _debounceTimer = null;
        private static bool _debouncePending = false;
        private static GlobalSystemMediaTransportControlsSession? _debounceSession = null;
        private const double DebounceIntervalMs = 120; // 120ms debounce
        private static int _mediaPropertiesRefreshVersion = 0;
        private static int _thumbnailFetchVersion = 0;

        /// <summary>
        /// Initialises the connection to Windows Media controls once.
        /// Hooks up events so we don't have to poll manually.
        /// </summary>
        public static void Initialize()
        {
            if (_isInitialized) return;

            // Run on background thread to avoid blocking UI
            Task.Run(async () =>
            {
                await _initLock.WaitAsync();
                try
                {
                    if (_isInitialized) return;

                    // 1. Get the Manager ONCE
                    _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

                    // 2. Subscribe to session changes (when user switches apps)
                    _sessionManager.CurrentSessionChanged += OnSessionManager_CurrentSessionChanged;

                    // 3. Load initial session
                    UpdateCurrentSession();

                    _isInitialized = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MediaInfo] Init Failed: {ex.Message}");
                }
                finally
                {
                    _initLock.Release();
                }
            });
        }

        /// <summary>
        /// Handles switching focus between apps (e.g. Spotify -> Chrome)
        /// </summary>
        private static void OnSessionManager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            UpdateCurrentSession();
        }

        private static void UpdateCurrentSession()
        {
            try
            {
                // Unsubscribe from old session to prevent leaks
                if (_currentSession != null)
                {
                    _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                    _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                    _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
                    _currentSession = null;
                }

                // Get new session
                var session = _sessionManager?.GetCurrentSession();
                if (session != null)
                {
                    _currentSession = session;
                    _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                    _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
                    _currentSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;

                    // Immediate fetch of initial data
                    RefreshMediaPropertiesAsync(session);
                    RefreshTimeline(session, notify: true);
                }
                else
                {
                    // No media playing
                    Interlocked.Increment(ref _mediaPropertiesRefreshVersion);
                    Interlocked.Increment(ref _thumbnailFetchVersion);
                    Current = null;
                    _timelineCache = null;
                    _thumbnailBytesCache = null;
                    NotifyTimelineChanged(null);

                    try
                    {
                        MediaThumbnailService.Instance.ClearCurrentMedia(forceNotify: true);
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[MediaInfo] UpdateSession Error: {ex.Message}"); }
        }

        // Triggered by Windows when Song/Title changes
        private static void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            // Debounce rapid events, but always update immediately on first event
            lock (typeof(MediaInfo))
            {
                if (_debounceTimer == null)
                {
                    _debounceTimer = new System.Timers.Timer(DebounceIntervalMs);
                    _debounceTimer.AutoReset = false;
                    _debounceTimer.Elapsed += (s, e) =>
                    {
                        lock (typeof(MediaInfo))
                        {
                            if (_debouncePending && _debounceSession != null)
                            {
                                RefreshMediaPropertiesAsync(_debounceSession);
                                _debouncePending = false;
                                _debounceSession = null;
                            }
                        }
                    };
                }

                if (!_debouncePending)
                {
                    // First event: update immediately
                    RefreshMediaPropertiesAsync(sender);
                    _debouncePending = true;
                    _debounceSession = sender;
                    _debounceTimer.Stop();
                    _debounceTimer.Start();
                }
                else
                {
                    // Another event during debounce: just reset timer and remember session
                    _debounceSession = sender;
                    _debounceTimer.Stop();
                    _debounceTimer.Start();
                }
            }
        }

        // Triggered by Windows when Play/Pause/Position changes
        private static void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            RefreshTimeline(sender, notify: true);
            try
            {
                MediaThumbnailService.Instance.ForceNotifyCurrentThumbnail();
            }
            catch { }
        }

        private static void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            RefreshTimeline(sender, notify: true);
        }

        // Now async void, called directly from event handler
        private static async void RefreshMediaPropertiesAsync(GlobalSystemMediaTransportControlsSession session)
        {
            int refreshVersion = Interlocked.Increment(ref _mediaPropertiesRefreshVersion);

            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                if (props == null) return;
                if (refreshVersion != Volatile.Read(ref _mediaPropertiesRefreshVersion)) return;
                if (!ReferenceEquals(session, _currentSession)) return;

                // Update Text Metadata
                Current = new Media
                {
                    Title = props.Title,
                    Artist = props.Artist,
                    ThumbnailData = null // Keep null, fetch bytes only on demand
                };

                // Reset thumb cache on song change
                Interlocked.Increment(ref _thumbnailFetchVersion);
                _thumbnailBytesCache = null;

                // Notify the central thumbnail service that media properties changed
                // This ensures immediate thumbnail fetch and UI updates
                try
                {
                    MediaThumbnailService.Instance.ForceNotifyCurrentThumbnail();
                }
                catch { }
            }
            catch { }
        }

        private static MediaTimeline? RefreshTimeline(GlobalSystemMediaTransportControlsSession session, bool notify = false)
        {
            try
            {
                var timeline = session.GetTimelineProperties();
                var info = session.GetPlaybackInfo();
                var now = DateTimeOffset.UtcNow;
                var lastUpdated = timeline.LastUpdatedTime == default
                    ? now
                    : timeline.LastUpdatedTime.ToUniversalTime();

                var next = new MediaTimeline
                {
                    Position = timeline.Position,
                    StartTime = timeline.StartTime,
                    EndTime = timeline.EndTime,
                    LastUpdatedTime = lastUpdated,
                    CachedAt = now,
                    PlaybackStatus = info?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed
                };

                _timelineCache = next;
                var projected = ProjectTimeline(next);

                if (notify)
                    NotifyTimelineChanged(projected);

                return projected;
            }
            catch
            {
                return ProjectTimeline(_timelineCache);
            }
        }

        private static void NotifyTimelineChanged(MediaTimeline? timeline)
        {
            try { TimelineChanged?.Invoke(timeline); } catch { }
        }

        private static MediaTimeline? ProjectTimeline(MediaTimeline? timeline)
        {
            if (timeline == null) return null;

            var now = DateTimeOffset.UtcNow;
            var projected = new MediaTimeline
            {
                Position = timeline.Position,
                StartTime = timeline.StartTime,
                EndTime = timeline.EndTime,
                LastUpdatedTime = timeline.LastUpdatedTime,
                CachedAt = timeline.CachedAt,
                PlaybackStatus = timeline.PlaybackStatus
            };

            if (projected.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                var anchor = projected.LastUpdatedTime == default ? projected.CachedAt : projected.LastUpdatedTime;
                if (anchor != default)
                {
                    var delta = now - anchor.ToUniversalTime();
                    if (delta > TimeSpan.Zero && delta < TimeSpan.FromHours(6))
                        projected.Position += delta;
                }
            }

            if (projected.Position < projected.StartTime)
                projected.Position = projected.StartTime;

            if (projected.EndTime > projected.StartTime && projected.Position > projected.EndTime)
                projected.Position = projected.EndTime;

            projected.LastUpdatedTime = now;
            projected.CachedAt = now;
            return projected;
        }

        // Public API

        public static async Task<Media?> FetchCurrentMediaAsync(bool forceRefresh = false)
        {
            if (!_isInitialized) Initialize();

            // If we have a cached object, return it instantly
            // If the user wants to force refresh, we trigger the update logic manually
            if (forceRefresh && _currentSession != null)
            {
                RefreshMediaPropertiesAsync(_currentSession);
            }

            return Current;
        }

        public static async Task<MediaTimeline?> FetchCurrentTimelineAsync(bool forceRefresh = false)
        {
            if (!_isInitialized) Initialize();

            // For timeline, we might want to poll 'Position' if the song is playing, 
            // but for metadata, we just return the cache
            if (forceRefresh && _currentSession != null)
            {
                return RefreshTimeline(_currentSession);
            }

            return ProjectTimeline(_timelineCache);
        }

        public static async Task<byte[]?> FetchCurrentThumbnailBytesAsync(bool forceRefresh = false)
        {
            if (!_isInitialized) Initialize();

            // Return cached bytes if available
            if (_thumbnailBytesCache != null && !forceRefresh)
                return _thumbnailBytesCache;

            if (_currentSession == null) return null;
            int fetchVersion = Volatile.Read(ref _thumbnailFetchVersion);

            try
            {
                // We have to fetch the stream here
                var props = await _currentSession.TryGetMediaPropertiesAsync();
                if (props?.Thumbnail == null) return null;

                using var streamRef = await props.Thumbnail.OpenReadAsync();
                using var stream = streamRef.AsStreamForRead();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);

                if (fetchVersion != Volatile.Read(ref _thumbnailFetchVersion)) return null;

                _thumbnailBytesCache = ms.ToArray();
                return _thumbnailBytesCache;
            }
            catch
            {
                return null;
            }
        }

        // Controls

        public static async Task<bool> TryTogglePlayPauseAsync()
        {
            if (_currentSession == null) return false;
            return await _currentSession.TryTogglePlayPauseAsync();
        }

        public static async Task<bool> TryNextAsync()
        {
            if (_currentSession == null) return false;
            return await _currentSession.TrySkipNextAsync();
        }

        public static async Task<bool> TryPreviousAsync()
        {
            if (_currentSession == null) return false;
            return await _currentSession.TrySkipPreviousAsync();
        }

        public static async Task<bool> SeekCurrentSessionAsync(TimeSpan position)
        {
            var session = _currentSession;
            if (session == null) return false;

            bool changed = await session.TryChangePlaybackPositionAsync(position.Ticks);
            if (changed && ReferenceEquals(session, _currentSession))
                RefreshTimeline(session, notify: true);

            return changed;
        }
    }
}
