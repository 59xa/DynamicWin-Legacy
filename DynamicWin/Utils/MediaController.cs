using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using Windows.Media.Playback;
using WindowsMediaController;
using static WindowsMediaController.MediaManager;
using System.Threading;

namespace DynamicWin.Utils
{
    /*
    *   Overview:
    *    - Allow user to interact with media controls inside a widget that implements it.
    *    
    *   Author:                 Florian Butz
    *   GitHub:                 https://github.com/FlorianButz
    *   Implementation Date:    3 August 2024
    *   Last Modified:          3 August 2024
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
    *    - Allows the fetching of currently playing media, returning its artist name, media title, and its corresponding image.
    *    - Handles metadata mapping through Media class which returns the three mentioned information.
    *    
    *   Author:                 59xa
    *   GitHub:                 https://github.com/59xa
    *   Implementation Date:    19 May 2025
    *   Last Modified:          26 December 2025
    */

    public class MediaInfo
    {
        private static MediaInfo? _i;
        private static MediaManager _m;
        private static bool _started = false;
        private static readonly SemaphoreSlim _fetchLock = new SemaphoreSlim(1, 1);
        private static DateTime _lastFetch = DateTime.MinValue;
        private static readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(1); // Cache for 1s to reduce work

        public static MediaInfo Instance => _i ??= new MediaInfo();

        public static Media? Current { get; private set; }

        public static async Task<Media?> FetchCurrentMediaAsync()
        {
            // Return cached result if recent
            if (Current != null && (DateTime.UtcNow - _lastFetch) < _cacheDuration)
                return Current;

            // Ensure manager exists and is started only once
            if (_m == null)
                _m = new MediaManager();

            if (!_started)
            {
                try
                {
                    await _m.StartAsync();
                    _started = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("MediaManager failed to start: " + ex.Message);
                    // On failure, avoid retrying too aggressively
                    _lastFetch = DateTime.UtcNow;
                    Current = null;
                    return null;
                }
            }

            await _fetchLock.WaitAsync();
            try
            {
                // Re-check cache after acquiring lock
                if (Current != null && (DateTime.UtcNow - _lastFetch) < _cacheDuration)
                    return Current;

                var _s = _m.GetFocusedSession();
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

                var _p = await control.TryGetMediaPropertiesAsync();
                if (_p == null)
                {
                    Current = null;
                    _lastFetch = DateTime.UtcNow;
                    return null;
                }

                BitmapImage? _i = null;
                if (_p.Thumbnail != null)
                {
                    try
                    {
                        using var stream = await _p.Thumbnail.OpenReadAsync();
                        _i = new BitmapImage();
                        _i.BeginInit();
                        _i.StreamSource = stream.AsStreamForRead();
                        _i.CacheOption = BitmapCacheOption.OnLoad;
                        _i.EndInit();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Failed to load thumbnail: " + ex.Message);
                        _i = null;
                    }
                }

                var result = new Media { Title = _p.Title, Artist = _p.Artist, Thumbnail = _i };

                Current = result;
                _lastFetch = DateTime.UtcNow;

#if DEBUG
                Debug.WriteLine("[MEDIA CONTROLLER] TITLE: {0}, ARTIST: {1}, IMAGE: {2}", _p.Title, _p.Artist, _p.Thumbnail);
#endif
                return result;
            }
            finally
            {
                _fetchLock.Release();
            }
        }
    }

    public class Media
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public BitmapImage? Thumbnail { get; set; }
    }
}
