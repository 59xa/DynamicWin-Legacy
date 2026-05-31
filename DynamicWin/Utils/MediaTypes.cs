namespace DynamicWin.Utils
{
    /// <summary>
    /// Lightweight timeline-only container used by FetchCurrentTimelineAsync.
    /// </summary>
    public class MediaTimeline
    {
        public System.TimeSpan Position { get; set; }
        public System.TimeSpan StartTime { get; set; }
        public System.TimeSpan EndTime { get; set; }
        public System.DateTimeOffset LastUpdatedTime { get; set; }
        public System.DateTimeOffset CachedAt { get; set; }
        public Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus { get; set; }
    }

    public class Media
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public byte[]? ThumbnailData { get; set; }
    }
}
