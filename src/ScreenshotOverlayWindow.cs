using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace ScreenLine
{
    internal sealed class ScreenshotOverlayWindow : Window
    {
        readonly Forms.Screen screen;
        readonly double scale;
        readonly TaskCompletionSource<ScreenshotSelection> completion = new TaskCompletionSource<ScreenshotSelection>();
        readonly Path dim = new Path();
        readonly Rectangle frame = new Rectangle();
        readonly Border sizeBadge = new Border();
        readonly TextBlock sizeText = new TextBlock();
        Point start;
        Rect selection;
        ScreenshotSelection result;
        bool selecting, closed;

        public ScreenshotOverlayWindow(Forms.Screen screen)
        {
            this.screen = screen;
            scale = WindowsNative.MonitorScale(screen);
            Title = "截图";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = true;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Cursor = Cursors.Cross;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            Width = screen.Bounds.Width / scale;
            Height = screen.Bounds.Height / scale;

            dim.Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
            dim.IsHitTestVisible = false;
            frame.Stroke = new SolidColorBrush(Color.FromRgb(76, 148, 255));
            frame.StrokeThickness = 1.5;
            frame.Fill = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));
            frame.Visibility = Visibility.Collapsed;
            frame.IsHitTestVisible = false;

            sizeText.FontSize = 11;
            sizeText.Foreground = Brushes.White;
            sizeText.TextAlignment = TextAlignment.Center;
            sizeBadge.Width = 94;
            sizeBadge.Height = 24;
            sizeBadge.Child = sizeText;
            sizeBadge.Background = new SolidColorBrush(Color.FromArgb(220, 32, 36, 44));
            sizeBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255));
            sizeBadge.BorderThickness = new Thickness(1);
            sizeBadge.CornerRadius = new CornerRadius(7);
            sizeBadge.Visibility = Visibility.Collapsed;
            sizeBadge.IsHitTestVisible = false;

            var hint = new Border {
                Child = new TextBlock { Text = "拖拽选择截图区域 · Esc 取消", FontSize = 13, Foreground = Brushes.White },
                Background = new SolidColorBrush(Color.FromArgb(220, 32, 36, 44)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 8, 16, 9),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 26, 0, 0),
                IsHitTestVisible = false
            };

            var canvas = new Canvas { Background = Brushes.Transparent };
            canvas.Children.Add(dim);
            canvas.Children.Add(frame);
            canvas.Children.Add(sizeBadge);
            var root = new Grid { Background = Brushes.Transparent };
            root.Children.Add(canvas);
            root.Children.Add(hint);
            Content = root;

            MouseLeftButtonDown += Down;
            MouseMove += Move;
            MouseLeftButtonUp += Up;
            MouseRightButtonUp += delegate { Cancel(); };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                Cancel();
            };
            SourceInitialized += delegate { Position(); };
            SizeChanged += delegate { UpdateDim(); };
            Loaded += delegate
            {
                UpdateDim();
                IntPtr handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero) WindowsNative.SetForegroundWindow(handle);
                Activate();
                Focus();
            };
            Closed += delegate { if (!closed) completion.TrySetResult(null); };
        }

        public Task<ScreenshotSelection> PickAsync()
        {
            Show();
            Activate();
            Focus();
            return completion.Task;
        }

        void Position()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            WindowsNative.SetWindowPos(handle, new IntPtr(-1), screen.Bounds.Left, screen.Bounds.Top,
                screen.Bounds.Width, screen.Bounds.Height, 0x10);
        }

        void Down(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || closed) return;
            start = e.GetPosition(this);
            selecting = true;
            frame.Visibility = Visibility.Visible;
            sizeBadge.Visibility = Visibility.Visible;
            UpdateSelection(start);
            CaptureMouse();
            e.Handled = true;
        }

        void Move(object sender, MouseEventArgs e)
        {
            if (!selecting) return;
            UpdateSelection(e.GetPosition(this));
        }

        void Up(object sender, MouseButtonEventArgs e)
        {
            if (!selecting) return;
            selecting = false;
            ReleaseMouseCapture();
            UpdateSelection(e.GetPosition(this));
            if (selection.Width * scale < 4 || selection.Height * scale < 4)
            {
                Cancel();
                return;
            }
            result = new ScreenshotSelection(ScreenshotCapture.ToPhysicalBounds(screen.Bounds, scale, selection));
            Finish();
        }

        void UpdateSelection(Point pointer)
        {
            double x = Math.Min(start.X, pointer.X), y = Math.Min(start.Y, pointer.Y);
            selection = new Rect(x, y, Math.Abs(pointer.X - start.X), Math.Abs(pointer.Y - start.Y));
            Canvas.SetLeft(frame, selection.Left);
            Canvas.SetTop(frame, selection.Top);
            frame.Width = Math.Max(1, selection.Width);
            frame.Height = Math.Max(1, selection.Height);
            sizeText.Text = (int)Math.Round(selection.Width * scale) + " × " + (int)Math.Round(selection.Height * scale);
            Canvas.SetLeft(sizeBadge, Math.Max(8, Math.Min((ActualWidth > 0 ? ActualWidth : Width) - sizeBadge.Width - 8, selection.Right - sizeBadge.Width)));
            double top = selection.Bottom + 8;
            if (top + sizeBadge.Height > (ActualHeight > 0 ? ActualHeight : Height) - 8)
                top = Math.Max(8, selection.Top - sizeBadge.Height - 8);
            Canvas.SetTop(sizeBadge, top);
            UpdateDim();
        }

        void UpdateDim()
        {
            double width = ActualWidth > 0 ? ActualWidth : Width;
            double height = ActualHeight > 0 ? ActualHeight : Height;
            if (width <= 0 || height <= 0) return;
            var outer = new RectangleGeometry(new Rect(0, 0, width, height));
            if (selection.Width > 0 && selection.Height > 0)
            {
                var cutout = new GeometryGroup { FillRule = FillRule.EvenOdd };
                cutout.Children.Add(outer);
                cutout.Children.Add(new RectangleGeometry(selection));
                dim.Data = cutout;
            }
            else dim.Data = outer;
        }

        void Cancel()
        {
            if (closed) return;
            result = null;
            Finish();
        }

        void Finish()
        {
            if (closed) return;
            closed = true;
            completion.TrySetResult(result);
            Close();
        }
    }
}
