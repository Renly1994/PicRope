using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace ScreenLine
{
    internal sealed class ImagePreviewWindow : Window
    {
        readonly Image picture = new Image { Stretch = Stretch.Uniform };
        IntPtr handle;
        Rect pending;
        double scale = 1;

        internal bool Shown { get { return IsVisible && picture.Source != null; } }
        internal BitmapSource ImageSource { get { return picture.Source as BitmapSource; } }

        internal Rect PictureBounds
        {
            get
            {
                if (!Shown) return Rect.Empty;
                var point = picture.PointToScreen(new Point(0, 0));
                return new Rect(point.X, point.Y, picture.ActualWidth * scale, picture.ActualHeight * scale);
            }
        }

        public ImagePreviewWindow()
        {
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; ShowActivated = false; Topmost = true;
            Focusable = false; IsHitTestVisible = false;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            var shell = new Border {
                CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(6),
                Margin = new Thickness(12), Child = picture,
                Background = LightTheme.Brush("#FFFFFF"), BorderBrush = LightTheme.Brush("#DCE1E9")
            };
            shell.Effect = new DropShadowEffect { Color = Color.FromRgb(20, 26, 22), BlurRadius = 26, ShadowDepth = 6, Opacity = 0.24 };
            Content = shell;
            System.Windows.Automation.AutomationProperties.SetAutomationId(this, "ImagePreview");
            SourceInitialized += delegate
            {
                handle = new WindowInteropHelper(this).Handle;
                WindowsNative.PrepareOverlay(handle);
                WindowsNative.PassMouse(handle, true);
                if (pending.Width > 0) Apply(pending);
            };
        }

        internal void Display(BitmapSource image, double imageWidth, double imageHeight, Rect screenBounds, double dpi)
        {
            scale = dpi;
            picture.Source = image;
            picture.Width = imageWidth / dpi;
            picture.Height = imageHeight / dpi;
            Width = screenBounds.Width / dpi;
            Height = screenBounds.Height / dpi;
            Left = screenBounds.Left / dpi;
            Top = screenBounds.Top / dpi;
            if (!IsVisible) Show();
            Apply(screenBounds);
        }

        void Apply(Rect screenBounds)
        {
            pending = screenBounds;
            if (handle == IntPtr.Zero) return;
            WindowsNative.SetWindowPos(handle, new IntPtr(-1),
                (int)Math.Round(screenBounds.Left), (int)Math.Round(screenBounds.Top),
                (int)Math.Round(screenBounds.Width), (int)Math.Round(screenBounds.Height), 0x10);
        }
    }
}
