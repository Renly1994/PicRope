using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenLine.Tests
{
    internal static class AnimationFrameProbe
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        static ShelfWindow shelf;
        static bool snapshots;
        static int checks;

        [STAThread]
        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            snapshots = args.Contains("--snapshots");
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            LightTheme.Install(app);
            IntPtr foreground = WindowsNative.GetForegroundWindow();
            Window context = null;
            app.Startup += async delegate
            {
                try
                {
                    context = new Window { Width = 640, Height = 360, Title = "图绳", ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                    context.Show(); WindowsNative.ActivateEditor(new WindowInteropHelper(context).Handle);
                    var results = new List<object>();
                    foreach (int count in snapshots ? new[] { 6 } : new[] { 6, 49 })
                    {
                        var gallery = await Open(count);
                        foreach (var size in new[] { new Size(1280, 720), new Size(600, 1000), new Size(shelf.Display.Bounds.Width, shelf.Display.Bounds.Height) })
                        {
                            results.Add(await Measure(gallery, size));
                            if (snapshots) break;
                        }
                        if (count == 49 || snapshots) CheckFlightDetail(gallery);
                        shelf.Reveal(false, false, 0); await Task.Delay(350);
                        Check(!shelf.Animating && !shelf.MotionWindows.Any(), "收起后停止动画并清理飞行窗口");
                        shelf.Close(); shelf = null;
                    }
                    Console.WriteLine(new JavaScriptSerializer().Serialize(new {
                        renderTier = RenderCapability.Tier >> 16,
                        software = RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly,
                        screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds.ToString(), checks, results
                    }));
                }
                catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
                finally
                {
                    if (shelf != null) shelf.Close(); if (context != null) context.Close();
                    if (foreground != IntPtr.Zero) WindowsNative.SetForegroundWindow(foreground);
                    app.Shutdown(Environment.ExitCode);
                }
            };
            app.Run();
        }

        static async Task<Gallery> Open(int count)
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "animation-fps");
            Directory.CreateDirectory(root);
            var store = new StateStore(Path.Combine(root, "profile"));
            store.State.Images.Clear(); store.State.Collection.Clear();
            store.State.CollectClipboard = store.State.CollectTextClipboard = true;
            store.State.HoverPreview = false;
            var gallery = new Gallery(store);
            for (int i = 0; i < count; i++)
            {
                string path = Picture(root, i.ToString(), 320, 180);
                gallery.Add(path, ImageContent.Read(path, false));
            }
            shelf = new ShelfWindow(gallery, delegate { }, delegate { });
            shelf.ChoosingAppearance = true;
            typeof(ShelfWindow).GetField("nextGust", Flags).SetValue(shelf, DateTime.MaxValue);
            shelf.Show(); shelf.Reveal(true, true, 120);
            await Task.Delay(1500);
            Check(shelf.Revealed, "基准窗口实际展开");
            return gallery;
        }

        static string Picture(string root, string name, int width, int height)
        {
            string path = Path.Combine(root, name + ".png");
            if (File.Exists(path)) return path;
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.SteelBlue, null, new Rect(0, 0, width, height));
                for (int n = 0; n < 8; n++) drawing.DrawRectangle(Brushes.White, null, new Rect(width * 0.05, height * (0.08 + n * 0.1), width * (0.3 + n * 0.06), height * 0.03));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            return path;
        }

        static async Task<object> Measure(Gallery gallery, Size size)
        {
            var intervals = new List<double>();
            var startup = new List<double>();
            var duration = new List<double>();
            int frames = 0, calls = 0;
            string path = Picture(gallery.Store.Root, "incoming-" + size.Width + "x" + size.Height, (int)size.Width, (int)size.Height);
            using (var process = Process.GetCurrentProcess())
            {
                TimeSpan cpu = process.TotalProcessorTime;
                var total = Stopwatch.StartNew();
                for (int repeat = 0; repeat < 5; repeat++)
                {
                    gallery.Add(path, ImageContent.Read(path, false));
                    var item = gallery.Items.Last(); var card = shelf.CardFor(item.Path);
                    var screen = shelf.Display.Bounds;
                    var clock = Stopwatch.StartNew();
                    shelf.ShowNewCapture(item, new Point(screen.Right - 120, screen.Bottom - 150));
                    var flight = shelf.MotionWindows.Single();
                    var done = new TaskCompletionSource<bool>();
                    flight.Closed += delegate { done.TrySetResult(true); };
                    double last = -1;
                    int captured = 0;
                    TimeSpan stamp = TimeSpan.MinValue;
                    EventHandler rendering = delegate(object sender, EventArgs args)
                    {
                        calls++;
                        // 按合成时间去重，仅统计落位前的动画帧，回调次数不等于显示器呈现帧数。
                        var time = ((RenderingEventArgs)args).RenderingTime;
                        if (time == stamp || !card.Flying) return;
                        stamp = time;
                        double now = clock.Elapsed.TotalMilliseconds;
                        if (snapshots && repeat == 0 && gallery.Items.Count == 7 && size.Width == 1280 && now >= 180 + captured * 300 && captured < 2)
                        {
                            var bitmap = Snapshot(flight);
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using (var output = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "flight-" + captured++ + ".png"))) encoder.Save(output);
                        }
                        if (last >= 0) intervals.Add(now - last); else startup.Add(now);
                        last = now; frames++;
                    };
                    CompositionTarget.Rendering += rendering;
                    try
                    {
                        Check(await Task.WhenAny(done.Task, Task.Delay(3000)) == done.Task, "飞行动画按时结束");
                        duration.Add(clock.Elapsed.TotalMilliseconds);
                        Check(!card.Flying && card.Opacity > 0 && !shelf.MotionWindows.Any(), "落位后原卡片可见且临时窗口已清理");
                        Check(card.Grip.IsEnabled && gallery.Contains(path) && File.Exists(path), "落位后夹子和原图可继续使用");
                    }
                    finally { CompositionTarget.Rendering -= rendering; flight.Close(); }
                    gallery.Remove(item); await Task.Delay(150);
                }
                process.Refresh();
                Check(intervals.Count > 0, "采集到上绳动画的实际更新帧");
                var result = new {
                    images = gallery.Items.Count + 1, width = size.Width, height = size.Height, repeats = 5, frames, calls,
                    fps = Math.Round(intervals.Count * 1000.0 / intervals.Sum(), 2),
                    p95Ms = Percentile(intervals, 0.95), maxMs = Math.Round(intervals.Max(), 2),
                    over25Ms = intervals.Count(x => x > 25), over50Ms = intervals.Count(x => x > 50),
                    startupP95Ms = Percentile(startup, 0.95), durationP95Ms = Percentile(duration, 0.95),
                    cpuPercent = Math.Round((process.TotalProcessorTime - cpu).TotalMilliseconds / total.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100, 2),
                    privateMB = Math.Round(process.PrivateMemorySize64 / 1048576.0, 1)
                };
                Console.WriteLine(new JavaScriptSerializer().Serialize(result)); return result;
            }
        }

        static double Percentile(List<double> samples, double fraction)
        {
            samples.Sort(); return Math.Round(samples[(int)Math.Ceiling(samples.Count * fraction) - 1], 2);
        }
        static RenderTargetBitmap Snapshot(Window window)
        {
            double scale = WindowsNative.MonitorScale(shelf.Display);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(window); return bitmap;
        }

        static void CheckFlightDetail(Gallery gallery)
        {
            const int width = 900, height = 600;
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                byte value = (byte)((i % width / 2 % 2) * 255);
                pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = value; pixels[i * 4 + 3] = 255;
            }
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); image.Freeze();
            string path = Path.Combine(gallery.Store.Root, "detail.png"); File.WriteAllBytes(path, ImageContent.EncodeClipboard(image));
            gallery.Add(path, ImageContent.Read(path, false));
            var item = gallery.Items.Last(); var card = shelf.CardFor(path); card.BeginFlight();
            var screen = shelf.Display.Bounds;
            var flight = new ShotFlight(shelf.Display, card, new Rect(screen.Left + 200, screen.Top + 400, width, height), false, card.Land, image);
            try
            {
                flight.Show(); ((Stopwatch)typeof(ShotFlight).GetField("clock", Flags).GetValue(flight)).Stop(); flight.UpdateLayout();
                var frame = ((Canvas)flight.Content).Children[0];
                var photo = (Image)typeof(ShotFlight).GetField("photo", Flags).GetValue(flight);
                Point center = photo.TransformToAncestor(flight).Transform(new Point(photo.ActualWidth / 2, photo.ActualHeight / 2));
                double scale = WindowsNative.MonitorScale(shelf.Display);
                var area = new Int32Rect((int)(center.X * scale) - 64, (int)(center.Y * scale), 128, 1);
                var cached = new byte[512]; Snapshot(flight).CopyPixels(area, cached, 512, 0);
                frame.CacheMode = null;
                var reference = new byte[512]; Snapshot(flight).CopyPixels(area, reference, 512, 0);
                Check(reference.Where((value, index) => index % 4 == 0).Max() - reference.Where((value, index) => index % 4 == 0).Min() > 160, "原图细线清晰度样本有效");
                double error = Enumerable.Range(0, 128).Average(i => Math.Abs(cached[i * 4] - reference[i * 4]));
                Check(error < 60, "大图飞行缓存保留原始细线，像素平均误差：" + error);
            }
            finally { flight.Close(); gallery.Remove(item); }
        }
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    }
}
