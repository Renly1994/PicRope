using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ScreenLine
{
    internal enum ShotAction { Copy, Open, Edit, Remove, Trash, Save, Locate, RemoveLeft, RemoveRight, RemoveOthers, RemoveAll }

    internal struct BulkCounts
    {
        public int Left;
        public int Right;
        public int Others;
    }

    internal sealed class ShelfWindow : Window
    {
        readonly Gallery gallery;
        readonly Action<ShotAction, CaptureItem> act;
        readonly ScrollViewer viewport = new ScrollViewer();
        readonly Canvas content = new Canvas();
        readonly System.Windows.Shapes.Path rope = new System.Windows.Shapes.Path();
        readonly LinearGradientBrush edgeMask = new LinearGradientBrush(new GradientStopCollection {
            new GradientStop(Colors.White, 0), new GradientStop(Colors.White, 0.03),
            new GradientStop(Colors.White, 0.97), new GradientStop(Colors.White, 1)
        }, new Point(0, 0), new Point(1, 0));
        readonly Dictionary<string, ShotCard> cards = new Dictionary<string, ShotCard>(StringComparer.OrdinalIgnoreCase);
        readonly Border hint = new Border();
        readonly Button enable = new Button();
        readonly TextBlock helpTitle = new TextBlock();
        readonly TextBlock helpDetail = new TextBlock();
        readonly TextBlock basketCount = new TextBlock();
        readonly Image basketImage = new Image { Width = 112, Stretch = Stretch.Uniform, IsHitTestVisible = false };
        readonly RotateTransform basketTurn = new RotateTransform();
        readonly ScaleTransform basketCounterScale = new ScaleTransform(1, 1);
        readonly SpringMotion basketMotion = new SpringMotion(0.5, 0.5, 0);
        readonly SpringMotion basketCounterMotion = new SpringMotion(0.28, 0.65, 1);
        int pendingPictures;
        readonly Button openBasket = new Button();
        internal readonly Button BasketButton = new Button();
        public event Action BasketRequested;
        internal readonly Button TextButton = new Button();
        readonly EnvelopeIcon envelopeImage = new EnvelopeIcon { Width = 96, Height = 86, Stretch = Stretch.Fill, IsHitTestVisible = false };
        readonly ClothesClip textClip = new ClothesClip();
        readonly TextBlock textCount = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center };
        readonly Border textCounter = new Border();
        public event Action TextRequested;
        public event Action RopeHidden;
        readonly TranslateTransform slide = new TranslateTransform(0, -240);
        readonly SpringMotion revealMotion = new SpringMotion(0.42, 0.82, -240);
        readonly SpringMotion ropeMotion = new SpringMotion(0.55, 0.78, 18);
        readonly SpringMotion scrollMotion = new SpringMotion(0.55, 0.78, 0);
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Random random = new Random();
        readonly List<ShotFlight> flights = new List<ShotFlight>();
        readonly Dictionary<string, PinnedImageWindow> pins = new Dictionary<string, PinnedImageWindow>(StringComparer.OrdinalIgnoreCase);
        bool closing;
        ImagePreviewWindow preview;
        readonly DispatcherTimer previewDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        ShotCard previewCandidate, previewCard;
        int previewToken;
        readonly DispatcherTimer pointer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        DateTime edgeSince = DateTime.MaxValue, awaySince = DateTime.MaxValue, peekUntil;
        DateTime nextGust;
        double lastFrame, hideTime, hideFrom, drawnWidth = -1, drawnSag = -1;
        double lastScrollOffset;
        bool rendering;
        bool pinned, previousDown, suppressEdge;
        string viewingName;
        IntPtr viewingWindow;
        DateTime viewerSearchUntil;
        public bool Revealed { get; private set; }
        public IntPtr Handle { get; private set; }
        public Forms.Screen Display { get; private set; }
        public LineTheme Appearance { get; private set; }
        internal bool ChoosingAppearance { get; set; }
        internal bool ChoosingBasket { get; set; }
        internal bool ChoosingTexts { get; set; }
        double PhotoWidth { get { return Math.Max(1, (ActualWidth > 0 ? ActualWidth : Width) - 308); } }
        double LineWidth { get { return PhotoWidth + 260; } }
        double ScrollLimit { get { return Math.Max(0, content.Width - PhotoWidth); } }
        internal Point TextAnchor
        {
            get
            {
                var point = textCounter.PointToScreen(new Point(textCounter.ActualWidth / 2, textCounter.ActualHeight + 4));
                point.Y -= slide.Y * WindowsNative.MonitorScale(Display);
                return point;
            }
        }

        public ShelfWindow(Gallery gallery, Action<ShotAction, CaptureItem> act, Action enableClipboard)
        {
            this.gallery = gallery;
            this.act = act;
            Title = "PicRope · 图绳";
            Width = 1000;
            Height = 220;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            viewport.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            viewport.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            viewport.Content = content;
            viewport.Margin = new Thickness(24, 0, 284, 0);
            viewport.RenderTransform = slide;
            viewport.ScrollChanged += delegate(object sender, ScrollChangedEventArgs e)
            {
                if (e.ExtentWidthChange != 0 || e.ViewportWidthChange != 0) lastScrollOffset = viewport.HorizontalOffset;
                foreach (var card in cards.Values) card.Frame(0, ropeMotion.Position, LineWidth, viewport.HorizontalOffset);
                UpdateEdgeMask();
            };
            viewport.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e)
            {
                double target = Math.Max(0, Math.Min(ScrollLimit, scrollMotion.Target - e.Delta));
                if (Math.Abs(target - scrollMotion.Target) > 0.01)
                {
                    scrollMotion.Target = target;
                    nextGust = DateTime.UtcNow.AddSeconds(7 + random.NextDouble() * 17);
                }
                e.Handled = true;
            };
            var root = new Grid();
            var line = new Canvas { Margin = new Thickness(24, 0, 24, 0), RenderTransform = slide, IsHitTestVisible = false };
            line.Children.Add(rope); root.Children.Add(line);
            root.Children.Add(viewport); Content = root;
            Appearance = LineTheme.FromId(gallery.Store.State.Theme);
            RopeLine.Apply(rope, Appearance);

            var basket = new Canvas { Width = 156, Height = 220, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 24, 0), RenderTransform = slide };
            var basketBody = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            basketImage.Source = PictureBasket.Source(Appearance);
            RenderOptions.SetBitmapScalingMode(basketImage, BitmapScalingMode.HighQuality);
            basketImage.RenderTransform = basketTurn;
            Point hook = PictureBasket.Hook(Appearance); basketTurn.CenterX = hook.X; basketTurn.CenterY = hook.Y;
            basketImage.Height = 112 * basketImage.Source.Height / basketImage.Source.Width;
            basketBody.Children.Add(basketImage);
            basketCount.FontFamily = new FontFamily("Segoe UI"); basketCount.FontSize = 13; basketCount.FontWeight = FontWeights.SemiBold;
            basketCount.Foreground = Brushes.White; basketCount.TextAlignment = TextAlignment.Center;
            basketCount.RenderTransformOrigin = new Point(0.5, 0.5); basketCount.RenderTransform = basketCounterScale;
            basketBody.Children.Add(new Border { Child = basketCount, Background = LightTheme.Brush("#D920242C"),
                BorderBrush = LightTheme.Brush("#40FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 1, 6, 2), MinWidth = 26, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
            BasketButton.Content = basketBody; BasketButton.Width = 144; BasketButton.Height = 154; BasketButton.Focusable = false; BasketButton.Cursor = Cursors.Hand;
            BasketButton.ToolTip = "打开图片篮子";
            BasketButton.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border Background='#02FFFFFF'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Top'/></Border></ControlTemplate>");
            BasketButton.Click += delegate { if (BasketRequested != null) BasketRequested(); };
            BasketButton.MouseEnter += delegate { basketMotion.Target = 1.3; StartRendering(); };
            BasketButton.MouseLeave += delegate { basketMotion.Target = 0; };
            System.Windows.Automation.AutomationProperties.SetName(BasketButton, "打开图片篮子");
            basket.Children.Add(BasketButton); Canvas.SetLeft(BasketButton, 6); Canvas.SetTop(BasketButton, 20);
            root.Children.Add(basket);

            var textHolder = new Canvas { Width = 112, Height = 220, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 166, 0), RenderTransform = slide };
            var textBody = new Grid { Width = 96, Height = 154 };
            envelopeImage.Apply(Appearance);
            envelopeImage.HorizontalAlignment = HorizontalAlignment.Center; envelopeImage.VerticalAlignment = VerticalAlignment.Top;
            envelopeImage.Margin = new Thickness(0, 20, 0, 0);
            RenderOptions.SetBitmapScalingMode(envelopeImage, BitmapScalingMode.HighQuality); textBody.Children.Add(envelopeImage);
            textClip.Apply(Appearance, 2); textClip.HorizontalAlignment = HorizontalAlignment.Center; textClip.VerticalAlignment = VerticalAlignment.Top;
            textBody.Children.Add(textClip);
            textCounter.Child = textCount; textCounter.Background = LightTheme.Brush("#D920242C");
            textCounter.BorderBrush = LightTheme.Brush("#40FFFFFF"); textCounter.BorderThickness = new Thickness(1); textCounter.CornerRadius = new CornerRadius(6);
            textCounter.Padding = new Thickness(6, 1, 6, 2); textCounter.MinWidth = 26;
            textCounter.HorizontalAlignment = HorizontalAlignment.Center; textCounter.VerticalAlignment = VerticalAlignment.Top;
            textBody.Children.Add(textCounter);
            TextButton.Content = textBody; TextButton.Width = 96; TextButton.Height = 154; TextButton.Focusable = false; TextButton.Cursor = Cursors.Hand;
            TextButton.Template = BasketButton.Template; TextButton.ToolTip = "打开文字";
            TextButton.MouseEnter += delegate { envelopeImage.Expand(true); StartRendering(); };
            TextButton.MouseLeave += delegate { envelopeImage.Expand(false); };
            AutomationProperties.SetName(TextButton, "打开文字");
            TextButton.Click += delegate { if (TextRequested != null) TextRequested(); };
            textHolder.Children.Add(TextButton); Canvas.SetLeft(TextButton, 8);
            root.Children.Add(textHolder); UpdateTexts();

            var help = new StackPanel { Margin = new Thickness(20, 14, 20, 14) };
            helpTitle.FontSize = 15; helpTitle.FontWeight = FontWeights.SemiBold; helpTitle.Foreground = Brushes.Black; helpTitle.HorizontalAlignment = HorizontalAlignment.Center;
            helpDetail.FontSize = 12; helpDetail.Foreground = Brushes.DimGray; helpDetail.Margin = new Thickness(0, 8, 0, 12); helpDetail.HorizontalAlignment = HorizontalAlignment.Center;
            help.Children.Add(helpTitle); help.Children.Add(helpDetail);
            openBasket.Content = "打开图片篮子"; openBasket.Style = LightTheme.ButtonStyle; openBasket.Tag = "primary";
            openBasket.HorizontalAlignment = HorizontalAlignment.Center; openBasket.Margin = new Thickness(0, 0, 0, 8);
            openBasket.Click += delegate { if (BasketRequested != null) BasketRequested(); }; help.Children.Add(openBasket);
            enable.Content = "开启剪贴板收集";
            enable.Style = LightTheme.ButtonStyle;
            enable.Tag = "primary";
            enable.Padding = new Thickness(14, 5, 14, 5);
            enable.HorizontalAlignment = HorizontalAlignment.Center;
            enable.Focusable = false;
            enable.Click += delegate { enableClipboard(); Update(); };
            help.Children.Add(enable);
            hint.Child = help;
            hint.Width = 380;
            hint.Background = new SolidColorBrush(Color.FromArgb(248, 255, 255, 255));
            hint.CornerRadius = new CornerRadius(16);
            hint.BorderBrush = new SolidColorBrush(Color.FromRgb(218, 224, 232));
            hint.BorderThickness = new Thickness(1);
            hint.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.18 };
            content.Children.Add(hint);

            SourceInitialized += delegate
            {
                Handle = new WindowInteropHelper(this).Handle;
                WindowsNative.PrepareOverlay(Handle);
                WindowsNative.PassMouse(Handle, true);
                HwndSource.FromHwnd(Handle).AddHook(Messages);
                MoveToDisplay();
                pointer.Start();
            };
            SizeChanged += delegate { LayoutCards(); };
            pointer.Tick += Tick;
            previewDelay.Tick += delegate { previewDelay.Stop(); ShowHoverPreview(); };
            gallery.Changed += Update;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaysChanged;
            Closed += delegate
            {
                closing = true;
                HidePreview();
                foreach (var pin in pins.Values.ToArray()) pin.Close();
                pointer.Stop();
                StopRendering();
                foreach (var flight in flights.ToArray()) flight.Close();
                gallery.Changed -= Update;
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
                foreach (var card in cards.Values) card.Dispose();
            };
            Update();
        }

        IntPtr Messages(IntPtr window, int message, IntPtr w, IntPtr l, ref bool handled)
        {
            if (message == 0x21) { handled = true; return new IntPtr(3); }
            if (message == 0x02E0) Dispatcher.BeginInvoke(new Action(delegate { if (Display != null) MoveToDisplay(); }));
            return IntPtr.Zero;
        }

        void DisplaysChanged(object sender, EventArgs args)
        {
            Dispatcher.BeginInvoke(new Action(MoveToDisplay));
        }

        Forms.Screen SelectedDisplay()
        {
            string device = gallery.Store.State.DisplayDevice;
            if (string.IsNullOrEmpty(device)) return WindowsNative.ScreenAtPointer();
            var screens = WindowsNative.Screens;
            return Array.Find(screens, screen => screen.DeviceName == device) ?? Array.Find(screens, screen => screen.Primary);
        }

        void MoveToDisplay()
        {
            HidePreview();
            var screen = SelectedDisplay();
            Display = screen;
            double scale = WindowsNative.MonitorScale(screen);
            Width = screen.WorkingArea.Width / scale;
            if (Handle != IntPtr.Zero)
                WindowsNative.SetWindowPos(Handle, new IntPtr(-1), screen.WorkingArea.Left, screen.WorkingArea.Top, screen.WorkingArea.Width, (int)Math.Round(Height * scale), 0x10);
            LayoutCards();
        }

        internal void SetDisplay(string device)
        {
            string previous = gallery.Store.State.DisplayDevice;
            gallery.Store.State.DisplayDevice = device;
            try { gallery.Store.Save(); }
            catch { gallery.Store.State.DisplayDevice = previous; throw; }
            MoveToDisplay();
            Reveal(true, true, 0);
        }

        public void Update()
        {
            var retained = new HashSet<string>(gallery.Items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            foreach (string gone in cards.Keys.Where(k => !retained.Contains(k)).ToArray())
            {
                if (previewCandidate == cards[gone] || previewCard == cards[gone]) HidePreview();
                cards[gone].Dispose();
                PinnedImageWindow pin;
                if (pins.TryGetValue(gone, out pin)) pin.Close();
                content.Children.Remove(cards[gone]);
                cards.Remove(gone);
            }
            foreach (var item in gallery.Items)
            {
                ShotCard card;
                if (!cards.TryGetValue(item.Path, out card))
                {
                    card = new ShotCard(item, act, BulkCountsFor);
                    card.ApplyTheme(Appearance, gallery.Items.IndexOf(item));
                    var mounted = card;
                    card.Grip.Click += async delegate { await ReleasePhoto(mounted); };
                    card.Surface.MouseEnter += delegate { HoverPreview(mounted); };
                    card.Surface.MouseLeave += delegate { LeavePreview(mounted); };
                    card.Surface.PreviewMouseLeftButtonDown += delegate { HidePreview(); };
                    card.ActionMenu.Opened += delegate { HidePreview(); };
                    cards.Add(item.Path, card);
                    content.Children.Add(card);
                }
                bool changed = card.UpdateImage();
                if (changed && previewCard == card) HidePreview();
                PinnedImageWindow pinnedImage;
                if (changed && pins.TryGetValue(item.Path, out pinnedImage)) pinnedImage.UpdateImage();
            }
            hint.Visibility = gallery.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            helpTitle.Text = gallery.Store.State.AutoHang ? "截图会挂在这里" : "从图片篮子里挑几张挂上";
            helpDetail.Text = gallery.Store.State.AutoHang ? "Win + PrtScn · 或开启剪贴板截图收集" : "截图会先收好，需要时再挂上绳子";
            openBasket.Visibility = gallery.Store.State.AutoHang ? Visibility.Collapsed : Visibility.Visible;
            int pending = gallery.Collected.Count(i => !gallery.Contains(i.Path));
            if (pending > pendingPictures && Revealed && !flights.Any(flight => flight.Collecting || flight.Falling)) BasketReceived();
            pendingPictures = pending; basketCount.Text = pending.ToString();
            System.Windows.Automation.AutomationProperties.SetItemStatus(BasketButton, basketCount.Text + " 张待挂");
            enable.Visibility = gallery.Store.State.CollectClipboard ? Visibility.Collapsed : Visibility.Visible;
            LayoutCards();
        }

        void LayoutCards()
        {
            double available = PhotoWidth;
            var hanging = gallery.Items.Where(item => !cards[item.Path].Detached).ToArray();
            content.Width = Math.Max(available, hanging.Length * 172 + 32);
            content.Height = 220;
            scrollMotion.Target = Math.Max(0, Math.Min(ScrollLimit, scrollMotion.Target));
            if (ScrollLimit == 0) { scrollMotion.Snap(0); viewport.ScrollToHorizontalOffset(0); lastScrollOffset = 0; }
            else if (scrollMotion.Position > ScrollLimit) { scrollMotion.Position = ScrollLimit; scrollMotion.Velocity = 0; }
            ropeMotion.Target = 18 + Math.Min(7, hanging.Length) * 1.2;
            if (!Revealed) ropeMotion.Snap(ropeMotion.Target);
            DrawRope();
            Canvas.SetLeft(hint, (content.Width - hint.Width) / 2);
            Canvas.SetTop(hint, 56);
            for (int i = 0; i < hanging.Length; i++)
            {
                ShotCard card = cards[hanging[i].Path];
                double x = (content.Width - hanging.Length * 172) / 2 + i * 172;
                card.Place(x, !Revealed);
                card.Frame(0, ropeMotion.Position, LineWidth, viewport.HorizontalOffset);
            }
        }

        public void ApplyTheme(LineTheme theme)
        {
            Appearance = theme;
            RopeLine.Apply(rope, theme);
            basketImage.Source = PictureBasket.Source(theme);
            basketImage.Height = 112 * basketImage.Source.Height / basketImage.Source.Width;
            envelopeImage.Apply(theme); textClip.Apply(theme, 2);
            Point hook = PictureBasket.Hook(theme); basketTurn.CenterX = hook.X; basketTurn.CenterY = hook.Y;
            drawnWidth = -1; DrawRope();
            for (int i = 0; i < gallery.Items.Count; i++) cards[gallery.Items[i].Path].ApplyTheme(theme, i);
            foreach (var flight in flights) flight.ApplyTheme(theme);
            foreach (var entry in pins) entry.Value.ApplyTheme(theme, cards[entry.Key].ColorIndex);
        }

        void DrawRope()
        {
            if (Math.Abs(drawnWidth - LineWidth) < 0.01 && Math.Abs(drawnSag - ropeMotion.Position) < 0.01) return;
            drawnWidth = LineWidth;
            drawnSag = ropeMotion.Position;
            var geometry = new PathGeometry();
            var shape = new PathFigure { StartPoint = new Point(0, 18) };
            shape.Segments.Add(new QuadraticBezierSegment(new Point(drawnWidth / 2, 18 + 2 * drawnSag), new Point(drawnWidth, 18), true));
            geometry.Figures.Add(shape);
            rope.Data = geometry;
            Point hook = PictureBasket.Hook(Appearance);
            double fraction = (LineWidth - 156 + 22 + hook.X) / LineWidth;
            double offset = 4 * drawnSag * fraction * (1 - fraction);
            double basketTop = 18 + offset - hook.Y;
            Canvas.SetTop(BasketButton, basketTop);
            double textFraction = (PhotoWidth + 62) / LineWidth;
            double textTop = 18 + 4 * drawnSag * textFraction * (1 - textFraction) - 7;
            Canvas.SetTop(TextButton, textTop);
            double countTop = basketTop + basketImage.Height + 6 - textTop;
            textCounter.Margin = new Thickness(0, countTop, 0, 0);
            envelopeImage.Height = Math.Max(24, Math.Min(86, countTop - 26));
        }

        internal void UpdateTexts()
        {
            textCount.Text = gallery.Store.State.Texts.Count.ToString();
            AutomationProperties.SetItemStatus(TextButton, textCount.Text + " 条文字");
        }

        void UpdateEdgeMask()
        {
            double fade = Math.Min(0.08, 18 / PhotoWidth);
            edgeMask.GradientStops[0].Color = viewport.HorizontalOffset > 1 ? Colors.Transparent : Colors.White;
            edgeMask.GradientStops[1].Offset = fade;
            edgeMask.GradientStops[2].Offset = 1 - fade;
            edgeMask.GradientStops[3].Color = viewport.HorizontalOffset < ScrollLimit - 1 ? Colors.Transparent : Colors.White;
            viewport.OpacityMask = ScrollLimit > 0 ? edgeMask : null;
        }

        public void Toggle()
        {
            viewingName = null;
            viewingWindow = IntPtr.Zero;
            if (Revealed) Reveal(false, false, 0);
            else { MoveToDisplay(); Reveal(true, true, 0); }
        }

        public void FollowImageViewer(string path)
        {
            viewingName = System.IO.Path.GetFileName(path);
            viewingWindow = IntPtr.Zero;
            viewerSearchUntil = DateTime.UtcNow.AddSeconds(5);
            awaySince = DateTime.MaxValue;
        }

        bool ViewingImage(DateTime now)
        {
            if (viewingName == null) return false;
            IntPtr foreground = WindowsNative.GetForegroundWindow();
            if (viewingWindow == IntPtr.Zero)
            {
                if (foreground != Handle && WindowsNative.WindowTitle(foreground).IndexOf(viewingName, StringComparison.OrdinalIgnoreCase) >= 0)
                    viewingWindow = foreground;
                else if (now < viewerSearchUntil) return true;
                else { viewingName = null; return false; }
            }
            if (!WindowsNative.IsWindowVisible(viewingWindow))
            {
                viewingName = null;
                viewingWindow = IntPtr.Zero;
                Reveal(true, false, 0);
                return true;
            }
            return foreground == viewingWindow;
        }

        public void ShowNewCapture(CaptureItem item, Point? origin)
        {
            MoveToDisplay();
            Reveal(true, false, 2);
            scrollMotion.Target = ScrollLimit;
            ropeMotion.Velocity += 8;
            if (!Revealed || !origin.HasValue || flights.Count(f => !f.Falling && !f.Collecting) >= 2) return;
            ShotCard card = cards[item.Path];
            if (card.Detached) return;
            card.BeginFlight();
            var bounds = Display.Bounds;
            double scale = Math.Min(1, Math.Min(bounds.Width * 0.7 / item.Image.PixelWidth, bounds.Height * 0.7 / item.Image.PixelHeight));
            double width = item.Image.PixelWidth * scale, height = item.Image.PixelHeight * scale;
            double left = Math.Max(bounds.Left, Math.Min(bounds.Right - width, origin.Value.X - width));
            double top = Math.Max(bounds.Top, Math.Min(bounds.Bottom - height, origin.Value.Y - height));
            Rect start = new Rect(left, top, width, height);
            if (item.Image.PixelWidth == bounds.Width && item.Image.PixelHeight == bounds.Height)
                start = new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            PlayFlight(new ShotFlight(Display, card, start, false, card.Land));
        }

        public void ShowDiscard(CaptureItem item)
        {
            ShotCard card;
            if (!Revealed || !cards.TryGetValue(item.Path, out card) || card.Flying) return;
            PlayFlight(new ShotFlight(Display, card, card.PhotoRect(), true, delegate { }));
        }

        public void ShowCollect(CaptureItem item, bool announce = true)
        {
            ShotCard card;
            if (!Revealed || !cards.TryGetValue(item.Path, out card) || card.Flying) return;
            peekUntil = DateTime.UtcNow.AddSeconds(2);
            Action landed = announce ? (Action)BasketReceived : delegate { };
            PlayFlight(new ShotFlight(Display, card, card.PhotoRect(), false, landed, null, delegate
            {
                Point mouth = basketImage.PointToScreen(PictureBasket.Mouth(Appearance));
                double scale = WindowsNative.MonitorScale(Display);
                return new Rect(mouth.X - 28 * scale, mouth.Y, 56 * scale, 16 * scale);
            }));
        }

        internal List<CaptureItem> HangingItems
        {
            get
            {
                var hanging = new List<CaptureItem>();
                foreach (var item in gallery.Items)
                {
                    ShotCard card;
                    if (!cards.TryGetValue(item.Path, out card) || !card.Detached) hanging.Add(item);
                }
                return hanging;
            }
        }

        BulkCounts BulkCountsFor(CaptureItem item)
        {
            var hanging = HangingItems;
            int index = hanging.FindIndex(entry => FileRules.SameFile(entry.Path, item.Path));
            if (index < 0) return new BulkCounts();
            return new BulkCounts { Left = index, Right = hanging.Count - index - 1, Others = hanging.Count - 1 };
        }

        public void ShowBulkCollect(IEnumerable<CaptureItem> items)
        {
            if (!Revealed) return;
            bool started = false;
            foreach (var item in items.Take(3))
            {
                ShotCard card;
                if (!cards.TryGetValue(item.Path, out card) || card.Detached || card.Flying) continue;
                ShowCollect(item, false);
                started = true;
            }
            if (started) BasketReceived();
        }

        void BasketReceived()
        {
            if (closing || !Revealed) return;
            basketMotion.Velocity = Math.Max(-48, basketMotion.Velocity - 32);
            basketCounterMotion.Velocity = Math.Min(2, basketCounterMotion.Velocity + 1.2);
            StartRendering();
        }

        public void ShowEditedImage(string path, Rect origin, BitmapSource picture)
        {
            ShotCard card;
            if (closing || !cards.TryGetValue(path, out card) || card.Detached || card.Flying) return;
            MoveToDisplay();
            Reveal(true, false, 2);
            if (!Revealed) return;
            card.BeginFlight();
            LayoutCards();
            viewport.UpdateLayout();
            scrollMotion.Target = Math.Max(0, Math.Min(content.Width - viewport.ViewportWidth, card.MountX + 82 - viewport.ViewportWidth / 2));
            PlayFlight(new ShotFlight(Display, card, origin, false, delegate { card.Land(); ropeMotion.Velocity += 12; }, picture));
        }

        void PlayFlight(ShotFlight flight)
        {
            flights.Add(flight);
            flight.Closed += delegate { flights.Remove(flight); };
            flight.Show();
        }

        async Task ReleasePhoto(ShotCard card)
        {
            if (card.Releasing || card.Flying || card.Detached) return;
            Rect origin = card.PhotoRect();
            card.OpenClip();
            awaySince = DateTime.MaxValue;
            try
            {
                var picture = Task.Run(() => PinnedImageWindow.ReadPicture(card.Item.Path));
                await Task.Delay(40);
                var image = await picture;
                if (closing || card.IsDisposed) return;
                var window = new PinnedImageWindow(card.Item, origin, ((RotateTransform)card.RenderTransform).Angle, image, Appearance, card.ColorIndex);
                pins.Add(card.Item.Path, window);
                window.Closed += delegate
                {
                    pins.Remove(card.Item.Path);
                    if (closing || card.IsDisposed) return;
                    card.Detached = false;
                    card.ResetClip();
                    card.BeginFlight();
                    var bounds = window.ReturnBounds;
                    MoveToDisplay();
                    Reveal(true, false, 3);
                    LayoutCards();
                    viewport.UpdateLayout();
                    scrollMotion.Target = Math.Max(0, Math.Min(content.Width - viewport.ViewportWidth, card.MountX + 82 - viewport.ViewportWidth / 2));
                    PlayFlight(new ShotFlight(Display, card, bounds, false, card.Land, window.Picture));
                };
                card.DetachPhoto();
                ropeMotion.Velocity -= 24;
                LayoutCards();
                window.Show();
            }
            catch (Exception error) { gallery.Store.Log(error); card.ResetClip(); }
        }

        public void Reveal(bool shown, bool pin, double seconds)
        {
            pinned = pin;
            peekUntil = DateTime.UtcNow.AddSeconds(seconds);
            awaySince = DateTime.MaxValue;
            if (shown && WindowsNative.FullScreen(Display, Handle)) return;
            if (shown == Revealed) return;
            Revealed = shown;
            revealMotion.Target = shown ? 0 : -240;
            hideFrom = revealMotion.Position;
            hideTime = 0;
            StartRendering();
            if (!shown)
            {
                HidePreview();
                if (RopeHidden != null) RopeHidden();
                foreach (var flight in flights.ToArray()) flight.Close();
                basketMotion.Snap(0); basketTurn.Angle = 0;
                basketCounterMotion.Snap(1); basketCounterScale.ScaleX = basketCounterScale.ScaleY = 1;
                envelopeImage.Reset();
                WindowsNative.PassMouse(Handle, true);
            }
            else
            {
                if (nextGust == default(DateTime)) nextGust = DateTime.UtcNow.AddSeconds(7 + random.NextDouble() * 17);
                foreach (var card in cards.Values) card.Nudge(1.2);
            }
        }

        void StartRendering()
        {
            if (rendering) return;
            rendering = true;
            lastFrame = clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += RenderFrame;
        }

        void StopRendering()
        {
            if (!rendering) return;
            rendering = false;
            CompositionTarget.Rendering -= RenderFrame;
        }

        void RenderFrame(object sender, EventArgs args)
        {
            double now = clock.Elapsed.TotalSeconds;
            double elapsed = now - lastFrame;
            lastFrame = now;
            if (Revealed) revealMotion.Step(elapsed);
            else
            {
                hideTime += elapsed;
                double t = Math.Min(1, hideTime / 0.22);
                revealMotion.Position = hideFrom + (-240 - hideFrom) * t * t;
                revealMotion.Velocity = 2 * (-240 - hideFrom) * t / 0.22;
                if (t == 1) revealMotion.Snap(-240);
            }
            slide.Y = revealMotion.Position;
            ropeMotion.Step(elapsed);
            DrawRope();
            basketMotion.Step(elapsed); basketTurn.Angle = basketMotion.Position;
            basketCounterMotion.Step(elapsed); basketCounterScale.ScaleX = basketCounterScale.ScaleY = basketCounterMotion.Position;
            envelopeImage.Frame(elapsed);
            scrollMotion.Target = Math.Max(0, Math.Min(scrollMotion.Target, ScrollLimit));
            scrollMotion.Step(elapsed);
            if (scrollMotion.Position < 0 || scrollMotion.Position > ScrollLimit)
            {
                scrollMotion.Position = Math.Max(0, Math.Min(scrollMotion.Position, ScrollLimit));
                scrollMotion.Velocity = 0;
            }
            viewport.ScrollToHorizontalOffset(scrollMotion.Position);
            double scrollVelocity = Revealed && elapsed > 0 ? (viewport.HorizontalOffset - lastScrollOffset) / elapsed : 0;
            lastScrollOffset = viewport.HorizontalOffset;
            foreach (var card in cards.Values)
            {
                card.ScrollWith(scrollVelocity);
                card.Frame(elapsed, ropeMotion.Position, LineWidth, viewport.HorizontalOffset);
            }
            if (!Revealed && hideTime >= 0.22) StopRendering();
        }

        bool OverControl(WindowsNative.Point cursor)
        {
            Point local = PointFromScreen(new Point(cursor.X, cursor.Y));
            var photoArea = viewport.TransformToAncestor(this).TransformBounds(new Rect(viewport.RenderSize));
            IEnumerable<FrameworkElement> targets = cards.Values.Where(c => !c.Flying && !c.Detached).SelectMany(c => new FrameworkElement[] { c.Surface, c.Grip });
            if (hint.IsVisible) targets = targets.Concat(new[] { hint });
            targets = targets.Concat(new[] { BasketButton, TextButton });
            foreach (var target in targets)
            {
                var bounds = target.TransformToAncestor(this).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
                if (target != BasketButton && target != TextButton) bounds.Intersect(photoArea);
                if (bounds.Contains(local)) return true;
            }
            return false;
        }

        void Tick(object sender, EventArgs args)
        {
            var cursor = WindowsNative.Cursor();
            var screen = SelectedDisplay();
            bool down = (WindowsNative.GetAsyncKeyState(1) & 0x8000) != 0;
            bool nearTop = cursor.X >= screen.Bounds.Left && cursor.X < screen.Bounds.Right
                && cursor.Y >= screen.Bounds.Top && cursor.Y <= Math.Max(screen.Bounds.Top + 2, screen.WorkingArea.Top - 1);
            bool over = Revealed && OverControl(cursor);
            var now = DateTime.UtcNow;
            bool viewing = ViewingImage(now);
            if (!nearTop) suppressEdge = false;
            if (nearTop && down && !previousDown && !over && !viewing)
            {
                suppressEdge = true;
                viewingName = null;
                viewingWindow = IntPtr.Zero;
                Reveal(false, false, 0);
            }
            previousDown = down;
            if (nearTop && !down && !suppressEdge && !WindowsNative.FullScreen(screen, Handle))
            {
                if (edgeSince == DateTime.MaxValue) edgeSince = now;
                if (now - edgeSince >= TimeSpan.FromMilliseconds(320) && (!Revealed || screen.DeviceName != Display.DeviceName))
                {
                    MoveToDisplay();
                    Reveal(true, false, 0);
                }
            }
            else edgeSince = DateTime.MaxValue;
            if (WindowsNative.FullScreen(Display, Handle)) { Reveal(false, false, 0); return; }
            bool busy = cards.Values.Any(c => c.Busy);
            WindowsNative.PassMouse(Handle, !Revealed || (!over && !busy));
            if (!Revealed) return;
            if (previewCandidate != null && (!OverCard(previewCandidate, cursor) || previewCandidate.Busy))
            {
                previewCandidate = null;
                previewDelay.Stop();
            }
            if (previewCard != null && (!OverCard(previewCard, cursor) || previewCard.Busy)) HidePreview();
            if (cards.Count > 0 && Math.Abs(scrollMotion.Velocity) < 8 && Math.Abs(scrollMotion.Target - viewport.HorizontalOffset) < 0.5 && !busy && now >= nextGust)
            {
                nextGust = now.AddSeconds(7 + random.NextDouble() * 17);
                int direction = random.Next(2) == 0 ? -1 : 1;
                foreach (var card in cards.Values) card.Breeze(random.NextDouble() * 0.35, direction * (1.6 + random.NextDouble() * 1.8));
                StartRendering();
            }
            WindowsNative.Rectangle bounds;
            WindowsNative.GetWindowRect(Handle, out bounds);
            bool inside = cursor.X >= bounds.Left && cursor.X < bounds.Right && cursor.Y >= Display.Bounds.Top && cursor.Y < bounds.Bottom;
            if (inside) pinned = false;
            if (inside || pinned || busy || viewing || ChoosingAppearance || ChoosingBasket || ChoosingTexts || now < peekUntil) awaySince = DateTime.MaxValue;
            else
            {
                if (awaySince == DateTime.MaxValue) awaySince = now;
                if (now - awaySince > TimeSpan.FromMilliseconds(1200)) Reveal(false, false, 0);
            }
        }

        public void ShowCopied(string path) { if (cards.ContainsKey(path)) cards[path].FlashCopied(); }

        bool OverCard(ShotCard card, WindowsNative.Point cursor)
        {
            if (Display == null) return false;
            var surface = card.Surface;
            Point origin = surface.PointToScreen(new Point(0, 0));
            double dpi = WindowsNative.MonitorScale(Display);
            return cursor.X >= origin.X && cursor.X < origin.X + surface.ActualWidth * dpi
                && cursor.Y >= origin.Y && cursor.Y < origin.Y + surface.ActualHeight * dpi;
        }

        void HoverPreview(ShotCard card)
        {
            if (!Revealed || card == null || closing) return;
            previewCandidate = card;
            previewDelay.Stop();
            previewDelay.Start();
        }

        void LeavePreview(ShotCard card)
        {
            if (previewCandidate == card) { previewCandidate = null; previewDelay.Stop(); }
            if (previewCard == card) HidePreview();
        }

        internal void HidePreview()
        {
            previewToken++;
            previewCandidate = null;
            previewCard = null;
            previewDelay.Stop();
            var window = preview;
            preview = null;
            if (window != null) window.Close();
        }

        async void ShowHoverPreview()
        {
            var card = previewCandidate;
            if (card == null || !Revealed || card.Busy || card.IsDisposed || closing) return;
            var image = card.Item.Image;
            if (image == null || image.PixelWidth <= 0 || image.PixelHeight <= 0) return;
            double dpi = WindowsNative.MonitorScale(Display);
            var area = Display.WorkingArea;
            var surface = card.Surface;
            Point origin = surface.PointToScreen(new Point(0, 0));
            double cardWidth = surface.ActualWidth * dpi, cardHeight = surface.ActualHeight * dpi;
            double maxWidth = Math.Min(640 * dpi, area.Width - 40 * dpi);
            double maxHeight = Math.Min(640 * dpi, area.Bottom - (origin.Y + cardHeight) - 32 * dpi);
            if (maxWidth < 100 * dpi || maxHeight < 100 * dpi) return;
            double fit = Math.Min(maxWidth / image.PixelWidth, maxHeight / image.PixelHeight);
            fit = Math.Min(fit, 3);
            double imageWidth = Math.Max(1, Math.Round(image.PixelWidth * fit));
            double imageHeight = Math.Max(1, Math.Round(image.PixelHeight * fit));
            double windowWidth = imageWidth + 38 * dpi, windowHeight = imageHeight + 38 * dpi;
            double left = Math.Max(area.Left + 12 * dpi, Math.Min(area.Right - windowWidth - 12 * dpi, origin.X + cardWidth / 2 - windowWidth / 2));
            double top = Math.Max(area.Top + 8 * dpi, Math.Min(origin.Y + cardHeight + 10 * dpi, area.Bottom - windowHeight - 8 * dpi));
            int decodeWidth = (int)Math.Min(Math.Round(imageWidth), image.PixelWidth);
            int token = ++previewToken;
            BitmapSource picture;
            try { picture = await Task.Run(delegate { return DecodePreview(card.Item.Path, image, decodeWidth); }); }
            catch (Exception error) { gallery.Store.Log(error); return; }
            if (token != previewToken || previewCandidate != card || !Revealed || closing) return;
            try
            {
                previewCard = card;
                preview = new ImagePreviewWindow();
                preview.Display(picture, imageWidth, imageHeight, new Rect(left, top, windowWidth, windowHeight), dpi);
            }
            catch (Exception error)
            {
                HidePreview();
                gallery.Store.Log(error);
            }
        }

        static BitmapSource DecodePreview(string path, ImageContent image, int width)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bitmap.StreamSource = stream;
                if (width > 0 && width < image.PixelWidth) bitmap.DecodePixelWidth = width;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }

        internal ShotCard CardFor(string path) { return cards[path]; }
        internal ImagePreviewWindow LargePreview { get { return preview; } }
        internal bool PreviewVisible { get { return preview != null && preview.Shown; } }
        internal IEnumerable<ShotFlight> MotionWindows { get { return flights; } }
        internal IEnumerable<PinnedImageWindow> PinWindows { get { return pins.Values; } }
        internal bool Animating { get { return rendering; } }
    }

    internal sealed class ShotCard : Canvas, IDisposable
    {
        public readonly CaptureItem Item;
        public readonly Border Surface = new Border();
        internal readonly Button Grip = new Button();
        readonly TranslateTransform clipLift = new TranslateTransform();
        readonly ScaleTransform clipScale = new ScaleTransform(1, 1);
        readonly SpringMotion clipMotion = new SpringMotion(0.25, 0.8, 0);
        readonly ClothesClip clip = new ClothesClip();
        readonly Image photo = new Image();
        readonly Button close = new Button();
        readonly Border copied = new Border();
        readonly RotateTransform tilt = new RotateTransform(0, 82, 8);
        Point position;
        readonly ScaleTransform photoScale = new ScaleTransform(1, 1);
        readonly SpringMotion xMotion = new SpringMotion(0.55, 0.78, 0);
        readonly SpringMotion drop = new SpringMotion(0.42, 0.72, -46) { Target = 0 };
        readonly SpringMotion swing = new SpringMotion(2 * Math.PI / Math.Sqrt(46), 2.6 / (2 * Math.Sqrt(46)), 16) { Target = 0 };
        readonly SpringMotion scaleMotion = new SpringMotion(0.3, 0.6, 1);
        readonly DispatcherTimer hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(480) };
        readonly DispatcherTimer flash = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        readonly Action<ShotAction, CaptureItem> act;
        readonly Func<CaptureItem, BulkCounts> bulkCounts;
        readonly ImageMenu menu;
        internal ImageMenu ActionMenu { get { return menu; } }
        Point start;
        bool pressed, held, dragging, placed, hovering, gripHover;
        double entryTime, breezeDelay = -1, breezeAngle;
        ImageContent displayed;
        public bool Busy { get { return pressed || dragging || Releasing || menu.IsOpen; } }
        public bool Flying { get; private set; }
        public bool Detached { get; set; }
        public bool Releasing { get; private set; }
        public bool IsDisposed { get; private set; }
        public readonly double Tilt;
        public LineTheme Appearance { get; private set; }
        public int ColorIndex { get; private set; }
        internal double MountX { get { return xMotion.Target; } }

        public ShotCard(CaptureItem item, Action<ShotAction, CaptureItem> act, Func<CaptureItem, BulkCounts> bulkCounts)
        {
            Item = item;
            Appearance = LineTheme.All[0];
            this.act = act;
            this.bulkCounts = bulkCounts;
            Width = 164;
            Height = 178;
            RenderTransform = tilt;
            Tilt = (item.Path.GetHashCode() & 7) * 0.7 - 2.45;
            Opacity = 0;
            Surface.RenderTransform = photoScale;
            Surface.RenderTransformOrigin = new Point(0.5, 0);
            Surface.Background = new SolidColorBrush(Color.FromArgb(245, 255, 255, 255));
            Surface.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 225, 233));
            Surface.BorderThickness = new Thickness(1);
            Surface.CornerRadius = new CornerRadius(12);
            Surface.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 4, Opacity = 0.2 };
            var imageLayer = new Grid();
            photo.Stretch = Stretch.Uniform;
            photo.Margin = new Thickness(6);
            imageLayer.Children.Add(photo);
            close.Content = LightTheme.CloseIcon();
            close.Style = LightTheme.ButtonStyle;
            close.MinWidth = 0;
            close.Width = close.Height = 22;
            close.Padding = new Thickness(0);
            close.Margin = new Thickness(3);
            close.HorizontalAlignment = HorizontalAlignment.Left;
            close.VerticalAlignment = VerticalAlignment.Top;
            close.Focusable = false;
            close.Opacity = 0;
            close.ToolTip = "删除图片（移到回收站）";
            close.Click += delegate { act(ShotAction.Trash, item); };
            imageLayer.Children.Add(close);
            Surface.Child = imageLayer;
            Children.Add(Surface);
            var clipTransform = new TransformGroup();
            clipTransform.Children.Add(clipScale); clipTransform.Children.Add(clipLift);
            clip.RenderTransform = clipTransform;
            clip.RenderTransformOrigin = new Point(0.5, 0.5);
            clip.HorizontalAlignment = HorizontalAlignment.Center;
            clip.VerticalAlignment = VerticalAlignment.Top;
            clip.Margin = new Thickness(0, 4, 0, 0);
            Grip.Width = 28; Grip.Height = 34;
            Grip.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border Background='Transparent'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Top'/></Border></ControlTemplate>");
            Grip.Focusable = false;
            Grip.Cursor = Cursors.Hand;
            Grip.ToolTip = new ToolTip { Content = "贴到桌面", Style = LightTheme.HintStyle,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Right, HorizontalOffset = 6 };
            ToolTipService.SetInitialShowDelay(Grip, 800);
            ToolTipService.SetBetweenShowDelay(Grip, 0);
            ToolTipService.SetShowDuration(Grip, 2500);
            System.Windows.Automation.AutomationProperties.SetName(Grip, "贴到桌面");
            Grip.Content = clip;
            Grip.MouseEnter += delegate { gripHover = true; };
            Grip.MouseLeave += delegate { gripHover = false; };
            Children.Add(Grip);
            SetLeft(Grip, 68); SetTop(Grip, -4);
            copied.Width = 84;
            copied.Padding = new Thickness(10, 4, 10, 4);
            copied.Background = Brushes.White;
            copied.BorderBrush = LightTheme.Brush("#DCE1E9");
            copied.BorderThickness = new Thickness(1);
            copied.CornerRadius = new CornerRadius(12);
            copied.Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.18 };
            copied.Child = new TextBlock { Text = "✓ 已复制", FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = LightTheme.Brush("#20408E"), TextAlignment = TextAlignment.Center };
            copied.Opacity = 0;
            copied.IsHitTestVisible = false;
            Children.Add(copied);
            flash.Tick += delegate { flash.Stop(); copied.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(0.2))); };
            Surface.MouseEnter += delegate { hovering = true; close.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.18))); };
            Surface.MouseLeave += delegate { hovering = false; close.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(0.18))); };
            Surface.MouseLeftButtonDown += Down;
            Surface.MouseLeftButtonUp += Up;
            Surface.MouseMove += Move;
            Surface.LostMouseCapture += delegate { hold.Stop(); pressed = false; };
            hold.Tick += delegate { hold.Stop(); held = true; pressed = false; Surface.ReleaseMouseCapture(); act(ShotAction.Edit, item); };
            menu = new ImageMenu(delegate(ShotAction action) { act(action, Item); }, delegate { return bulkCounts(Item); });
            Surface.MouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                menu.PlacementTarget = Surface;
                WindowsNative.SetForegroundWindow(new WindowInteropHelper(Window.GetWindow(this)).Handle);
                menu.IsOpen = true;
            };
            UpdateImage();
        }

        public void ApplyTheme(LineTheme theme, int colorIndex)
        {
            Appearance = theme; ColorIndex = colorIndex;
            clip.Apply(theme, colorIndex);
        }

        public bool UpdateImage()
        {
            if (displayed == Item.Image) return false;
            displayed = Item.Image;
            menu.SetDetails(displayed.PixelWidth + " × " + displayed.PixelHeight);
            photo.Source = displayed.Thumbnail;
            double scale = Math.Min(142.0 / displayed.Thumbnail.PixelWidth, 98.0 / displayed.Thumbnail.PixelHeight);
            photo.Width = displayed.Thumbnail.PixelWidth * scale;
            photo.Height = displayed.Thumbnail.PixelHeight * scale;
            photo.Clip = new RectangleGeometry(new Rect(0, 0, photo.Width, photo.Height), 7, 7);
            Surface.Width = photo.Width + 14;
            Surface.Height = photo.Height + 14;
            SetLeft(Surface, (Width - Surface.Width) / 2);
            SetTop(Surface, 20);
            SetLeft(copied, (Width - copied.Width) / 2);
            SetTop(copied, Surface.Height + 30);
            return true;
        }

        public void Place(double x, bool immediately)
        {
            if (!placed || immediately) xMotion.Snap(x);
            else xMotion.Target = x;
            placed = true;
        }

        public void Frame(double elapsed, double sag, double lineWidth, double scrollOffset)
        {
            if (IsDisposed) return;
            xMotion.Step(elapsed);
            drop.Step(elapsed);
            swing.Step(elapsed);
            if (breezeDelay >= 0)
            {
                breezeDelay -= elapsed;
                if (breezeDelay < 0 && !Flying && !Busy) Nudge(breezeAngle);
            }
            scaleMotion.Target = pressed ? 0.95 : (hovering && !Releasing ? 1.035 : 1);
            scaleMotion.Step(elapsed);
            clipMotion.Target = gripHover || Releasing ? 1 : 0;
            clipMotion.Step(elapsed);
            clipLift.Y = -0.8 * clipMotion.Position;
            clipScale.ScaleX = clipScale.ScaleY = 1 + 0.06 * clipMotion.Position;
            photoScale.ScaleX = photoScale.ScaleY = scaleMotion.Position;
            double fraction = Math.Max(0, Math.Min(1, (xMotion.Position + 82 - scrollOffset) / lineWidth));
            position.X = xMotion.Position;
            position.Y = 18 + 4 * sag * fraction * (1 - fraction) - 8 + drop.Position;
            Canvas.SetLeft(this, position.X);
            Canvas.SetTop(this, position.Y);
            tilt.Angle = Tilt + swing.Position;
            entryTime += elapsed;
            Opacity = Flying ? 0 : Math.Min(1, entryTime / 0.16);
        }

        public void Breeze(double delay, double degrees) { breezeDelay = delay; breezeAngle = degrees; }
        public void ScrollWith(double velocity) { swing.Target = Flying || Busy ? 0 : Math.Max(-2.5, Math.Min(2.5, -velocity / 200)); }
        public void Nudge(double degrees) { if (!Flying && !IsDisposed) swing.Velocity += degrees * 8; }

        public void OpenClip()
        {
            ((ToolTip)Grip.ToolTip).IsOpen = false;
            ToolTipService.SetIsEnabled(Grip, false);
            Releasing = true; Grip.IsEnabled = false;
            clip.Open();
        }
        public void DetachPhoto()
        {
            Releasing = false; Detached = true;
            Surface.Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.12)) { BeginTime = TimeSpan.FromSeconds(0.10) });
        }
        public void ResetClip()
        {
            BeginAnimation(OpacityProperty, null);
            Surface.Opacity = 1;
            ToolTipService.SetIsEnabled(Grip, true);
            Releasing = false; Grip.IsEnabled = true;
            clip.Reset();
        }

        public void BeginFlight()
        {
            Releasing = false;
            Flying = true;
            drop.Snap(0);
            swing.Snap(0);
            entryTime = 0;
            Opacity = 0;
        }

        public void Land()
        {
            if (IsDisposed) return;
            Flying = false;
            entryTime = 0;
            Nudge(2.2);
        }

        public Rect PhotoRect()
        {
            var parent = (Canvas)Parent;
            double x = position.X + (Width - Surface.Width) / 2, y = position.Y + 20;
            return new Rect(parent.PointToScreen(new Point(x, y)), parent.PointToScreen(new Point(x + Surface.Width, y + Surface.Height)));
        }

        void Down(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (e.ClickCount == 2) { hold.Stop(); held = true; act(ShotAction.Open, Item); return; }
            start = e.GetPosition(this);
            pressed = true;
            held = false;
            Surface.CaptureMouse();
            hold.Start();
        }

        void Up(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            hold.Stop();
            bool copy = pressed && !held && !dragging;
            pressed = false;
            Surface.ReleaseMouseCapture();
            if (copy) act(ShotAction.Copy, Item);
        }

        void Move(object sender, MouseEventArgs e)
        {
            if (!pressed || held || dragging || e.LeftButton != MouseButtonState.Pressed) return;
            Point position = e.GetPosition(this);
            if (Math.Abs(position.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            hold.Stop();
            pressed = false;
            dragging = true;
            Surface.Opacity = 0.45;
            Surface.ReleaseMouseCapture();
            var payload = new DataObject(DataFormats.FileDrop, new[] { Item.Path });
            var preview = new DragPreview(Item.Image.Thumbnail);
            try { preview.Show(); DragDrop.DoDragDrop(Surface, payload, DragDropEffects.Copy | DragDropEffects.Move); }
            finally { preview.Close(); dragging = false; Surface.Opacity = 1; }
        }

        public void FlashCopied()
        {
            copied.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.15)));
            Nudge(3);
            flash.Stop(); flash.Start();
        }
        public void Dispose() { IsDisposed = true; hold.Stop(); flash.Stop(); menu.IsOpen = false; }
    }

    internal sealed class DragPreview : Window
    {
        readonly DispatcherTimer follow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        IntPtr handle;

        public DragPreview(System.Windows.Media.Imaging.BitmapSource image)
        {
            double scale = Math.Min(126.0 / image.PixelWidth, 86.0 / image.PixelHeight);
            Width = image.PixelWidth * scale + 12;
            Height = image.PixelHeight * scale + 12;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowActivated = false;
            ShowInTaskbar = false;
            Topmost = true;
            IsHitTestVisible = false;
            Opacity = 0.8;
            Content = new Border { CornerRadius = new CornerRadius(10), Background = Brushes.White, Padding = new Thickness(6), Child = new Image { Source = image } };
            SourceInitialized += delegate
            {
                handle = new WindowInteropHelper(this).Handle;
                WindowsNative.PrepareOverlay(handle);
                WindowsNative.PassMouse(handle, true);
                Follow();
                follow.Start();
            };
            follow.Tick += delegate { Follow(); };
            Closed += delegate { follow.Stop(); };
        }

        void Follow()
        {
            var cursor = WindowsNative.Cursor();
            double scale = WindowsNative.MonitorScale(WindowsNative.ScreenAtPointer());
            WindowsNative.SetWindowPos(handle, new IntPtr(-1), cursor.X + 18, cursor.Y + 20, (int)(Width * scale), (int)(Height * scale), 0x10);
        }
    }
}
