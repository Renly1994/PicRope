using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenLine.Tests
{
    internal static class HoverScrollTests
    {
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        static ShelfWindow shelf;
        static int checks;

        [STAThread]
        static void Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            LightTheme.Install(app);
            var cursor = WindowsNative.Cursor(); IntPtr foreground = WindowsNative.GetForegroundWindow();
            app.Startup += async delegate
            {
                try { await Run(); Console.WriteLine("滚动悬停：{0} 项验证通过", checks); }
                catch (Exception error) { Console.WriteLine("滚动悬停验证失败（已通过 " + checks + " 项）：" + error); Environment.ExitCode = 1; }
                finally
                {
                    if (shelf != null) shelf.Close(); SetCursorPos(cursor.X, cursor.Y);
                    if (foreground != IntPtr.Zero) WindowsNative.ActivateEditor(foreground);
                    app.Shutdown(Environment.ExitCode);
                }
            };
            app.Run();
        }

        static async Task Run()
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "hover-scroll-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var store = new StateStore(Path.Combine(root, "profile"));
            var gallery = new Gallery(store);
            for (int i = 0; i < 12; i++)
            {
                string path = Path.Combine(root, i + ".png");
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen()) context.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(50 + i * 12), 100, 160)), null, new Rect(0, 0, 320, 180));
                var bitmap = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(path)) encoder.Save(stream);
                gallery.Add(path, ImageContent.Read(path, false));
            }
            shelf = new ShelfWindow(gallery, delegate { }, delegate { });
            typeof(ShelfWindow).GetField("nextGust", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(shelf, DateTime.MaxValue);
            shelf.Show(); shelf.ChoosingAppearance = true; shelf.Reveal(true, true, 30); await Task.Delay(900);
            var first = shelf.CardFor(gallery.Items[2].Path);
            Point center = first.Surface.PointToScreen(new Point(first.Surface.ActualWidth / 2, first.Surface.ActualHeight / 2));
            SetCursorPos((int)center.X, (int)center.Y);
            await Task.Delay(100); SetCursorPos((int)center.X + 1, (int)center.Y);
            await Until(() => shelf.Preview.Visible && shelf.Preview.CurrentItem == first.Item);

            Wheel(first, 120);
            Check(!shelf.Preview.Visible, "滚到边缘时先收起预览");
            await Until(() => shelf.Preview.Visible && shelf.Preview.CurrentItem == first.Item);
            Check(first.Surface.IsMouseOver, "边缘滚动后鼠标不移动也恢复预览");

            Wheel(first, -120);
            Check(!shelf.Preview.Visible, "滚动时立即收起旧预览");
            await Until(() => shelf.Preview.Visible && shelf.Preview.CurrentItem != first.Item);
            var underPointer = gallery.Items.Select(item => shelf.CardFor(item.Path)).FirstOrDefault(card => card.Surface.IsMouseOver);
            Check(underPointer != null && shelf.Preview.CurrentItem == underPointer.Item, "滚动停止后显示鼠标下的新图片");

            shelf.Preview.SetEnabled(false);
            Wheel(underPointer, 120); await Task.Delay(1600);
            Check(!shelf.Preview.Visible, "关闭悬停预览后滚动不会重新打开");
            shelf.Preview.SetEnabled(true);
            underPointer = gallery.Items.Select(item => shelf.CardFor(item.Path)).FirstOrDefault(card => card.Surface.IsMouseOver);
            Check(underPointer != null, "重新开启后鼠标仍在图片上");
            Wheel(underPointer, -120);
            var area = shelf.Display.WorkingArea;
            SetCursorPos(area.Left + 20, area.Bottom - 20);
            await Task.Delay(1600);
            Check(!shelf.Preview.Visible, "滚动时移开鼠标不会弹出预览");
            var rendering = typeof(ShelfWindow).GetField("rendering", BindingFlags.Instance | BindingFlags.NonPublic);
            for (int i = 0; i < 220 && (bool)rendering.GetValue(shelf); i++) await Task.Delay(50);
            Check(shelf.Revealed && !(bool)rendering.GetValue(shelf), "绳子静止后停止逐帧刷新");
            var wakeCard = shelf.CardFor(gallery.Items[3].Path);
            Point wake = wakeCard.Surface.PointToScreen(new Point(wakeCard.Surface.ActualWidth / 2, wakeCard.Surface.ActualHeight / 2));
            SetCursorPos((int)wake.X, (int)wake.Y); await Task.Delay(100); SetCursorPos((int)wake.X + 1, (int)wake.Y);
            await Until(() => shelf.Preview.Visible && shelf.Preview.CurrentItem == wakeCard.Item);
            var scale = (SpringMotion)typeof(ShotCard).GetField("scaleMotion", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(wakeCard);
            Check(scale.Position > 1.001, "鼠标悬停会恢复图片动效");
            Wheel(wakeCard, -120);
            Check((bool)rendering.GetValue(shelf), "滚动会立即恢复逐帧刷新");
        }

        static void Wheel(ShotCard card, int delta)
        {
            card.Surface.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
        }
        static async Task Until(Func<bool> condition)
        {
            for (int i = 0; i < 80; i++) { if (condition()) return; await Task.Delay(50); }
            throw new Exception("等待滚动预览超时");
        }
        static void Check(bool condition, string description) { if (!condition) throw new Exception(description); checks++; }
    }
}
