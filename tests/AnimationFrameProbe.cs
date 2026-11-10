using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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
        static bool snapshots, fullscreen, stress;
        static int checks;
        [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);

        [STAThread]
        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            snapshots = args.Contains("--snapshots");
            fullscreen = args.Contains("--fullscreen"); stress = args.Contains("--stress");
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
                    if (args.Contains("--capture-flow") || args.Contains("--clipboard-flow"))
                    {
                        context.WindowState = WindowState.Maximized;
                        context.Content = new Border { Background = Brushes.Bisque,
                            Child = new TextBlock { Text = "图绳 · 截图动画验证", FontSize = 64, Foreground = Brushes.DarkSlateGray,
                                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                        await CheckCaptureFlow(args.Contains("--clipboard-flow"));
                        Console.WriteLine("真实截图上绳流程：" + checks + " 项通过"); return;
                    }
                    var results = new List<object>();
                    foreach (int count in fullscreen || stress ? new[] { 49 } : snapshots ? new[] { 6 } : new[] { 6, 49 })
                    {
                        var gallery = await Open(count);
                        foreach (var size in fullscreen || stress ? new[] { new Size(shelf.Display.Bounds.Width, shelf.Display.Bounds.Height) } : new[] { new Size(1280, 720), new Size(600, 1000), new Size(shelf.Display.Bounds.Width, shelf.Display.Bounds.Height) })
                        {
                            results.Add(await Measure(gallery, size));
                            if (snapshots) break;
                        }
                        if (count == 49 || snapshots) CheckFlightDetail(gallery);
                        if (count == 49) await CheckCaptureLifecycle(gallery);
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
            double drawMs = 0, updateMs = 0, waitMs = 0;
            int repeats = stress ? 60 : fullscreen ? 12 : 5;
            string path = Picture(gallery.Store.Root, "incoming-" + size.Width + "x" + size.Height, (int)size.Width, (int)size.Height);
            using (var process = Process.GetCurrentProcess())
            {
                TimeSpan cpu = process.TotalProcessorTime;
                uint gdiBefore = GetGuiResources(process.Handle, 0);
                long peakPrivate = process.PrivateMemorySize64, peakWorking = process.WorkingSet64;
                var total = Stopwatch.StartNew();
                for (int repeat = 0; repeat < repeats; repeat++)
                {
                    gallery.Add(path, ImageContent.Read(path, false));
                    var item = gallery.Items.Last(); var card = shelf.CardFor(item.Path);
                    var screen = shelf.Display.Bounds;
                    long startedTicks = Stopwatch.GetTimestamp();
                    var clock = Stopwatch.StartNew();
                    shelf.ShowNewCapture(item, new Point(screen.Right - 120, screen.Bottom - 150));
                    var flight = shelf.MotionWindows.Single();
                    var done = new TaskCompletionSource<bool>();
                    flight.Closed += delegate { done.TrySetResult(true); };
                    double last = -1;
                    int captured = 0;
                    bool visibleChecked = false;
                    bool screenVisible = false;
                    double lastMemory = -100;
                    TimeSpan stamp = TimeSpan.MinValue;
                    EventHandler rendering = delegate(object sender, EventArgs args)
                    {
                        try
                        {
                            calls++;
                            // 按合成时间去重，仅统计落位前的动画帧，回调次数不等于显示器呈现帧数。
                            var time = ((RenderingEventArgs)args).RenderingTime;
                            if (time == stamp || !card.Flying) return;
                            stamp = time;
                            double now = clock.Elapsed.TotalMilliseconds;
                            if (!visibleChecked && repeat == 0 && now > 400 && flight.CaptureRenderer != null && flight.CaptureRenderer.Failure == null)
                            {
                                Point point = flight.PointToScreen(flight.CaptureRenderer.SamplePoint);
                                using (var sample = new System.Drawing.Bitmap(13, 13, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                                using (var painter = System.Drawing.Graphics.FromImage(sample))
                                {
                                    CopyScreen(painter, (int)point.X - 6, (int)point.Y - 6, 13, 13);
                                    bool blue = false;
                                    for (int y = 0; y < 13; y++) for (int x = 0; x < 13; x++)
                                    { var color = sample.GetPixel(x, y); blue |= Math.Abs(color.R - 70) < 15 && Math.Abs(color.G - 130) < 15 && Math.Abs(color.B - 180) < 15; }
                                    screenVisible = blue;
                                    if (!blue)
                                    {
                                        using (var desktop = new System.Drawing.Bitmap(screen.Width, screen.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                                        using (var capture = System.Drawing.Graphics.FromImage(desktop))
                                        {
                                            CopyScreen(capture, screen.Left, screen.Top, screen.Width, screen.Height);
                                            desktop.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fps60-desktop.png"));
                                        }
                                        var snapshot = Snapshot(flight); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(snapshot));
                                        using (var output = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fps60-buffer.png"))) encoder.Save(output);
                                        Console.WriteLine("屏幕采样位置：" + point);
                                    }
                                }
                                visibleChecked = true;
                            }
                            if (now - lastMemory >= 100)
                            {
                                lastMemory = now; process.Refresh();
                                peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
                                peakWorking = Math.Max(peakWorking, process.WorkingSet64);
                            }
                            if (snapshots && repeat == 0 && gallery.Items.Count == 7 && size.Width == 1280 && now >= 180 + captured * 300 && captured < 2)
                            {
                                var bitmap = Snapshot(flight);
                                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                using (var output = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "flight-" + captured++ + ".png"))) encoder.Save(output);
                            }
                            if (flight.CaptureRenderer != null && flight.CaptureRenderer.Failure == null) return;
                            if (last >= 0) intervals.Add(now - last); else startup.Add(now);
                            last = now; frames++;
                        }
                        catch (Exception error) { done.TrySetException(error); }
                    };
                    CompositionTarget.Rendering += rendering;
                    try
                    {
                        Check(await Task.WhenAny(done.Task, Task.Delay(3000)) == done.Task, "飞行动画按时结束");
                        await done.Task;
                        duration.Add(clock.Elapsed.TotalMilliseconds);
                        if (flight.CaptureRenderer != null)
                        {
                            if (repeat == 0) Check(visibleChecked && screenVisible, "截图动画实际出现在屏幕上");
                            Check(flight.CaptureRenderer.Failure == null, "原生截图绘制成功：" + flight.CaptureRenderer.Failure);
                            double[] presented = flight.CaptureRenderer.Intervals;
                            intervals.AddRange(presented); frames += presented.Length + 1;
                            startup.Add((flight.CaptureRenderer.FirstPresentationTicks - startedTicks) * 1000.0 / Stopwatch.Frequency);
                            var timing = flight.CaptureRenderer.Timings; drawMs += timing[0]; updateMs += timing[1]; waitMs += timing[2];
                        }
                        Check(!card.Flying && card.Opacity > 0 && !shelf.MotionWindows.Any(), "落位后原卡片可见且临时窗口已清理");
                        Check(card.Grip.IsEnabled && gallery.Contains(path) && File.Exists(path), "落位后夹子和原图可继续使用");
                    }
                    finally { CompositionTarget.Rendering -= rendering; flight.Close(); }
                    gallery.Remove(item); await Task.Delay(150);
                    // 首次绘制会初始化 WPF 的字体和画刷缓存，后续截图应保持资源数稳定。
                    if (repeat == 0) gdiBefore = GetGuiResources(process.Handle, 0);
                }
                process.Refresh();
                Check(CaptureFlightRenderer.ActiveCount == 0, "飞行结束后释放全部原生缓冲区和绘制线程");
                uint gdiAfter = GetGuiResources(process.Handle, 0);
                Check(gdiAfter <= gdiBefore + 4, "连续截图不累积 GDI 资源：" + gdiBefore + "→" + gdiAfter);
                Check(intervals.Count > 0, "采集到上绳动画的实际更新帧");
                var result = new {
                    images = gallery.Items.Count + 1, width = size.Width, height = size.Height, repeats, frames, calls,
                    fps = Math.Round(intervals.Count * 1000.0 / intervals.Sum(), 2),
                    p95Ms = Percentile(intervals, 0.95), maxMs = Math.Round(intervals.Max(), 2),
                    over25Ms = intervals.Count(x => x > 25), over50Ms = intervals.Count(x => x > 50),
                    startupP95Ms = Percentile(startup, 0.95), durationP95Ms = Percentile(duration, 0.95),
                    cpuPercent = Math.Round((process.TotalProcessorTime - cpu).TotalMilliseconds / total.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100, 2),
                    peakPrivateMB = Math.Round(peakPrivate / 1048576.0, 1), peakWorkingMB = Math.Round(peakWorking / 1048576.0, 1),
                    privateMB = Math.Round(process.PrivateMemorySize64 / 1048576.0, 1), gdiBefore, gdiAfter,
                    drawMs = Math.Round(drawMs / repeats, 2), updateMs = Math.Round(updateMs / repeats, 2), waitMs = Math.Round(waitMs / repeats, 2)
                };
                Console.WriteLine(new JavaScriptSerializer().Serialize(result)); return result;
            }
        }

        static double Percentile(List<double> samples, double fraction)
        {
            samples.Sort(); return Math.Round(samples[(int)Math.Ceiling(samples.Count * fraction) - 1], 2);
        }
        static void CopyScreen(System.Drawing.Graphics graphics, int x, int y, int width, int height)
        {
            IntPtr screen = GetDC(IntPtr.Zero), target = graphics.GetHdc();
            try { if (!BitBlt(target, 0, 0, width, height, screen, x, y, 0x40CC0020)) throw new System.ComponentModel.Win32Exception(); }
            finally { graphics.ReleaseHdc(target); ReleaseDC(IntPtr.Zero, screen); }
        }
        static BitmapSource Snapshot(Window window)
        {
            var flight = window as ShotFlight;
            if (flight != null)
            {
                var frame = flight.CaptureFrame(); if (frame != null) return frame;
            }
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

        static async Task CheckCaptureLifecycle(Gallery gallery)
        {
            var appearance = shelf.Appearance;
            var bounds = shelf.Display.Bounds;
            var origin = new Point(bounds.Right - 100, bounds.Bottom - 100);
            string path = Picture(gallery.Store.Root, "cancel-flight", bounds.Width, bounds.Height);
            gallery.Add(path, ImageContent.Read(path, false)); var item = gallery.Items.Last();
            for (int i = 0; i < 2; i++)
            {
                shelf.Reveal(true, true, 120); shelf.ShowNewCapture(item, origin); await Task.Delay(100);
                if (i == 1) { shelf.ApplyTheme(LineTheme.FromId("graphite")); await Task.Delay(80); }
                Check(shelf.MotionWindows.Any(), "取消前截图处于飞行状态");
                shelf.Reveal(false, false, 0); await NativeReleased();
                Check(!shelf.MotionWindows.Any() && !shelf.CardFor(path).Flying, "取消和途中换肤不遗留动画窗口");
                await Task.Delay(250);
            }
            gallery.Remove(item); shelf.ApplyTheme(appearance); shelf.Reveal(true, true, 120); await Task.Delay(350);
            var added = new List<CaptureItem>();
            for (int i = 0; i < 2; i++)
            {
                path = Picture(gallery.Store.Root, "concurrent-flight-" + i, bounds.Width, bounds.Height);
                gallery.Add(path, ImageContent.Read(path, false)); added.Add(gallery.Items.Last());
                shelf.ShowNewCapture(added.Last(), origin);
            }
            Check(shelf.MotionWindows.Count() == 2, "连续两张全屏截图可同时上绳");
            await Task.Delay(1300); await NativeReleased();
            Check(!shelf.MotionWindows.Any() && added.All(entry => !shelf.CardFor(entry.Path).Flying), "同时上绳后均落位并释放绘制资源");
            foreach (var entry in added) gallery.Remove(entry);
        }

        static async Task CheckCaptureFlow(bool clipboard)
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "capture-flow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var store = new StateStore(Path.Combine(root, "profile"));
            store.State.CollectClipboard = true; store.State.CollectTextClipboard = false;
            var controller = new DesktopController(store, Path.Combine(root, "watched"), false);
            var previous = System.Windows.Forms.Clipboard.GetDataObject();
            var savedClipboard = new System.Windows.Forms.DataObject();
            if (previous != null) foreach (string format in previous.GetFormats(false))
            {
                object value = previous.GetData(format, false);
                var stream = value as MemoryStream; if (stream != null) value = new MemoryStream(stream.ToArray());
                var image = value as System.Drawing.Image; if (image != null) value = new System.Drawing.Bitmap(image);
                if (value != null) savedClipboard.SetData(format, false, value);
            }
            uint sequence = 0;
            try
            {
                await controller.Start();
                for (int i = 0; i < 49; i++)
                {
                    string path = Picture(root, "existing-" + i, 320, 180);
                    controller.Gallery.Add(path, ImageContent.Read(path, false));
                }
                controller.Shelf.Reveal(false, false, 0); await Task.Delay(500);
                var screen = controller.Shelf.Display.Bounds;
                foreach (bool full in new[] { false, true })
                {
                    var images = new List<System.Drawing.Bitmap>();
                    try
                    {
                        int before = controller.Gallery.Items.Count;
                        SetCursorPos(screen.Right - 140, screen.Bottom - 160);
                        if (clipboard)
                        {
                            var data = new System.Windows.Forms.DataObject();
                            string path = Picture(root, "clipboard-" + full, full ? screen.Width : 900, full ? screen.Height : 600);
                            using (var picture = new System.Drawing.Bitmap(path))
                            {
                                data.SetImage(picture); await WindowsClipboard.Write(data); sequence = WindowsNative.GetClipboardSequenceNumber();
                            }
                        }
                        else
                        {
                            controller.ShowCapture();
                            for (int i = 0; i < 100 && controller.Capture == null; i++) await Task.Delay(20);
                            var capture = controller.Capture;
                            Check(capture != null && capture.IsVisible, "打开真实截图窗口");
                            capture.Select(full ? new Rect(0, 0, screen.Width, screen.Height) : new Rect(180, 240, 900, 600));
                            await Task.Delay(120);
                            var button = capture.Tools["完成"];
                            var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                            SetCursorPos((int)point.X, (int)point.Y);
                            mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                        }
                        ShotFlight flight = null;
                        for (int i = 0; i < 120 && flight == null; i++)
                        { flight = controller.Shelf.MotionWindows.FirstOrDefault(); if (flight == null) await Task.Delay(20); }
                        Check(flight != null, (clipboard ? "剪贴板截图" : "主动截图") + (full ? "全屏" : "区域") + "完成后实际启动上绳动画");
                        sequence = WindowsNative.GetClipboardSequenceNumber();
                        for (int i = 0; i < 8; i++)
                        {
                            var frame = new System.Drawing.Bitmap(screen.Width, screen.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                            using (var graphics = System.Drawing.Graphics.FromImage(frame)) CopyScreen(graphics, screen.Left, screen.Top, screen.Width, screen.Height);
                            images.Add(frame); await Task.Delay(90);
                        }
                        string prefix = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, (clipboard ? "clipboard" : "capture") + (full ? "-full-" : "-region-"));
                        for (int i = 0; i < images.Count; i++) images[i].Save(prefix + i + ".png");
                        if (clipboard)
                        {
                            int early = BlueArea(images[1]), middle = BlueArea(images[3]);
                            Check(early > 1000 && middle < early * 0.6, "屏幕中的图片随飞行缩小，不能只更新缓冲区或停在首帧：" + early + "→" + middle);
                        }
                        Check(flight.CaptureRenderer != null && flight.CaptureRenderer.Failure == null, "真实截图使用优化绘制路径");
                        Check(flight.CaptureRenderer.Intervals.Length >= 20, "真实截图具有连续的飞行帧");
                        Check(controller.Gallery.Items.Count == Math.Min(50, before + 1), "一次截图只新增一张挂图，并遵守挂图上限");
                        await Task.Delay(300); await NativeReleased();
                        Check(!controller.Shelf.MotionWindows.Any() && !controller.Shelf.CardFor(controller.Gallery.Items.Last().Path).Flying, "截图完整飞行并落位");
                        controller.Shelf.Reveal(false, false, 0); await Task.Delay(400);
                    }
                    finally { foreach (var image in images) image.Dispose(); }
                }
            }
            finally
            {
                controller.Dispose();
                if (sequence != 0 && WindowsNative.GetClipboardSequenceNumber() == sequence) System.Windows.Forms.Clipboard.SetDataObject(savedClipboard, true);
            }
        }

        static async Task NativeReleased()
        {
            for (int i = 0; i < 100 && CaptureFlightRenderer.ActiveCount != 0; i++) await Task.Delay(20);
            Check(CaptureFlightRenderer.ActiveCount == 0, "原生截图绘制资源已释放");
        }
        static int BlueArea(System.Drawing.Bitmap image)
        {
            int count = 0;
            for (int y = 260; y < image.Height - 80; y += 4) for (int x = 40; x < image.Width - 40; x += 4)
            {
                var color = image.GetPixel(x, y);
                if (Math.Abs(color.R - 70) < 15 && Math.Abs(color.G - 130) < 15 && Math.Abs(color.B - 180) < 15) count++;
            }
            return count;
        }
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    }
}
