using DynamicWin.Main;
using DynamicWin.Resources;
using DynamicWin.UI.UIElements;
using DynamicWin.UI.UIElements.Custom;
using DynamicWin.UI.Widgets;
using DynamicWin.UI.Widgets.Big;
using DynamicWin.UI.Widgets.Small;
using DynamicWin.Utils;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using static DynamicWin.UI.UIElements.IslandObject;

namespace DynamicWin.UI.Menu.Menus
{
    public class HomeMenu : BaseMenu
    {
        public List<SmallWidgetBase> smallLeftWidgets = new List<SmallWidgetBase>();
        public List<SmallWidgetBase> smallRightWidgets = new List<SmallWidgetBase>();
        public List<SmallWidgetBase> smallCenterWidgets = new List<SmallWidgetBase>();

        public List<WidgetBase> bigWidgets = new List<WidgetBase>();

        float songSizeAddition = 0f;
        float songLocalPosXAddition = 0f;


        public void NextSong()
        {
            if (RendererMain.Instance.MainIsland.IsHovering) return;

            songSizeAddition = 45;
            songLocalPosXAddition = 45;
        }

        public void PrevSong()
        {
            if (RendererMain.Instance.MainIsland.IsHovering) return;

            songSizeAddition = 45;
            songLocalPosXAddition = -45;
        }

        public override Vec2 IslandSize()
        {
            Vec2 size = new Vec2(200, 35);

            float sizeTogether = 0f;
            smallLeftWidgets.ForEach(x => sizeTogether += x.GetWidgetSize().X);
            smallRightWidgets.ForEach(x => sizeTogether += x.GetWidgetSize().X);
            smallCenterWidgets.ForEach(x => sizeTogether += x.GetWidgetSize().X);

            sizeTogether += smallWidgetsSpacing * (smallCenterWidgets.Count + smallLeftWidgets.Count + smallRightWidgets.Count + 0.25f) + middleWidgetsSpacing;

            size.X = (float)Math.Max(size.X, sizeTogether) + songSizeAddition;

            return size;
        }

        public override Vec2 IslandSizeBig()
        {
            // Use different sizing logic depending on the current big menu mode
            if (currentBigMenuMode == BigMenuMode.Tray)
            {
                // Tray mode
                return new Vec2(350, 250);
            }
            else if (currentBigMenuMode == BigMenuMode.Media)
            {
                // Media mode
                float desiredMediaWidth = 440f;
                float desiredMediaHeight = 110f;
                float horizontalPadding = 60f;
                float topContainerHeight = 30f;
                float width = desiredMediaWidth + horizontalPadding;
                float height = desiredMediaHeight + bCD + topContainerHeight + topSpacing;
                return new Vec2(width, height);
            }
            else // Widgets mode (default)
            {
                Vec2 size = new Vec2(275, 145);

                float sizeTogetherBiggest = 0f;
                float sizeTogether = 0f;

                for (int i = 0; i < bigWidgets.Count; i++)
                {
                    sizeTogether += bigWidgets[i].GetWidgetSize().X + bigWidgetsSpacing * 2;
                    if ((i) % maxBigWidgetInOneRow == 1)
                    {
                        sizeTogetherBiggest = (float)Math.Max(sizeTogetherBiggest, sizeTogether);
                        sizeTogether = 0f;
                    }
                }

                size.X = (float)Math.Max(size.X, sizeTogetherBiggest);

                sizeTogetherBiggest = 0f;

                for (int i = 0; i < bigWidgets.Count; i++)
                {
                    if ((i) % maxBigWidgetInOneRow == 0)
                    {
                        sizeTogetherBiggest += bigWidgets[i].GetWidgetSize().Y;
                    }
                }

                sizeTogetherBiggest += bCD + (bigWidgetsSpacing * (int)Math.Floor((float)(bigWidgets.Count / maxBigWidgetInOneRow))) + topSpacing;

                // Set the container height to the total height of all rows
                size.Y = Math.Max(size.Y, sizeTogetherBiggest);

                return size;
            }
        }

        UIObject smallWidgetsContainer;
        UIObject bigWidgetsContainer;

        List<UIObject> bigMenuItems = new List<UIObject>();

        UIObject topContainer;

        Tray tray;
        MediaPlayer media;

        // Enum to track which big menu is active
        public enum BigMenuMode { Widgets, Tray, Media }
        public BigMenuMode currentBigMenuMode;

        public override List<UIObject> InitializeMenu(IslandObject island)
        {
            // Set currentBigMenuMode from user setting
            currentBigMenuMode = Settings.DefaultBigMenuMode;

            var objects = base.InitializeMenu(island);

            smallWidgetsContainer = new UIObject(island, Vec2.zero, IslandSize(), UIAlignment.Center);
            bigWidgetsContainer = new UIObject(island, Vec2.zero, IslandSize(), UIAlignment.Center);

            CancellationTokenSource _cts = new CancellationTokenSource();
            CancellationToken _ctk = _cts.Token;

            // Check if weather widget exists in home menu
            List<string> _bW = Settings.bigWidgets;
            if (_bW.Exists(x => x.Contains("RegisterWeatherWidget")) && RegisterWeatherWidgetSettings.saveData.isSettingsMenuOpen == true)
            {
                RegisterWeatherWidgetSettings.saveData.isSettingsMenuOpen = false;
            }

            // Create elements
            topContainer = new UIObject(island, new Vec2(0, 30), new Vec2(island.currSize.X, 50))
            {
                Color = Col.Transparent
            };
            bigMenuItems.Add(topContainer);

            // Initialize next and previous images
            next = new DWImage(island, Resources.Res.Next, new Vec2(50, 0), new Vec2(30, 30), UIAlignment.Center)
            {
            };
            next.SilentSetActive(false);
            
            previous = new DWImage(island, Resources.Res.Previous, new Vec2(-50, 0), new Vec2(30, 30), UIAlignment.Center)
            {
            };
            previous.SilentSetActive(false);
            
            // Add them to objects list
            objects.Add(next);
            objects.Add(previous);

            // Navigation buttons removed per user request.
            var settingsButton = new DWImageButton(topContainer, Resources.Res.Settings, new Vec2(-20f, 0), new Vec2(20, 20), () =>
            {
<<<<<<< HEAD
                var settingsWindow = new DynamicWin.UI.Forms.SettingsWindow();
=======
                var settingsWindow = new SettingsWindow();
>>>>>>> 578588243fc3c8382f91a900c41cde23a6b9c211
                settingsWindow.Show();

#if DEBUG
                System.Diagnostics.Debug.WriteLine("[HOME MENU] User opened Settings window.");
#endif
            },
            UIAlignment.MiddleRight);
            settingsButton.normalColor = Col.Transparent;
            settingsButton.hoverColor = Col.Transparent;
            settingsButton.clickColor = Theme.Primary.Override(a: 0.35f);
            settingsButton.roundRadius = 25;

            bigMenuItems.Add(settingsButton);

            tray = new Tray(island, new Vec2(0, -topSpacing * 1.5f), Vec2.zero, UIAlignment.BottomCenter)
            {
                Anchor = new Vec2(0.5f, 1f)
            };
            tray.SilentSetActive(false);
            bigMenuItems.Add(tray);

            // Instantiate media UIObject so media.SetActive(...) won't NRE
            media = new MediaPlayer(island, new Vec2(0, -topSpacing * 1.5f), Vec2.zero, UIAlignment.BottomCenter)
            {
                Anchor = new Vec2(0.5f, 0.8f)
            };
            media.SilentSetActive(false);
            bigMenuItems.Add(media);

            // Get all widgets

            Dictionary<string, IRegisterableWidget> smallWidgets = new Dictionary<string, IRegisterableWidget>();
            Dictionary<string, IRegisterableWidget> widgets = new Dictionary<string, IRegisterableWidget>();

            foreach (var widget in Res.availableSmallWidgets)
            {
                if (smallWidgets.ContainsKey(widget.GetType().FullName)) continue;
                smallWidgets.Add(widget.GetType().FullName, widget);
                System.Diagnostics.Debug.WriteLine(widget.GetType().FullName);
            }

            foreach (var widget in Res.availableBigWidgets)
            {
                if (widgets.ContainsKey(widget.GetType().FullName)) continue;
                widgets.Add(widget.GetType().FullName, widget);
                System.Diagnostics.Debug.WriteLine(widget.GetType().FullName);
            }

            // Create widgets

            foreach (var smallWidget in Settings.smallWidgetsMiddle)
            {
                if (!smallWidgets.ContainsKey(smallWidget.ToString())) continue;
                var widget = smallWidgets[smallWidget.ToString()];

                smallCenterWidgets.Add((SmallWidgetBase)widget.CreateWidgetInstance(smallWidgetsContainer, Vec2.zero, UIAlignment.Center));
            }

            foreach (var smallWidget in Settings.smallWidgetsLeft)
            {
                if (!smallWidgets.ContainsKey(smallWidget.ToString())) continue;
                var widget = smallWidgets[smallWidget.ToString()];

                smallLeftWidgets.Add((SmallWidgetBase)widget.CreateWidgetInstance(smallWidgetsContainer, Vec2.zero, UIAlignment.MiddleLeft));
            }

            foreach (var smallWidget in Settings.smallWidgetsRight)
            {
                if (!smallWidgets.ContainsKey(smallWidget.ToString())) continue;
                var widget = smallWidgets[smallWidget.ToString()];

                smallRightWidgets.Add((SmallWidgetBase)widget.CreateWidgetInstance(smallWidgetsContainer, Vec2.zero, UIAlignment.MiddleRight));
            }

            foreach (var bigWidget in Settings.bigWidgets)
            {
                if (!widgets.ContainsKey(bigWidget.ToString())) continue;
                var widget = widgets[bigWidget.ToString()];

                bigWidgets.Add((WidgetBase)widget.CreateWidgetInstance(bigWidgetsContainer, Vec2.zero, UIAlignment.BottomCenter));
            }

            smallLeftWidgets.ForEach(x => {
                objects.Add(x);
            });

            smallRightWidgets.ForEach(x => {
                objects.Add(x);
            });

            smallCenterWidgets.ForEach(x => {
                objects.Add(x);
            });

            bigWidgets.ForEach(x => {
                objects.Add(x);
                x.SilentSetActive(false);
            });

            // Add lists

            bigMenuItems.ForEach(x =>
            {
                objects.Add(x);
                x.SilentSetActive(false);
                });

            return objects;
        }

        DWImage next;
        DWImage previous;

        public float topSpacing = 15;
        public float bigWidgetsSpacing = 15;
        int maxBigWidgetInOneRow = 2;

        public float smallWidgetsSpacing = 15f;
        public float middleWidgetsSpacing = 70f;

        float sCD = 20; // Small widget padding (horizontal)
        float bCD = 50;

        public bool isWidgetMode = true;

        int cycle = 0;

        // Reusable list to avoid per-frame allocations
        private readonly List<WidgetBase> widgetsInOneLine = new List<WidgetBase>();

        public override void Update()
        {
            // Handle content visibility based on current mode
            bool isWidgets = currentBigMenuMode == BigMenuMode.Widgets;
            bool isTray = currentBigMenuMode == BigMenuMode.Tray;
            bool isMedia = currentBigMenuMode == BigMenuMode.Media;

            bigWidgetsContainer.SetActive(isWidgets);
            tray.SetActive(isTray);
            media.SetActive(isMedia);

            // Display logic for different modes handled elsewhere

            tray.Size = new Vec2(topContainer.Size.X - 5f, IslandSizeBig().Y - bCD - topContainer.Size.Y);

            // Enable / Disable small widgets

            smallLeftWidgets.ForEach(x => x.SetActive(!RendererMain.Instance.MainIsland.IsHovering));
            smallCenterWidgets.ForEach(x => x.SetActive(!RendererMain.Instance.MainIsland.IsHovering));
            smallRightWidgets.ForEach(x => x.SetActive(!RendererMain.Instance.MainIsland.IsHovering));

            // Enable / Disable big widgets / Tray / Media based on current mode

            tray.SetActive(RendererMain.Instance.MainIsland.IsHovering && currentBigMenuMode == BigMenuMode.Tray);
            media.SetActive(RendererMain.Instance.MainIsland.IsHovering && currentBigMenuMode == BigMenuMode.Media);
            bigWidgets.ForEach(x => x.SetActive(RendererMain.Instance.MainIsland.IsHovering && currentBigMenuMode == BigMenuMode.Widgets));
            bigMenuItems.ForEach(x =>
            {
                // Skip activating the Tray and Media UIObjects here; they are controlled separately above
                if (!(x is Tray) && !(x is MediaPlayer))
                {
                    x.SetActive(RendererMain.Instance.MainIsland.IsHovering);
                }
            });

            // Navigation button color animations removed per user request.

            RendererMain.Instance.MainIsland.LocalPosition.X = Mathf.Lerp(RendererMain.Instance.MainIsland.LocalPosition.X,
                songLocalPosXAddition, 2f * RendererMain.Instance.DeltaTime);
            songLocalPosXAddition = Mathf.Lerp(songLocalPosXAddition, 0f, 10 * RendererMain.Instance.DeltaTime);
            songSizeAddition = Mathf.Lerp(songSizeAddition, 0f, 10 * RendererMain.Instance.DeltaTime);

            if(Math.Abs(songLocalPosXAddition) < 5f)
            {
                if(next != null && next.IsEnabled)
                    next.SetActive(false);
                if (previous != null && previous.IsEnabled)
                    previous.SetActive(false);
            }
            else if (songLocalPosXAddition > 15f)
            {
                if(next != null)
                    next.SetActive(true);
            }
            else if (songLocalPosXAddition < -15f)
            {
                if(previous != null)
                    previous.SetActive(true);
            }

            if (!RendererMain.Instance.MainIsland.IsHovering)
            {
                var smallContainerSize = IslandSize() - songSizeAddition;
                smallContainerSize -= sCD;
                smallWidgetsContainer.LocalPosition.X = -RendererMain.Instance.MainIsland.LocalPosition.X;
                smallWidgetsContainer.Size = smallContainerSize;

                { // Left Small Widgets
                    float leftStackedPos = 0f;
                    foreach (var smallLeft in smallLeftWidgets)
                    {
                        smallLeft.Anchor.X = 0;
                        smallLeft.LocalPosition.X = leftStackedPos;

                        leftStackedPos += smallWidgetsSpacing + smallLeft.GetWidgetSize().X;
                    }
                }

                { // Right Small Widgets
                    float rightStackedPos = 0f;
                    foreach (var smallRight in smallRightWidgets)
                    {
                        smallRight.Anchor.X = 1;
                        smallRight.LocalPosition.X = rightStackedPos;

                        rightStackedPos -= smallWidgetsSpacing + smallRight.GetWidgetSize().X;
                    }
                }

                float requiredCenterWidth = 0f;
                foreach (var w in smallCenterWidgets)
                {
                    requiredCenterWidth += w.GetWidgetSize().X;
                }
                requiredCenterWidth += smallWidgetsSpacing * Math.Max(0, smallCenterWidgets.Count - 1);

                float availableWidth = smallWidgetsContainer.Size.X;

                // Minimum spacing to prevent overlap (can be 0)
                float safeSpacing = smallWidgetsSpacing;

                if (requiredCenterWidth > availableWidth)
                {
                    // Reduce spacing but never go negative
                    safeSpacing = Math.Max(
                        0f,
                        (availableWidth - requiredCenterWidth + smallWidgetsSpacing * (smallCenterWidgets.Count - 1))
                        / Math.Max(1, smallCenterWidgets.Count - 1)
                    );
                }

                { // Center Small Widgets (overlap-safe)
                    float totalWidth = 0f;

                    foreach (var w in smallCenterWidgets)
                        totalWidth += w.GetWidgetSize().X;

                    totalWidth += safeSpacing * Math.Max(0, smallCenterWidgets.Count - 1);

                    float startX = -totalWidth / 2f;

                    foreach (var w in smallCenterWidgets)
                    {
                        w.Anchor.X = 0.5f;
                        w.LocalPosition.X = startX + w.GetWidgetSize().X / 2f;
                        startX += w.GetWidgetSize().X + safeSpacing;
                    }
                }
            }
            else if (RendererMain.Instance.MainIsland.IsHovering)
            {
                topContainer.Size = new Vec2(RendererMain.Instance.MainIsland.currSize.X - 30, 30);

                var bigContainerSize = IslandSizeBig();
                bigContainerSize -= bCD;
                bigWidgetsContainer.Size = bigContainerSize;

                // Ensure media UIObject has a valid size when activated so its Draw/Update layout isn't zero-sized
                if (media != null)
                {
                    if (currentBigMenuMode == BigMenuMode.Media)
                    {
                        // Give media panel its own dedicated size (independent from widgets layout)
                        float mediaWidth = 450f;
                        float mediaHeight = 160f;
                        media.Size = new Vec2(mediaWidth, mediaHeight);

                        // Centre horizontally and keep it anchored near the bottom like before
                        media.LocalPosition.X = 0f;
                    }
                    else
                    {
                        // For non-media modes keep the previous behavior so other views still work
                        media.Size = bigContainerSize;
                    }
                }

                { // Big Widgets

                    widgetsInOneLine.Clear();

                    float lastBiggestY = 0f;

                    for(int i = 0; i < bigWidgets.Count; i++)
                    {
                        int line = i / maxBigWidgetInOneRow; // Correct line calculation

                        widgetsInOneLine.Add(bigWidgets[i]);
                        bigWidgets[i].Anchor.Y = 1;
                        bigWidgets[i].Anchor.X = 0.5f;
                        lastBiggestY = (float)Math.Max(lastBiggestY, bigWidgets[i].GetWidgetSize().Y);

                        bigWidgets[i].LocalPosition.Y = -line * (lastBiggestY + bigWidgetsSpacing);

                        if ((i) % maxBigWidgetInOneRow == 1)
                        {
                            lastBiggestY = 0f;
                            CenterWidgets(widgetsInOneLine, bigWidgetsContainer);
                            widgetsInOneLine.Clear();
                            line++;
                        }
                    }

                    // If there are leftover widgets in the last row, center them as well
                    if (widgetsInOneLine.Count > 0)
                    {
                        CenterWidgets(widgetsInOneLine, bigWidgetsContainer);
                        widgetsInOneLine.Clear();
                    }
                }
            }

            cycle++;
        }

        public void CenterWidgets(List<WidgetBase> widgets, UIObject container)
        {
            float spacing = bigWidgetsSpacing;
            float stackedXPosition = 0f;

            float fullWidth = 0f;

            for (int i = 0; i < widgets.Count; i++)
            {
                fullWidth += widgets[i].GetWidgetSize().X;

                widgets[i].LocalPosition.X = stackedXPosition + widgets[i].GetWidgetSize().X / 2 - container.Size.X / 2;
                stackedXPosition += widgets[i].GetWidgetSize().X + spacing;
            }

            float offset = fullWidth / 2 - container.Size.X / 2 + bigWidgetsSpacing / 2;

            for (int i = 0; i< widgets.Count; i++)
            {
                widgets[i].LocalPosition.X -= offset;
            }
        }

        // Border should only be rendered if on island mode instead of notch
        public override Col IslandBorderColor()
        {
            IslandMode mode = Settings.IslandMode; // Reads either Island or Notch as value
            if (mode == IslandMode.Island && RendererMain.Instance.MainIsland.IsHovering) return new Col(0.5f, 0.5f, 0.5f);
            else return new Col(0, 0, 0, 0); // Render transparent if island mode is Notch
        }
    }
}
