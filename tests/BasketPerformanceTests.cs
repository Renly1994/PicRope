using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ScreenLine.Tests
{
    internal static class BasketPerformanceTests
    {
        static BasketWindow basket;
        static int checks;

        [STAThread]
        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LightTheme.Install(app);
            app.Startup += async delegate
            {
                try { await Run(args.Contains("--installed")); Console.WriteLine("篮子性能：{0} 项验证通过", checks); }
                catch (Exception error) { Console.WriteLine("篮子性能验证失败：" + error.Message); Environment.ExitCode = 1; }
                finally { if (basket != null) basket.Close(); app.Shutdown(Environment.ExitCode); }
            };
            app.Run();
        }

        static async Task Run(bool installed)
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "basket-performance-" + Guid.NewGuid().ToString("N"));
            var store = new StateStore(root); Directory.CreateDirectory(store.Inbox);
            var gallery = new Gallery(store);
            if (installed)
            {
                string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PicRope", "data", "state.json");
                var state = new JavaScriptSerializer().Deserialize<SavedState>(File.ReadAllText(profile));
                foreach (string path in state.Collection.Where(File.Exists)) gallery.Collect(path, await Task.Run(() => ImageContent.Read(path, false)));
            }
            else
            {
                for (int i = 0; i < 12; i++)
                {
                    string path = Path.Combine(store.Inbox, i + ".png");
                    var visual = new DrawingVisual();
                    using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(i == 0 ? Brushes.Coral : Brushes.CornflowerBlue, null, new Rect(0, 0, 2400, 1600));
                    var bitmap = new RenderTargetBitmap(2400, 1600, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(path)) encoder.Save(file);
                    gallery.Collect(path, ImageContent.Read(path, false));
                }
            }
            if (!installed)
            {
                object cache = typeof(ImageContent).GetField("cache", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                using (var held = new ManualResetEvent(false))
                {
                    Task holder = Task.Run(delegate { lock (cache) { held.Set(); Thread.Sleep(1400); } });
                    held.WaitOne();
                    var opening = Stopwatch.StartNew();
                    basket = new BasketWindow(gallery, delegate { }, delegate { }, delegate { });
                    long elapsed = opening.ElapsedMilliseconds;
                    await holder;
                    Check(elapsed < 900, "打开篮子不应同步等待缩略图缓存：" + elapsed + "ms");
                }
            }
            else
            {
                var opening = Stopwatch.StartNew();
                basket = new BasketWindow(gallery, delegate { }, delegate { }, delegate { });
                Console.WriteLine("实际图片集窗口构造：{0}ms", opening.ElapsedMilliseconds);
            }
            basket.Title = "图绳 · 篮子性能验证"; if (installed) basket.Opacity = 0;
            var clock = Stopwatch.StartNew(); long last = 0, largestGap = 0, largestAt = 0;
            var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            heartbeat.Tick += delegate { long now = clock.ElapsedMilliseconds; if (now - last > largestGap) { largestGap = now - last; largestAt = now; } last = now; };
            heartbeat.Start(); basket.Show(); Console.WriteLine("窗口显示调用：{0}ms", clock.ElapsedMilliseconds);
            var newest = gallery.Collected.OrderByDescending(item => item.Written).First();
            await Until(() => basket.Preview.Source is BitmapSource && (Math.Max(newest.Image.PixelWidth, newest.Image.PixelHeight) <= 240
                || Math.Max(((BitmapSource)basket.Preview.Source).PixelWidth, ((BitmapSource)basket.Preview.Source).PixelHeight) > 240));
            await Task.Delay(300); heartbeat.Stop();
            var shown = (BitmapSource)basket.Preview.Source;
            Console.WriteLine("最大界面间隔：{0}ms，发生于打开后 {1}ms", largestGap, largestAt);
            Check(Math.Max(shown.PixelWidth, shown.PixelHeight) <= 1280, "篮子大图预览应按显示需求限制解码尺寸");
            Check(largestGap < 500, "加载图片时界面应保持响应，最大间隔：" + largestGap + "ms");
            Console.WriteLine("图片数：{0}，最大界面间隔：{1}ms", gallery.Collected.Count, largestGap);
            if (!installed)
            {
                basket.ChoiceFor(gallery.Collected[0].Path).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                basket.ChoiceFor(gallery.Collected[1].Path).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => basket.Preview.Source != null && Pixel((BitmapSource)basket.Preview.Source) == Colors.CornflowerBlue);
                Check(basket.SelectedCount == 2, "快速切换大图预览保留多选状态");
                basket.SelectAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(basket.SelectedCount == gallery.Collected.Count, "异步加载时全选可用");
                basket.UpdateLayout();
                var pictures = (System.Collections.Generic.Dictionary<CaptureItem, Image>)typeof(BasketWindow).GetField("pictures", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(basket);
                await Until(() => pictures.Values.Count(image => image.Source != null) >= 3);
                Check(pictures.Values.Count(image => image.Source != null) < pictures.Count, "只加载可见图片");
                var screenshot = new RenderTargetBitmap((int)basket.ActualWidth, (int)basket.ActualHeight, 96, 96, PixelFormats.Pbgra32); screenshot.Render(basket);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(screenshot));
                using (var output = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "basket-performance-preview.png"))) encoder.Save(output);
                var scroll = (ScrollViewer)typeof(BasketWindow).GetField("scroll", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(basket);
                var lastItem = gallery.Collected.OrderByDescending(item => item.Written).Last();
                scroll.ScrollToEnd();
                await Until(() => pictures[lastItem].Source != null);
                Check(pictures[newest].Source == null, "滚动后释放离开视区的图片");
            }
            basket.Close(); await Task.Delay(150);
            Check(basket.Preview.Source == null, "关闭后不再回填预览"); basket = null;
        }

        static Color Pixel(BitmapSource image)
        {
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); var bytes = new byte[4];
            converted.CopyPixels(new Int32Rect(1, 1, 1, 1), bytes, 4, 0); return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
        }
        static async Task Until(Func<bool> condition) { for (int i = 0; i < 150; i++) { if (condition()) return; await Task.Delay(50); } throw new Exception("等待图片加载超时"); }
        static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    }
}
