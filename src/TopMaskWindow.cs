using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace ScreenLine
{
    /// <summary>在屏幕顶部拉一条带子，拖拽选出「不唤起图绳」的横向区间。</summary>
    internal sealed class TopMaskWindow : Window
    {
        readonly Forms.Screen screen;
        readonly Action<double, double> done;
        readonly Canvas layer = new Canvas();
        readonly Rectangle selection = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(96, 96, 165, 250)),
            Stroke = new SolidColorBrush(Color.FromArgb(255, 96, 165, 250)),
            StrokeThickness = 1.5,
            Visibility = Visibility.Collapsed,
        };
        double scale = 1;
        double anchor = double.NaN;
        bool dragging;

        internal TopMaskWindow(Forms.Screen screen, Action<double, double> done)
        {
            this.screen = screen;
            this.done = done;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(88, 15, 23, 42));
            Cursor = Cursors.Cross;
            FontFamily = new FontFamily("Microsoft YaHei UI");

            layer.Children.Add(selection);
            var hint = new TextBlock
            {
                Text = "拖拽框选「不唤起图绳」的顶部区域 · Esc 取消",
                Foreground = Brushes.White,
                FontSize = 14,
                Padding = new Thickness(14, 7, 14, 7),
                Background = new SolidColorBrush(Color.FromArgb(170, 15, 23, 42)),
            };
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.VerticalAlignment = VerticalAlignment.Center;
            var root = new Grid();
            root.Children.Add(layer);
            root.Children.Add(hint);
            Content = root;

            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            };
            Loaded += delegate { Place(); };
        }

        void Place()
        {
            scale = Math.Max(0.5, WindowsNative.MonitorScale(screen));
            var bounds = screen.Bounds;
            Width = bounds.Width / scale;
            Height = 170;
            WindowsNative.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1),
                bounds.Left, bounds.Top, bounds.Width, (int)Math.Round(170 * scale), 0);
            Activate();
            Focus();
        }

        void OnDown(object sender, MouseButtonEventArgs e)
        {
            anchor = e.GetPosition(this).X;
            dragging = true;
            selection.Visibility = Visibility.Visible;
            Canvas.SetLeft(selection, anchor);
            Canvas.SetTop(selection, 0);
            selection.Width = 0;
            selection.Height = ActualHeight > 1 ? ActualHeight : 170;
            CaptureMouse();
            e.Handled = true;
        }

        void OnMove(object sender, MouseEventArgs e)
        {
            if (!dragging) return;
            double x = e.GetPosition(this).X;
            Canvas.SetLeft(selection, Math.Min(anchor, x));
            selection.Width = Math.Abs(x - anchor);
        }

        void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            ReleaseMouseCapture();
            double x = e.GetPosition(this).X;
            double left = Math.Min(anchor, x);
            double width = Math.Abs(x - anchor);
            double pixelLeft = screen.Bounds.Left + left * scale;
            double pixelRight = pixelLeft + width * scale;
            Close();
            // 太窄的区间没有意义（多半是误点），直接丢弃。
            if (width * scale >= 24 && done != null) done(pixelLeft, pixelRight);
        }
    }
}
