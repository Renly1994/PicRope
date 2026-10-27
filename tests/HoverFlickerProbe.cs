using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenLine.Tests
{
    internal static class HoverFlickerProbe
    {
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
        static ShelfWindow shelf;
        static Window backdrop;

        [STAThread]
        public static void Main(string[] arguments)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            LightTheme.Install(app);
            app.Startup += async delegate
            {
                try { await Probe(Array.IndexOf(arguments, "--portrait") >= 0); }
                catch (Exception error) { Console.WriteLine("合成画面验证失败：" + error); Environment.ExitCode = 1; }
                finally { if (shelf != null) shelf.Close(); if (backdrop != null) backdrop.Close(); app.Shutdown(Environment.ExitCode); }
            };
            app.Run();
        }

        static async Task Probe(bool portrait)
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "frames-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var store = new StateStore(Path.Combine(root, "profile"));
            var gallery = new Gallery(store);
            string first = WriteImage(root, "a.png", Colors.CornflowerBlue);
            string second = WriteImage(root, "b.png", Colors.Coral, portrait ? 900 : 1600, portrait ? 1600 : 900);
            gallery.Add(first, ImageContent.Read(first, false)); gallery.Add(second, ImageContent.Read(second, false));
            shelf = new ShelfWindow(gallery, delegate { }, delegate { });
            shelf.Opacity = 0; shelf.Show(); shelf.ChoosingAppearance = true; shelf.Reveal(true, true, 30);
            await Task.Delay(500);
            var a = shelf.CardFor(first); var b = shelf.CardFor(second);
            Enter(a); await Until(() => shelf.Preview.Visible);
            WindowsNative.Rectangle firstBounds;
            WindowsNative.GetWindowRect(new WindowInteropHelper(shelf.Preview.Window).Handle, out firstBounds);
            var work = shelf.Display.WorkingArea;
            double scale = WindowsNative.MonitorScale(shelf.Display);
            Size size = HoverPreview.PreviewSize(b.Item.Image.PixelWidth, b.Item.Image.PixelHeight, b.PhotoRect(), new Rect(work.Left, work.Top, work.Width, work.Height), scale);
            double width = size.Width, height = size.Height;
            Rect secondBounds = HoverPreview.Place(b.PhotoRect(), new Rect(work.Left, work.Top, work.Width, work.Height), width + 56, height + 56, scale);
            int left = Math.Max(firstBounds.Left + 48, (int)secondBounds.Left + 48);
            int right = Math.Min(firstBounds.Right - 48, (int)secondBounds.Right - 48);
            int top = Math.Max(firstBounds.Top + 48, (int)secondBounds.Top + 48);
            int bottom = Math.Min(firstBounds.Bottom - 48, (int)secondBounds.Bottom - 48);
            if (left >= right || top >= bottom) throw new Exception("测试图片的预览区域没有重叠");
            int x = (left + right) / 2, y = (top + bottom) / 2;
            int backLeft = Math.Min(firstBounds.Left, (int)secondBounds.Left) - 10;
            int backTop = Math.Min(firstBounds.Top, (int)secondBounds.Top) - 10;
            int backRight = Math.Max(firstBounds.Right, (int)secondBounds.Right) + 10;
            int backBottom = Math.Max(firstBounds.Bottom, (int)secondBounds.Bottom) + 10;
            backdrop = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = Brushes.Black,
                Width = (backRight - backLeft) / scale, Height = (backBottom - backTop) / scale, Topmost = true,
                ShowInTaskbar = false, ShowActivated = false, IsHitTestVisible = false };
            backdrop.Show();
            var backHandle = new WindowInteropHelper(backdrop).Handle;
            WindowsNative.PrepareOverlay(backHandle); WindowsNative.PassMouse(backHandle, true);
            WindowsNative.SetWindowPos(backHandle, new IntPtr(-1), backLeft, backTop, backRight - backLeft, backBottom - backTop, 0x10);
            WindowsNative.SetWindowPos(new WindowInteropHelper(shelf.Preview.Window).Handle, new IntPtr(-1), 0, 0, 0, 0, 0x13);
            await Task.Delay(120);

            var clock = Stopwatch.StartNew();
            var ready = new TaskCompletionSource<bool>();
            var samples = new List<Tuple<long, int>>();
            long changedAt = -1;
            Task sampler = Task.Run(delegate
            {
                IntPtr dc = GetDC(IntPtr.Zero); int last = -1;
                try
                {
                    ready.TrySetResult(true);
                    while (clock.ElapsedMilliseconds < 850)
                    {
                        int state = Classify(GetPixel(dc, x, y));
                        if (state != last) { samples.Add(Tuple.Create(clock.ElapsedMilliseconds, state)); last = state; }
                        Thread.Sleep(1);
                    }
                }
                finally { ReleaseDC(IntPtr.Zero, dc); }
            });
            await ready.Task; await Task.Delay(80);
            changedAt = clock.ElapsedMilliseconds; Leave(a); Enter(b);
            await Until(() => shelf.Preview.Visible && shelf.Preview.CurrentItem == b.Item);
            await sampler;
            var timeline = samples.ConvertAll(value => value.Item1 + ":" + (value.Item2 == 1 ? "A" : value.Item2 == 2 ? "B" : value.Item2 == 3 ? "白屏" : "背景"));
            Console.WriteLine("切换时间：" + changedAt + "ms，画面变化：" + string.Join(" → ", timeline));
            if (samples.Count < 2 || !samples.Exists(value => value.Item2 == 1) || !samples.Exists(value => value.Item2 == 2))
                throw new Exception("无法可靠采集到两张测试图片的桌面合成画面");
            bool cleared = false;
            foreach (var sample in samples)
            {
                if (sample.Item1 < changedAt) continue;
                if (sample.Item2 == 0) cleared = true;
                if (cleared && sample.Item2 == 1) throw new Exception("切换到第二张图片时旧图再次出现在屏幕上");
                if (sample.Item2 == 3) throw new Exception("切换图片时出现空白预览");
            }
        }

        static int Classify(uint pixel)
        {
            int r = (int)(pixel & 255), g = (int)((pixel >> 8) & 255), b = (int)((pixel >> 16) & 255);
            if (Math.Abs(r - 100) < 18 && Math.Abs(g - 149) < 18 && Math.Abs(b - 237) < 18) return 1;
            if (Math.Abs(r - 255) < 18 && Math.Abs(g - 127) < 18 && Math.Abs(b - 80) < 18) return 2;
            if (r > 242 && g > 242 && b > 242) return 3;
            return 0;
        }

        static void Enter(ShotCard card) { card.Surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent }); }
        static void Leave(ShotCard card) { card.Surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent }); }
        static async Task Until(Func<bool> condition)
        {
            for (int i = 0; i < 60; i++) { if (condition()) return; await Task.Delay(50); }
            throw new Exception("等待测试预览超时");
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
