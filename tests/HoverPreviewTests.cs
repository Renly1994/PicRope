using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;

namespace ScreenLine.Tests
{
    internal static class HoverPreviewTests
    {
        static int checks;
        static ShelfWindow shelf;

        [STAThread]
        public static void Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            LightTheme.Install(app);
            app.Startup += async delegate
            {
                try { await Run(); Console.WriteLine("悬停预览：{0} 项验证通过", checks); }
                catch (Exception error) { Console.WriteLine("悬停预览验证失败：" + error); Environment.ExitCode = 1; }
                finally { if (shelf != null) shelf.Close(); app.Shutdown(Environment.ExitCode); }
            };
            app.Run();
        }

        static async Task Run()
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "hover-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string profile = Path.Combine(root, "profile"); Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "state.json"), "{\"Images\":[],\"Collection\":[],\"Texts\":[]}");
            var store = new StateStore(profile);
            Check(store.State.HoverPreview, "旧设置默认开启悬停预览");
            store.State.HoverPreview = false; store.Save();
            Check(!new StateStore(profile).State.HoverPreview, "关闭预览后设置可以恢复");
            store.State.HoverPreview = true; store.Save();
            var gallery = new Gallery(store);
            string first = WriteImage(root, "第一张.png", Colors.CornflowerBlue);
            string second = WriteImage(root, "第二张.png", Colors.Coral, 900, 1600);
            gallery.Add(first, ImageContent.Read(first, false)); gallery.Add(second, ImageContent.Read(second, false));
            shelf = new ShelfWindow(gallery, delegate { }, delegate { });
            shelf.Opacity = 0; shelf.Show(); shelf.ChoosingAppearance = true; shelf.Reveal(true, true, 30);
            await Task.Delay(500);
            typeof(ShelfWindow).GetMethod("StopRendering", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(shelf, null);
            await Task.Delay(100);
            var a = shelf.CardFor(first); var b = shelf.CardFor(second);

            Enter(a); await Task.Delay(170);
            Check(!shelf.Preview.Visible, "快速经过不会显示预览");
            await Until(() => shelf.Preview.Visible);
            Check(shelf.Preview.CurrentItem == a.Item, "停留后显示当前图片");
            Check(shelf.Preview.Picture != null && shelf.Preview.Picture.PixelWidth > 240, "预览使用更清晰的图片");
            var previewImage = FindImage(shelf.Preview.Window);
            Rect imageBounds = previewImage.TransformToAncestor(shelf.Preview.Window).TransformBounds(new Rect(0, 0, previewImage.ActualWidth, previewImage.ActualHeight));
            Check(previewImage.Effect is DropShadowEffect && imageBounds.Left > 0 && imageBounds.Top > 0
                && shelf.Preview.Window.Background == Brushes.Transparent,
                "预览图片带阴影，周围保持透明且无边框");
            Color firstPixel = Pixel(shelf.Preview.Picture);
            Check(shelf.Preview.Picture.PixelWidth <= Math.Ceiling(640 * WindowsNative.MonitorScale(shelf.Display)), "预览图按显示尺寸解码");
            Check(!shelf.Preview.Window.ShowActivated && !shelf.Preview.Window.IsHitTestVisible, "预览不抢焦点、不拦截鼠标");

            var firstWindow = shelf.Preview.Window;
            Leave(a); Enter(b);
            Check(!shelf.Preview.Visible && !firstWindow.IsVisible, "切换图片时旧预览立即收起");
            await Until(() => shelf.Preview.Visible && shelf.Preview.CurrentItem == b.Item);
            Check(shelf.Preview.CurrentItem == b.Item, "切换图片后显示新图片");
            Check(Pixel(shelf.Preview.Picture) != firstPixel, "切换后图片内容与新目标一致");
            Color secondPixel = Pixel(shelf.Preview.Picture);
            WindowsNative.Rectangle finalBounds;
            Check(!object.ReferenceEquals(firstWindow, shelf.Preview.Window), "新图片不使用上一张图的窗口画面");
            double scale = WindowsNative.MonitorScale(shelf.Display);
            var area = shelf.Display.WorkingArea;
            Rect expected = HoverPreview.Place(b.PhotoRect(), new Rect(area.Left, area.Top, area.Width, area.Height), shelf.Preview.Window.Width, shelf.Preview.Window.Height, scale);
            Check(WindowsNative.GetWindowRect(new WindowInteropHelper(shelf.Preview.Window).Handle, out finalBounds)
                && Math.Abs(finalBounds.Left - expected.Left) <= 1 && Math.Abs(finalBounds.Top - expected.Top) <= 1
                && Math.Abs(finalBounds.Right - expected.Right) <= 1 && Math.Abs(finalBounds.Bottom - expected.Bottom) <= 1,
                "横竖图片切换后预览位置和尺寸正确");
            for (int i = 0; i < 4; i++)
            {
                ShotCard from = i % 2 == 0 ? b : a, to = i % 2 == 0 ? a : b;
                Leave(from); Enter(to);
                await Until(() => shelf.Preview.Visible && shelf.Preview.CurrentItem == to.Item);
                Check(Pixel(shelf.Preview.Picture) == (to == a ? firstPixel : secondPixel), "连续切换只显示当前图片");
            }

            Leave(b); Enter(a);
            await Task.Delay(160); Leave(a); Enter(b);
            await Until(() => shelf.Preview.Visible && shelf.Preview.CurrentItem == b.Item);
            await Task.Delay(100);
            Check(shelf.Preview.CurrentItem == b.Item && Pixel(shelf.Preview.Picture) == secondPixel, "准备新图时快速移开不会显示过时画面");
            Leave(b); await Task.Delay(30); Enter(b); await Task.Delay(80);
            Check(shelf.Preview.Visible && shelf.Preview.CurrentItem == b.Item, "轻微摆动时预览保持稳定");
            Leave(b); await Task.Delay(160);
            Check(!shelf.Preview.Visible, "移开后预览收起");
            Enter(a); await Task.Delay(100); Leave(a); await Task.Delay(550);
            Check(!shelf.Preview.Visible, "离开后未完成的预览不会再次弹出");

            Enter(a); await Until(() => shelf.Preview.Visible);
            a.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Check(!shelf.Preview.Visible, "按下鼠标立即收起预览");
            shelf.Preview.SetEnabled(false); Enter(a); await Task.Delay(600);
            Check(!shelf.Preview.Visible, "关闭开关后不显示预览");
            shelf.Preview.SetEnabled(true);

            Rect positioned = HoverPreview.Place(new Rect(-1700, 100, 160, 120), new Rect(-1920, 0, 1920, 1080), 640, 480, 1.5);
            Check(positioned.Left >= -1920 && positioned.Top >= 0 && positioned.Right <= 0 && positioned.Bottom <= 1080, "负坐标和高缩放屏幕内完整显示");
            Rect screen = new Rect(0, 0, 1536, 960), card = new Rect(1000, 380, 120, 120);
            Size fitted = HoverPreview.PreviewSize(680, 680, card, screen, 1);
            Rect below = HoverPreview.Place(card, screen, fitted.Width + 56, fitted.Height + 56, 1);
            Check(below.Top >= card.Bottom && below.Bottom <= screen.Bottom - 8 && fitted.Height < 480,
                "上下空间都不够时缩小预览并放在图片下方");
            Rect lowerCard = new Rect(1000, 800, 120, 120);
            Size upperSize = HoverPreview.PreviewSize(680, 680, lowerCard, screen, 1);
            Rect above = HoverPreview.Place(lowerCard, screen, upperSize.Width + 56, upperSize.Height + 56, 1);
            Check(above.Bottom <= lowerCard.Top && Math.Abs(upperSize.Height - 480) < 0.1, "底部空间不足时预览在上方完整显示");
            Enter(b); await Until(() => shelf.Preview.Visible);
            gallery.Remove(b.Item);
            Check(!shelf.Preview.Visible, "图片移除时预览关闭");

            string missing = WriteImage(root, "已移动.png", Colors.SlateBlue);
            gallery.Add(missing, ImageContent.Read(missing, false));
            var moved = shelf.CardFor(missing); File.Delete(missing);
            Enter(moved); await Until(() => shelf.Preview.Visible);
            Check(shelf.Preview.Picture == null && shelf.Preview.CurrentItem == moved.Item, "原文件不存在时显示预览提示");
            shelf.Reveal(false, false, 0);
            Check(!shelf.Preview.Visible, "绳子收起时预览同步关闭");
        }

        static void Enter(ShotCard card) { card.Surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent }); }
        static void Leave(ShotCard card) { card.Surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent }); }
        static Image FindImage(DependencyObject parent)
        {
            var image = parent as Image;
            if (image != null && image.Source != null) return image;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                image = FindImage(VisualTreeHelper.GetChild(parent, i));
                if (image != null) return image;
            }
            return null;
        }
        static void Check(bool condition, string description) { if (!condition) throw new Exception(description); checks++; }
        static Color Pixel(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            byte[] bytes = new byte[4]; converted.CopyPixels(new Int32Rect(0, 0, 1, 1), bytes, 4, 0);
            return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
        }
        static async Task Until(Func<bool> condition)
        {
            for (int i = 0; i < 60; i++) { if (condition()) return; await Task.Delay(50); }
            throw new Exception("等待预览超时");
        }

        static string WriteImage(string root, string name, Color color, int width = 1600, int height = 900)
        {
            var drawing = new DrawingVisual();
            using (var painter = drawing.RenderOpen()) painter.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, width, height));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(root, name);
            using (var stream = File.Create(path)) encoder.Save(stream);
            return path;
        }
    }
}
