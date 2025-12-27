using DynamicWin.UI.UIElements;
using DynamicWin.Utils;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

/* DEPRECATED */

namespace DynamicWin.UI.Widgets.Big
{
    class RegisterMediaWidget : IRegisterableWidget
    {
        public bool IsSmallWidget => false;
        public string WidgetName => "Legacy Media Playback Control";

        public WidgetBase CreateWidgetInstance(UIObject? parent, Vec2 position, UIAlignment alignment = UIAlignment.TopCenter)
        {
            return new MediaWidget(parent, position, alignment);
        }
    }

    public class MediaWidget : WidgetBase
    {
        MediaController controller;
        AudioVisualiser audioVisualiser;
        AudioVisualiser audioVisualiserBig;

        DWImageButton playPause;
        DWImageButton next;
        DWImageButton prev;

        DWText noMediaPlaying;

        DWText title;
        DWText artist;

        // Cache previous artist and song values
        private string? lastTitle = null;
        private string? lastArtist = null;

        public MediaWidget(UIObject? parent, Vec2 position, UIAlignment alignment = UIAlignment.TopCenter) : base(parent, position, alignment)
        {
            InitMediaPlayer();

            playPause = new DWImageButton(this, Resources.Res.PlayPause, new Vec2(0, 25), new Vec2(30, 30), () =>
            {
                controller.PlayPause();
            }, alignment: UIAlignment.Center)
            {
                roundRadius = 25,
                normalColor = Col.Transparent,
                hoverColor = Col.White.Override(a: 0.1f),
                clickColor = Col.White.Override(a: 0.25f),
                hoverScaleMulti = Vec2.one * 1.25f,
                imageScale = 0.8f
            };
            AddLocalObject(playPause);

            next = new DWImageButton(this, Resources.Res.Next, new Vec2(50, 25), new Vec2(30, 30), () =>
            {
                controller.Next();
            }, alignment: UIAlignment.Center)
            {
                roundRadius = 25,
                normalColor = Col.Transparent,
                hoverColor = Col.White.Override(a: 0.1f),
                clickColor = Col.White.Override(a: 0.25f),
                hoverScaleMulti = Vec2.one * 1.25f,
                imageScale = 0.65f
            };
            AddLocalObject(next);

            prev = new DWImageButton(this, Resources.Res.Previous, new Vec2(-50, 25), new Vec2(30, 30), () =>
            {
                controller.Previous();
            }, alignment: UIAlignment.Center)
            {
                roundRadius = 25,
                normalColor = Col.Transparent,
                hoverColor = Col.White.Override(a: 0.1f),
                clickColor = Col.White.Override(a: 0.25f),
                hoverScaleMulti = Vec2.one * 1.25f,
                imageScale = 0.65f
            };
            AddLocalObject(prev);

            audioVisualiser = new AudioVisualiser(this, new Vec2(0, 30), new Vec2(125, 25));
            AddLocalObject(audioVisualiser);

            // Use generalised colour for audio visualiser
            audioVisualiserBig = new AudioVisualiser(this, new Vec2(0, 0), GetWidgetSize(), alignment: UIAlignment.Center,
                Primary: mediaCol.Override(a: 1f), Secondary: mediaCol.Override(a: 1f) * 0.1f);

            audioVisualiserBig.BlurAmount = 20f;
            audioVisualiserBig.SilentSetActive(false);
            AddLocalObject(audioVisualiserBig);

            noMediaPlaying = new DWText(this, "No Media Playing", new Vec2(0, 30))
            {
                Color = Theme.TextSecond,
                Font = Resources.Res.SatoshiBold,
                TextSize = 16
            };
            noMediaPlaying.SilentSetActive(false);
            AddLocalObject(noMediaPlaying);

            title = new DWText(this, "Title", new Vec2(0, 22.5f))
            {
                Color = Theme.TextSecond,
                Font = Resources.Res.SatoshiBold,
                TextSize = 15
            };
            title.SilentSetActive(false);
            AddLocalObject(title);

            artist = new DWText(this, "Artist", new Vec2(0, 42.5f))
            {
                Color = Theme.TextThird,
                Font = Resources.Res.SatoshiRegular,
                TextSize = 13
            };
            artist.SilentSetActive(false);
            AddLocalObject(artist);
        }

        float smoothedAmp = 0f;
        float smoothing = 1.5f;

        int cycle = 0;

        // Flag to avoid overlapping fetches
        private bool fetchingMedia = false;

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            if (cycle % 32 == 0)
            {
                // Periodically fetch current media info
                _ = FetchAndApplyMediaAsync();

                // Use last fetched values if available
                if (!string.IsNullOrEmpty(lastTitle))
                {
                    title.Text = DWText.Truncate(lastTitle, (GetWidgetWidth() == 400 ? 50 : 20));
                }

                if (!string.IsNullOrEmpty(lastArtist))
                {
                    artist.Text = DWText.Truncate(lastArtist, (GetWidgetWidth() == 400 ? 60 : 28));
                }
            }
            cycle++;

            prev.normalColor = Theme.IconColor * audioVisualiser.GetActionCol().Override(a: 0.2f);
            next.normalColor = Theme.IconColor * audioVisualiser.GetActionCol().Override(a: 0.2f);
            playPause.normalColor = Theme.IconColor * audioVisualiser.GetActionCol().Override(a: 0.2f);

            smoothedAmp = (float)Math.Max(Mathf.Lerp(smoothedAmp, audioVisualiser.AverageAmplitude, smoothing * deltaTime), audioVisualiser.AverageAmplitude);

            if (smoothedAmp < 0.005f) smoothedAmp = 0f;

            bool showSmallVisualiser = !isMediaAvailable && !smoothedAmp.Equals(0f);

            noMediaPlaying.SetActive(!showSmallVisualiser && !isMediaAvailable);
            title.SetActive(isMediaAvailable);
            artist.SetActive(isMediaAvailable);
            audioVisualiserBig.SetActive(isMediaAvailable);
            audioVisualiser.SetActive(showSmallVisualiser);
        }

        private void InitMediaPlayer()
        {
            controller = new MediaController();
        }

        private async Task FetchAndApplyMediaAsync()
        {
            if (fetchingMedia) return;
            fetchingMedia = true;

            try
            {
                var media = await MediaInfo.FetchCurrentMediaAsync();

                // Marshal UI updates to UI thread
                BeginInvokeUI(() =>
                {
                    if (media != null)
                    {
                        // Show media info
                        lastTitle = media.Title;
                        lastArtist = media.Artist;

                        title.Text = !string.IsNullOrEmpty(media.Title) ? DWText.Truncate(media.Title, (GetWidgetWidth() == 400 ? 50 : 20)) : "Title";
                        artist.Text = !string.IsNullOrEmpty(media.Artist) ? DWText.Truncate(media.Artist, (GetWidgetWidth() == 400 ? 60 : 28)) : string.Empty;
                        isMediaAvailable = true;
                    }
                    else
                    {
                        // No media
                        isMediaAvailable = false;
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to fetch media info: " + ex.Message);
                BeginInvokeUI(() => isMediaAvailable = false);
            }
            finally
            {
                fetchingMedia = false;
            }
        }

        bool isMediaAvailable = false;
        Col mediaCol = Theme.Primary;

        public override void DrawWidget(SKCanvas canvas)
        {
            var paint = GetPaint();
            paint.Color = GetColor(Theme.WidgetBackground).Value();
            canvas.DrawRoundRect(GetRect(), paint);

            int saveCanvas = canvas.Save();

            canvas.ClipRoundRect(GetRect());

            canvas.RestoreToCount(saveCanvas);

            if (isMediaAvailable)
            {
                var r = GetRect();
                paint = GetPaint();
                paint.Color = GetColor(mediaCol.Override(a: 0.25f * Color.a)).Value();
                paint.StrokeWidth = 2f;
                float[] intervals = { 5, 8 };
                paint.PathEffect = SKPathEffect.CreateDash(intervals, (float)-cycle * 0.1f);
                paint.IsStroke = true;
                paint.StrokeCap = SKStrokeCap.Round;
                paint.StrokeJoin = SKStrokeJoin.Round;
                canvas.DrawRoundRect(r, paint);
            }
        }
    }
}
