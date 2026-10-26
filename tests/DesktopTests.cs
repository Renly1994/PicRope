using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace ScreenLine.Tests
{
    internal static class DesktopTests
    {
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(WindowsNative.Point point);
        [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string type, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr window, string name);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
        static int checks;
        static DesktopController controller;
        static Forms.IDataObject previousClipboard;
        static uint lastTestSequence;
        static WindowsNative.Point previousPointer;
        static IntPtr previousFocus;
        static Window testContext;

        [STAThread]
        public static void Main(string[] arguments)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (arguments.Contains("--times"))
            {
                try { CheckCaptureTimes(); Console.WriteLine("截图时间测试：{0} 项通过", checks); }
                catch (Exception error) { Console.WriteLine("截图时间测试失败：" + error.Message); Environment.ExitCode = 1; }
                return;
            }
            bool passiveInput = arguments.Contains("--scroll-bounds") || arguments.Contains("--basket-appearance") || arguments.Contains("--basket-motion");
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            LightTheme.Install(application);
            application.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
            {
                Console.WriteLine("桌面测试异常：" + args.Exception);
                args.Handled = true;
                application.Shutdown(1);
            };
            application.Startup += async delegate
            {
                try { await Run(arguments.Contains("--visibility"), arguments.Contains("--menus"), arguments.Contains("--editor"), arguments.Contains("--discard"), arguments.Contains("--shelf-motion"), arguments.Contains("--pins"), arguments.Contains("--themes"), arguments.Contains("--copied"), arguments.Contains("--wind"), arguments.Contains("--basket"), arguments.Contains("--scroll-bounds"), arguments.Contains("--basket-appearance"), arguments.Contains("--basket-motion")); Console.WriteLine("桌面测试：{0} 项通过", checks); }
                catch (Exception error) { Console.WriteLine("桌面测试失败：" + error); Environment.ExitCode = 1; }
                if (!passiveInput) mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                try
                {
                    if (lastTestSequence != 0 && WindowsNative.GetClipboardSequenceNumber() == lastTestSequence)
                    {
                        if (previousClipboard == null) Forms.Clipboard.Clear();
                        else Forms.Clipboard.SetDataObject(previousClipboard, true, 8, 60);
                    }
                }
                catch (Exception error) { Console.WriteLine("恢复剪贴板失败：" + error); Environment.ExitCode = 1; }
                if (controller != null) { controller.Dispose(); controller = null; }
                if (testContext != null) testContext.Close();
                if (!passiveInput) { SetCursorPos(previousPointer.X, previousPointer.Y); WindowsNative.SetForegroundWindow(previousFocus); }
                application.Shutdown(Environment.ExitCode);
            };
            application.Run();
        }

        static async Task Run(bool visibilityOnly, bool menusOnly, bool editorOnly, bool discardOnly, bool shelfMotionOnly, bool pinsOnly, bool themesOnly, bool copiedOnly, bool windOnly, bool basketOnly, bool scrollOnly, bool basketAppearanceOnly, bool basketMotionOnly)
        {
            CheckCaptureTimes();
            previousPointer = WindowsNative.Cursor();
            previousFocus = WindowsNative.GetForegroundWindow();
            var original = Forms.Clipboard.GetDataObject();
            if (original != null)
            {
                var snapshot = new Forms.DataObject();
                foreach (string format in original.GetFormats(false))
                {
                    object value = original.GetData(format, false);
                    var stream = value as MemoryStream;
                    if (stream != null) value = new MemoryStream(stream.ToArray());
                    var image = value as System.Drawing.Image;
                    if (image != null) value = new System.Drawing.Bitmap(image);
                    if (value != null) snapshot.SetData(format, false, value);
                }
                previousClipboard = snapshot;
            }
            // 为桌面交互和剪贴板恢复保留独立窗口，避免依赖前台应用的状态。
            testContext = new Window { Title = "桌面交互验证", Width = 320, Height = 120, Left = 800, Top = 400, ShowInTaskbar = false, ShowActivated = !scrollOnly && !basketAppearanceOnly && !basketMotionOnly };
            testContext.Show();
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "desktop-" + Guid.NewGuid().ToString("N"));
            string watched = Path.Combine(root, "WindowsScreenshots");
            Directory.CreateDirectory(watched);
            var store = new StateStore(Path.Combine(root, "profile"));
            controller = new DesktopController(store, watched, false);
            await controller.Start();
            if (basketMotionOnly) { await CheckBasketMotion(store); return; }
            if (basketAppearanceOnly) { await CheckBasketAppearance(store); return; }
            if (scrollOnly) { await CheckScrollBoundaries(store); return; }
            if (basketOnly) { await CheckBasket(store, watched); return; }
            if (windOnly) { await CheckScrollBoundaries(store); await CheckIdleWind(store); await CheckScrollAndWind(store); return; }
            Check(!controller.Captures.CollectClipboard, "首次启动的剪贴板收集默认关闭");
            if (copiedOnly)
            {
                string path = Path.Combine(root, "复制提示.png");
                byte[] picture = Asset("复制提示", "截图收集 · 随手取用", Color.FromRgb(25, 40, 63));
                File.WriteAllBytes(path, picture);
                controller.Gallery.Add(path, ImageContent.Decode(picture));
                controller.Shelf.Reveal(true, true, 0);
                await Task.Delay(500);
                var card = controller.Shelf.CardFor(path);
                card.FlashCopied();
                await Task.Delay(500);
                await CheckCopyFeedback(card);
                return;
            }
            if (visibilityOnly) { await CheckVisibility(); return; }
            if (menusOnly) { await CheckMenus(store); return; }
            if (editorOnly) { await CheckEditor(store); return; }
            if (discardOnly) { await CheckDiscard(store); return; }
            if (shelfMotionOnly) { await CheckScrollBoundaries(store); await CheckScrollAndWind(store); return; }
            if (pinsOnly) { await CheckPins(store); return; }
            if (themesOnly) { await CheckThemes(store, watched); return; }
            controller.Captures.CollectClipboard = true;
            byte[] first = Asset("工作流程", "选题 → 录制 → 发布", Color.FromRgb(25, 40, 63));
            byte[] second = Asset("临时笔记", "截图收集 · 随手取用", Color.FromRgb(220, 112, 76));
            byte[] third = Asset("界面参考", "卡片 · 拖拽 · 复制", Color.FromRgb(61, 93, 181));
            await CheckThirdPartyFormats(second, third, store);
            controller.Gallery.Clear();
            string folderImage = Path.Combine(watched, "工作流程.png");
            IntPtr captureFocus = WindowsNative.GetForegroundWindow();
            File.WriteAllBytes(folderImage, first);
            await Until(() => controller.Gallery.Items.Count == 1);
            Check(controller.Gallery.Items[0].Path == folderImage, "文件夹里的新截图自动挂上");
            Check(controller.Shelf.CardFor(folderImage).Flying && controller.Shelf.MotionWindows.Any(), "新截图先飞入，挂图等待抵达后接管");
            Check(WindowsNative.GetForegroundWindow() == captureFocus, "截图飞入不会抢走前台应用焦点");
            await Task.Delay(850);
            Check(!controller.Shelf.CardFor(folderImage).Flying && !controller.Shelf.MotionWindows.Any(), "飞入完成后恢复真实卡片并关闭临时窗口");
            var firstCard = controller.Shelf.CardFor(folderImage);
            var lineCanvas = (System.Windows.Controls.Canvas)firstCard.Parent;
            Point pin = firstCard.TransformToAncestor(lineCanvas).Transform(new Point(82, 8));
            var rope = (System.Windows.Shapes.Path)typeof(ShelfWindow).GetField("rope", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(controller.Shelf);
            double controlY = ((System.Windows.Media.QuadraticBezierSegment)((System.Windows.Media.PathGeometry)rope.Data).Figures[0].Segments[0]).Point1.Y;
            double fraction = pin.X / ((QuadraticBezierSegment)((PathGeometry)rope.Data).Figures[0].Segments[0]).Point2.X;
            Check(Math.Abs(pin.Y - (18 + 2 * (controlY - 18) * fraction * (1 - fraction))) < 0.05, "图片摆动围绕夹子的绳子支点，夹子不会脱离绳子");
            var firstSurface = controller.Shelf.CardFor(folderImage).Surface;
            double firstX = firstSurface.PointToScreen(new Point(firstSurface.ActualWidth / 2, 0)).X;

            await SetClipboardImage(second);
            await Until(() => controller.Gallery.Items.Count == 2);
            Check(FileRules.IsInside(controller.Gallery.Items[1].Path, store.Inbox), "剪贴板截图保存到独立收集目录");
            double movingX = firstSurface.PointToScreen(new Point(firstSurface.ActualWidth / 2, 0)).X;
            Check(Math.Abs(movingX - firstX) < 60, "新截图加入时旧图片平滑让位，没有瞬间跳到新位置");
            File.WriteAllBytes(Path.Combine(watched, "重复通知.png"), second);
            await Task.Delay(900);
            Check(controller.Gallery.Items.Count == 2, "剪贴板与文件夹的重复通知只收一次");

            await SetClipboardImage(third);
            await RecordMotion(controller.Shelf);
            await Until(() => controller.Gallery.Items.Count == 3);
            Check(controller.Gallery.Items.Count == 3, "连续同尺寸不同内容的截图均可收集");
            for (int i = 0; i < controller.Gallery.Items.Count; i++)
            {
                var card = controller.Shelf.CardFor(controller.Gallery.Items[i].Path);
                if (i == 0) continue;
                var previous = controller.Shelf.CardFor(controller.Gallery.Items[i - 1].Path);
                Point currentPin = card.TransformToAncestor(lineCanvas).Transform(new Point(82, 8));
                Point previousPin = previous.TransformToAncestor(lineCanvas).Transform(new Point(82, 8));
                Check(Math.Abs(currentPin.X - previousPin.X - 172) < 1, "连续挂图后每张图片保留完整间距");
            }
            controller.Shelf.Reveal(true, true, 0);
            await Task.Delay(400);
            ExportPreview(controller.Shelf);
            var target = controller.Shelf.CardFor(folderImage).Surface;
            Point center = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
            SetCursorPos((int)center.X, (int)center.Y);
            await Task.Delay(150);
            await Until(() => WindowFromPoint(WindowsNative.Cursor()) == controller.Shelf.Handle);
            Check(WindowFromPoint(WindowsNative.Cursor()) == controller.Shelf.Handle, "鼠标在卡片上时窗口可接收点击");
            await Until(() => controller.Shelf.PreviewVisible);
            var targetBounds = new Rect(target.PointToScreen(new Point()), target.PointToScreen(new Point(target.ActualWidth, target.ActualHeight)));
            var largePreview = controller.Shelf.LargePreview;
            var largeBounds = largePreview.PictureBounds;
            Check(largeBounds.Width > targetBounds.Width + 100 && largeBounds.Top >= targetBounds.Bottom,
                "悬停图片时在卡片下方显示明显更大的预览");
            Check(largePreview.ImageSource != null && largePreview.ImageSource.PixelWidth > 240, "大图预览从原图解码，而不是 240px 缩略图");
            var screenArea = controller.Shelf.Display.WorkingArea;
            var captureArea = Rect.Intersect(Rect.Union(targetBounds, largeBounds), new Rect(screenArea.Left, screenArea.Top, screenArea.Width, screenArea.Height));
            CaptureScreen(captureArea, Path.Combine(Path.GetDirectoryName(PinPreviewPath("")), "大图预览.png"));
            SetCursorPos((int)Math.Round(largeBounds.Left + largeBounds.Width / 2), (int)Math.Round(largeBounds.Top + largeBounds.Height / 2));
            await Until(() => !controller.Shelf.PreviewVisible);
            Check(!controller.Shelf.PreviewVisible, "鼠标移开图片后大图预览立即隐藏");
            var secondCard = controller.Shelf.CardFor(controller.Gallery.Items[1].Path).Surface;
            Point secondCenter = secondCard.PointToScreen(new Point(secondCard.ActualWidth / 2, secondCard.ActualHeight / 2));
            SetCursorPos((int)secondCenter.X, (int)secondCenter.Y);
            await Task.Delay(120);
            SetCursorPos((int)secondCenter.X + 1, (int)secondCenter.Y);
            await Until(() => controller.Shelf.PreviewVisible);
            var secondPreview = controller.Shelf.LargePreview;
            Check(secondPreview != null && secondPreview != largePreview && secondPreview.ImageSource != null,
                "从一张图切到另一张时重新创建预览窗，不闪现上一张画面");
            SetCursorPos((int)center.X, (int)center.Y);
            await Until(() => WindowFromPoint(WindowsNative.Cursor()) == controller.Shelf.Handle);
            IntPtr focus = WindowsNative.GetForegroundWindow();
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            await Until(() => controller.Shelf.CardFor(folderImage).Busy);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            await Until(delegate
            {
                try { return Clipboard.ContainsFileDropList(); }
                catch (ExternalException) { return false; }
            });
            var copied = Clipboard.GetDataObject();
            lastTestSequence = WindowsNative.GetClipboardSequenceNumber();
            Check(copied.GetDataPresent(DataFormats.FileDrop) && copied.GetDataPresent("PNG"), "实际单击卡片可复制图片和文件");
            Check(WindowsNative.GetForegroundWindow() == focus, "单击截图不会抢走当前应用焦点");
            await Task.Delay(500);
            Check(controller.Gallery.Items.Count == 3, "复制自己的截图不会再次收集");
            await CheckCopyFeedback(firstCard);

            WindowsNative.Rectangle bounds;
            WindowsNative.GetWindowRect(controller.Shelf.Handle, out bounds);
            SetCursorPos(bounds.Left + 10, bounds.Top + 110);
            await Task.Delay(150);
            Check(WindowFromPoint(WindowsNative.Cursor()) != controller.Shelf.Handle, "透明区域允许鼠标点击穿透");
            await CheckVisibility();

            string copiedFile = await DragTo(folderImage, Path.Combine(root, "drop-copy"), false);
            Check(File.Exists(copiedFile) && File.Exists(folderImage) && controller.Gallery.Items.Count == 3, "实际拖拽复制可传递文件并保留原图");
            string moved = await DragTo(folderImage, Path.Combine(root, "drop-move"), true);
            await Until(() => controller.Gallery.Items.Count == 2);
            Check(File.Exists(moved), "移动文件后从绳子上移除，文件保留");
            var owned = controller.Gallery.Items[1];
            controller.Act(ShotAction.Remove, owned);
            Check(File.Exists(owned.Path) && controller.Gallery.Items.Count == 1 && controller.Gallery.Collected.Contains(owned), "取下收集的截图后仍保留在图片篓");
            await controller.Captures.Import(moved, null, false);
            var external = controller.Gallery.Items[1];
            controller.Act(ShotAction.Remove, external);
            Check(File.Exists(moved) && controller.Gallery.Items.Count == 1, "取下外部图片会保留原文件");

            string retained = controller.Gallery.Items[0].Path;
            controller.Dispose();
            controller = new DesktopController(new StateStore(store.Root), watched, false);
            await controller.Start();
            Check(controller.Gallery.Items.Count == 1 && controller.Gallery.Items[0].Path == retained, "重新启动可恢复挂着的图片");
            Check(controller.Captures.CollectClipboard, "重新启动可恢复剪贴板收集设置");
            await Task.Delay(900);
            var restoredCard = controller.Shelf.CardFor(retained);
            var restoredCanvas = (System.Windows.Controls.Canvas)restoredCard.Parent;
            Point restoredPin = restoredCard.TransformToAncestor(restoredCanvas).Transform(new Point(82, 8));
            var restoredRope = (System.Windows.Shapes.Path)typeof(ShelfWindow).GetField("rope", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(controller.Shelf);
            double restoredControlY = ((QuadraticBezierSegment)((PathGeometry)restoredRope.Data).Figures[0].Segments[0]).Point1.Y;
            double restoredFraction = restoredPin.X / ((QuadraticBezierSegment)((PathGeometry)restoredRope.Data).Figures[0].Segments[0]).Point2.X;
            Check(Math.Abs(restoredPin.Y - (18 + 2 * (restoredControlY - 18) * restoredFraction * (1 - restoredFraction))) < 0.1, "恢复的图片完成下落后仍准确夹在绳子上");
            Check(!File.Exists(Path.Combine(store.Root, "error.log")), "完整操作过程没有运行错误日志");
            await CheckDiscard(store);
            await CheckScrollBoundaries(store);
            await CheckScrollAndWind(store);
            await CheckPins(store);
        }

        static void CheckCaptureTimes()
        {
            DateTime now = DateTime.Now;
            Check(BasketWindow.TimeLabel(now.ToUniversalTime()) == "今天 " + now.ToString("HH:mm"), "UTC 文件时间在缩略图和预览中显示为本地时间");
            Check(BasketWindow.TimeLabel(now) == "今天 " + now.ToString("HH:mm"), "本地时间不重复转换时区");
            DateTime utc = new DateTime(2026, 10, 19, 23, 45, 0, DateTimeKind.Utc);
            DateTime local = utc.ToLocalTime();
            string date = local.Date == DateTime.Today ? "今天 " : local.ToString("MM-dd ");
            Check(BasketWindow.TimeLabel(utc) == date + local.ToString("HH:mm"), "跨日期的截图按本地日期和时间显示");
        }

        static async Task CheckBasketAppearance(StateStore store)
        {
            var shelf = controller.Shelf;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var count = (TextBlock)typeof(ShelfWindow).GetField("basketCount", flags).GetValue(shelf);
            shelf.ChoosingBasket = true;
            shelf.Width = 840; shelf.Reveal(true, true, 60);
            await Task.Delay(650);
            Check(count.Text == "0", "空收集箱在篮下只显示数字 0");
            Directory.CreateDirectory(store.Inbox);
            byte[] bytes = Asset("界面参考", "截图 · 留待取用", Color.FromRgb(35, 70, 100));
            string path = Path.Combine(store.Inbox, "待挂数量.png"); File.WriteAllBytes(path, bytes);
            controller.Gallery.Collect(path, ImageContent.Decode(bytes));
            Check(count.Text == "1", "只收集未挂图时更新篮下数量");
            controller.Gallery.Add(path, ImageContent.Decode(bytes));
            Check(count.Text == "0", "挂上后从待挂数量扣除");
            controller.Gallery.Remove(controller.Gallery.Items[0]);
            Check(count.Text == "1", "取下后重新计入待挂数量");
            var plate = (Border)count.Parent;
            Color background = ((SolidColorBrush)plate.Background).Color;
            Check(background.A >= 210 && background.R < 50 && ((SolidColorBrush)count.Foreground).Color == Colors.White,
                "数量使用足够深的半透明底和白字，白色及深色壁纸均可读");
            var drawing = new DrawingVisual();
            using (var painter = drawing.RenderOpen())
            {
                for (int row = 0; row < LineTheme.All.Length; row++)
                {
                    var theme = LineTheme.All[row]; shelf.ApplyTheme(theme); shelf.UpdateLayout();
                    var image = (Image)typeof(ShelfWindow).GetField("basketImage", flags).GetValue(shelf);
                    Check(image.Source != null && image.Source == PictureBasket.Source(theme), theme.Name + "切换到对应篮子素材");
                    Point digit = count.TransformToAncestor(shelf.BasketButton).Transform(new Point(count.ActualWidth / 2, 0));
                    Rect body = image.TransformToAncestor(shelf.BasketButton).TransformBounds(new Rect(image.RenderSize));
                    Check(digit.Y > body.Bottom && Math.Abs(digit.X - (body.Left + body.Width / 2)) < 0.6,
                        theme.Name + "数字始终在篮子正下方居中");
                    var line = (System.Windows.Shapes.Path)typeof(ShelfWindow).GetField("rope", flags).GetValue(shelf);
                    var curve = (QuadraticBezierSegment)((PathGeometry)line.Data).Figures[0].Segments[0];
                    Point hook = image.TransformToAncestor(shelf).Transform(PictureBasket.Hook(theme));
                    Point start = line.TransformToAncestor(shelf).Transform(new Point(0, 18));
                    double fraction = (hook.X - start.X) / curve.Point2.X;
                    double lineY = start.Y + 2 * (curve.Point1.Y - 18) * fraction * (1 - fraction);
                    Check(Math.Abs(hook.Y - lineY) < 0.2, theme.Name + "挂钩支点与绳子曲线衔接");
                    var snapshot = new RenderTargetBitmap((int)shelf.ActualWidth, 220, 96, 96, PixelFormats.Pbgra32);
                    snapshot.Render(shelf); snapshot.Freeze();
                    var crop = new CroppedBitmap(snapshot, new Int32Rect((int)shelf.ActualWidth - 220, 0, 220, 176)); crop.Freeze();
                    double y = row * 176;
                    painter.DrawRectangle(Brushes.White, null, new Rect(0, y, 150, 176));
                    painter.DrawText(new FormattedText(theme.Name, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                        new Typeface("Microsoft YaHei UI"), 15, Brushes.Black, 1), new Point(18, y + 68));
                    for (int column = 0; column < 3; column++)
                    {
                        var cell = new Rect(150 + column * 220, y, 220, 176);
                        painter.DrawRectangle(column == 0 ? Brushes.White : LightTheme.Brush("#071A30"), null, cell);
                        if (column == 2)
                            for (int tile = 0; tile < 9; tile++) painter.DrawRectangle(LightTheme.Brush(tile % 2 == 0 ? "#F5EACD" : "#557854"), null,
                                new Rect(cell.X + tile * 25, cell.Y, 25, cell.Height));
                        painter.DrawImage(crop, cell);
                    }
                }
            }
            var bitmap = new RenderTargetBitmap(810, 880, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string preview = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "previews", "图片篓", "篮子皮肤实装.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(preview)); using (var file = File.Create(preview)) encoder.Save(file);
            shelf.BasketButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(controller.Basket != null, "新篮子入口仍能打开收集箱");
            await CheckBasketSelection(store);
            await CheckBasketDeletion(store);
            controller.Basket.Close();
        }

        static async Task CheckBasketDeletion(StateStore store)
        {
            var basket = controller.Basket;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            byte[] bytes = Asset("界面参考", "截图 · 随手取用", Color.FromRgb(61, 93, 141));
            string closePath = Path.Combine(store.Inbox, "叉号删除.png"); File.WriteAllBytes(closePath, bytes);
            controller.Gallery.Add(closePath, ImageContent.Decode(bytes));
            await Task.Delay(250);
            var card = controller.Shelf.CardFor(closePath);
            ((Button)typeof(ShotCard).GetField("close", flags).GetValue(card)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!File.Exists(closePath) && !controller.Gallery.Contains(closePath) && !controller.Gallery.Collected.Any(item => item.Path == closePath),
                "绳子上的叉号直接删除文件并移除收集记录，不放回篮子");
            Check(controller.Shelf.MotionWindows.Any(flight => flight.Falling) && !controller.Shelf.MotionWindows.Any(flight => flight.Collecting), "叉号删除使用下落动画，不触发投篮动画");
            await Until(() => !controller.Shelf.MotionWindows.Any());
            var field = typeof(BasketWindow).GetField("DeleteButton", flags | System.Reflection.BindingFlags.Public);
            Check(field != null, "图片篮子提供删除所选入口");
            var delete = (Button)field.GetValue(basket);
            Check(!delete.IsEnabled, "没有选择图片时删除按钮不可用");
            string pending = Path.Combine(store.Inbox, "待挂删除.png"), mounted = Path.Combine(store.Inbox, "已挂删除.png"), keep = Path.Combine(store.Inbox, "保留图片.png");
            foreach (string path in new[] { pending, mounted, keep }) { File.WriteAllBytes(path, bytes); controller.Gallery.Collect(path, ImageContent.Decode(bytes)); }
            controller.Gallery.Add(mounted, ImageContent.Decode(bytes));
            basket.AllTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            basket.ChoiceFor(mounted).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(basket.SelectedCount == 1 && delete.IsEnabled && !basket.HangButton.IsEnabled, "全部列表中的已挂图片可以选中删除，但不能重复挂图");
            basket.PendingTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(basket.SelectedCount == 0 && !delete.IsEnabled, "切回待挂时取消已隐藏图片的选择，避免删除不可见的项目");
            basket.AllTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            basket.ChoiceFor(mounted).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            basket.ChoiceFor(pending).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(basket.SelectedCount == 2 && delete.IsEnabled && basket.HangButton.IsEnabled, "待挂和已挂图片可以混合多选，挂图按钮只处理待挂图片");
            ((ScrollViewer)typeof(BasketWindow).GetField("scroll", flags).GetValue(basket)).ScrollToTop();
            basket.UpdateLayout(); await Task.Delay(100);
            var screenshot = new RenderTargetBitmap((int)basket.ActualWidth, (int)basket.ActualHeight, 96, 96, PixelFormats.Pbgra32); screenshot.Render(basket);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(screenshot));
            using (var output = File.Create(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "previews", "图片篓", "删除功能.png")))) encoder.Save(output);
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!File.Exists(pending) && !File.Exists(mounted) && !controller.Gallery.Contains(mounted)
                && !controller.Gallery.Collected.Any(item => item.Path == pending || item.Path == mounted), "批量删除同时清理已选原图、挂图和篮子记录");
            Check(File.Exists(keep) && controller.Gallery.Collected.Any(item => item.Path == keep), "未选中的图片不受删除影响");
            Check(basket.SelectedCount == 0 && !delete.IsEnabled && !basket.HangButton.IsEnabled, "删除完成后清空选择并同步按钮状态");
            var selectedPreview = (CaptureItem)typeof(BasketWindow).GetField("previewItem", flags).GetValue(basket);
            Check(selectedPreview == null || (selectedPreview.Path != pending && selectedPreview.Path != mounted), "删除当前预览后不会留下已删除图片的大图");
            basket.AllTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            foreach (var item in controller.Gallery.Collected.ToArray()) basket.ChoiceFor(item.Path).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(controller.Gallery.Collected.Count == 0 && basket.VisibleCount == 0 && basket.Preview.Source == null,
                "删除最后一批图片后显示空篮子并清空预览");
        }

        static async Task CheckBasketSelection(StateStore store)
        {
            var basket = controller.Basket;
            string second = Path.Combine(store.Inbox, "第二张待挂.png");
            byte[] picture = TallBasketAsset(); File.WriteAllBytes(second, picture);
            controller.Gallery.Collect(second, ImageContent.Decode(picture));
            basket.UpdateLayout();
            var first = controller.Gallery.Collected[0];
            var choice = basket.ChoiceFor(first.Path);
            Check(choice.ActualHeight < 200, "只有两张图片时卡片不会拉伸成整列的选中区域");
            Size size = choice.RenderSize;
            choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            basket.UpdateLayout();
            Check(basket.SelectedCount == 1 && basket.HangButton.IsEnabled && AutomationProperties.GetItemStatus(basket.ChoiceFor(first.Path)) == "已选择",
                "点击缩略图后勾选状态、已选数量与挂图按钮同步更新");
            Check(basket.ChoiceFor(first.Path) == choice, "选中后保留原卡片，连续点击和键盘焦点不会因重建丢失");
            Check(choice.RenderSize == size, "勾选不会改变卡片尺寸或挤动列表");
            basket.ChoiceFor(second).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(basket.SelectedCount == 2, "点击另一张图片可累加多选");
            basket.UpdateLayout(); await Task.Delay(120);
            var screenshot = new RenderTargetBitmap((int)basket.ActualWidth, (int)basket.ActualHeight, 96, 96, PixelFormats.Pbgra32); screenshot.Render(basket);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(screenshot));
            string preview = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "previews", "图片篓", "选中修复.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(preview)); using (var file = File.Create(preview)) encoder.Save(file);
            choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(basket.SelectedCount == 1 && AutomationProperties.GetItemStatus(choice) == "待挂", "再次点击原卡片可以取消选中");
            basket.AllTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            basket.PendingTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(basket.SelectedCount == 1 && AutomationProperties.GetItemStatus(basket.ChoiceFor(second)) == "已选择", "切换待挂和全部保留多选状态");
            for (int index = 0; index < 10; index++)
            {
                string path = Path.Combine(store.Inbox, "滚动选择_" + index + ".png"); File.WriteAllBytes(path, picture);
                controller.Gallery.Collect(path, ImageContent.Decode(picture));
            }
            basket.UpdateLayout();
            var scroll = (ScrollViewer)typeof(BasketWindow).GetField("scroll", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(basket);
            scroll.ScrollToEnd(); await Task.Delay(120);
            double offset = scroll.VerticalOffset;
            var last = controller.Gallery.Collected.Last();
            basket.ChoiceFor(last.Path).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); basket.UpdateLayout();
            await Task.Delay(120);
            Check(offset > 0 && Math.Abs(scroll.VerticalOffset - offset) < 0.5 && basket.SelectedCount == 2,
                "滚动后勾选不会跳回列表顶部或丢失此前选择");
            basket.HangButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(controller.Gallery.Contains(second) && controller.Gallery.Contains(last.Path) && basket.SelectedCount == 0,
                "批量挂图使用当前多选结果并清空选择");
        }

        static async Task CheckBasket(StateStore store, string watched)
        {
            Check(controller.Captures.AutoHang, "旧版自动挂图体验默认保留");
            await Task.Delay(600);
            controller.Shelf.UpdateLayout();
            await ClickBasketControl(controller.Shelf.BasketButton);
            await Until(() => controller.Basket != null);
            var basket = controller.Basket;
            Check(basket.VisibleCount == 0 && !basket.HangButton.IsEnabled, "实际点击绳边衣篓打开空状态，无选择时不能挂图");
            controller.ShowBasket();
            Check(controller.Basket == basket, "重复打开图片篓复用同一窗口");
            await ClickBasketControl(basket.AutoHang);
            Check(!controller.Captures.AutoHang, "面板开关可关闭自动挂图");
            basket.Close();
            controller.Shelf.Reveal(false, false, 0);
            testContext.Activate();
            SetCursorPos(800, 450);
            await Task.Delay(400);
            IntPtr focus = WindowsNative.GetForegroundWindow();
            string first = Path.Combine(watched, "工作流程.png");
            File.WriteAllBytes(first, Asset("工作流程", "选题 → 录制 → 发布", Color.FromRgb(25, 40, 63)));
            await Until(() => controller.Gallery.Collected.Count == 1);
            Check(controller.Gallery.Items.Count == 0 && File.Exists(first) && !controller.Shelf.Revealed, "关闭自动挂图后，文件夹截图只收集、不展开绳子");
            Check(WindowsNative.GetForegroundWindow() == focus, "安静收集不抢走前台焦点");
            controller.Captures.CollectClipboard = true;
            byte[] clipboard = Asset("临时笔记", "整理素材 · 留待取用", Color.FromRgb(220, 112, 76));
            await SetClipboardImage(clipboard);
            await Until(() => controller.Gallery.Collected.Count == 2);
            string owned = controller.Gallery.Collected.Last().Path;
            Check(controller.Gallery.Items.Count == 0 && FileRules.IsInside(owned, store.Inbox) && File.Exists(owned), "剪贴板截图也先保存本地，关闭自动挂图不停止收集");
            File.WriteAllBytes(Path.Combine(watched, "重复通知.png"), clipboard);
            await Task.Delay(700);
            Check(controller.Gallery.Collected.Count == 2, "手动挂图模式仍合并同次截图的双来源通知");
            controller.ShowBasket(); basket = controller.Basket;
            Check(basket.VisibleCount == 2, "收集的历史截图可在待挂列表挑选");
            await ClickBasketControl(basket.ChoiceFor(first));
            await ClickBasketControl(basket.ChoiceFor(owned));
            await Until(() => basket.Preview.Source != null);
            Check(basket.SelectedCount == 2 && basket.HangButton.IsEnabled, "实际鼠标多选更新数量与主按钮");
            Check(((BitmapSource)basket.Preview.Source).PixelWidth == 360, "右侧大图预览读取完整图片");
            await ClickBasketControl(basket.HangButton);
            Check(controller.Gallery.Items.Count == 2 && basket.VisibleCount == 0 && basket.SelectedCount == 0 && basket.IsVisible, "批量挂图成功后保留面板、清空选择并更新待挂列表");
            await ClickBasketControl(basket.AllTab);
            Check(basket.VisibleCount == 2 && AutomationProperties.GetItemStatus(basket.ChoiceFor(first)) == "已挂上", "全部列表保留图片并标记已挂上");
            await ClickBasketControl(basket.ChoiceFor(first));
            Check(basket.SelectedCount == 1 && basket.DeleteButton.IsEnabled && !basket.HangButton.IsEnabled, "已挂上的图片可选择删除，但不会重复挂图");
            controller.Act(ShotAction.Remove, controller.Gallery.Items.Single(i => i.Path == owned));
            Check(File.Exists(owned) && controller.Gallery.Collected.Count == 2 && controller.Gallery.Items.Count == 1, "从绳子取下图片保留本地文件与收集记录");
            await ClickBasketControl(basket.PendingTab);
            Check(basket.VisibleCount == 1, "取下的图片立即回到待挂列表");
            await ClickBasketControl(basket.AutoHang);
            Check(controller.Captures.AutoHang && controller.Gallery.Items.Count == 1, "重新开启自动挂图不会补挂旧截图");
            string next = Path.Combine(watched, "界面参考.png");
            File.WriteAllBytes(next, Asset("界面参考", "卡片 · 拖拽 · 复制", Color.FromRgb(61, 93, 181)));
            await Until(() => controller.Gallery.Items.Count == 2);
            Check(controller.Gallery.Contains(next) && controller.Gallery.Collected.Count == 3, "开关重新开启后，新截图自动挂上并进入图片篓");
            await ClickBasketControl(basket.AutoHang);
            basket.Close();
            string historical = Path.Combine(store.Inbox, "历史未挂.png");
            File.WriteAllBytes(historical, Asset("历史截图", "本地保存 · 随时可取", Color.FromRgb(61, 120, 105)));
            controller.Dispose();
            controller = new DesktopController(new StateStore(store.Root), watched, false);
            await controller.Start();
            Check(!controller.Captures.AutoHang && controller.Gallery.Items.Count == 2 && controller.Gallery.Collected.Count == 4, "重启恢复开关、挂图和收集记录，并找回旧收集目录里的图片");
            File.Delete(historical);
            await Until(() => controller.Gallery.Collected.Count == 3);
            Check(controller.Gallery.Items.Count == 2, "外部删除待挂图片后清理失效记录，不影响现有挂图");
            for (int i = 0; i < 4; i++)
            {
                string path = Path.Combine(watched, "参考_" + i + ".png");
                File.WriteAllBytes(path, Asset(new[] { "拍摄计划", "素材整理", "产品界面", "旅行参考" }[i], "截图 · 标注 · 收藏", Color.FromRgb((byte)(90 + i * 22), (byte)(120 - i * 9), (byte)(155 - i * 15))));
            }
            string tall = Path.Combine(watched, "拍摄准备清单.png");
            File.WriteAllBytes(tall, TallBasketAsset());
            await Until(() => controller.Gallery.Collected.Count == 8);
            controller.ShowBasket(); basket = controller.Basket;
            await ClickBasketControl(basket.ChoiceFor(owned));
            await ClickBasketControl(basket.ChoiceFor(tall));
            await Until(() => ((BitmapSource)basket.Preview.Source).PixelHeight == 960);
            Check(((BitmapSource)basket.Preview.Source).PixelWidth == 540 && basket.SelectedCount == 2, "长截图在右侧保持完整比例预览，多选状态保持正确");
            await Task.Delay(500);
            ExportBasketPreview(basket);
            Check(!File.Exists(Path.Combine(store.Root, "error.log")), "图片篓完整操作过程没有运行错误");
        }

        static async Task ClickBasketControl(FrameworkElement control)
        {
            Point center = control.PointToScreen(new Point(control.ActualWidth / 2, control.ActualHeight / 2));
            SetCursorPos((int)center.X, (int)center.Y);
            await Task.Delay(140);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(45);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(160);
        }

        static byte[] TallBasketAsset()
        {
            var drawing = new DrawingVisual();
            using (var painter = drawing.RenderOpen())
            {
                painter.DrawRectangle(Brushes.White, null, new Rect(0, 0, 540, 960));
                var lines = new[] { "拍摄准备清单", "", "一、设备检查", "□ 相机机身（电量充足）", "□ 镜头、存储卡、三脚架", "□ 麦克风、补光灯", "□ 备用电池、充电器", "", "二、素材整理", "□ 确认拍摄主题与分镜脚本", "□ 准备参考图片与风格样片", "□ 整理场景道具与服装", "", "三、现场安排", "□ 确认拍摄时间与人员分工", "□ 与场地方沟通拍摄许可", "□ 检查天气与光线变化", "□ 再次核对设备与素材清单" };
                for (int i = 0; i < lines.Length; i++)
                    painter.DrawText(new FormattedText(lines[i], CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), i == 0 ? 29 : 21, LightTheme.Brush("#283343"), 1), new Point(45, 60 + i * 44));
            }
            var bitmap = new RenderTargetBitmap(540, 960, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            return ImageContent.EncodeClipboard(bitmap);
        }

        static void ExportBasketPreview(BasketWindow basket)
        {
            string directory = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "previews", "图片篓"));
            Directory.CreateDirectory(directory);
            CaptureScreen(new Rect(basket.PointToScreen(new Point()), basket.PointToScreen(new Point(basket.ActualWidth, basket.ActualHeight))), Path.Combine(directory, "实际窗口.png"));
            var drawing = new DrawingVisual();
            using (var painter = drawing.RenderOpen())
            {
                painter.DrawRectangle(LightTheme.Brush("#EDF2F9"), null, new Rect(0, 0, 1440, 1024));
                painter.DrawRectangle(new VisualBrush(controller.Shelf), null, new Rect(0, 0, 1440, 220 * 1440 / controller.Shelf.ActualWidth));
                painter.DrawRectangle(new VisualBrush(basket), null, new Rect(104, 188, 1232, 1232 * basket.ActualHeight / basket.ActualWidth));
            }
            var bitmap = new RenderTargetBitmap(1440, 1024, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(directory, "实际界面.png"))) encoder.Save(file);
        }

        static async Task CheckThemes(StateStore store, string watched)
        {
            Check(controller.Shelf.Appearance.Id == "classic", "首次启动使用经典银灰主题");
            Directory.CreateDirectory(store.Inbox);
            string[] titles = { "工作流程", "临时笔记", "界面参考" };
            Color[] colors = { Color.FromRgb(25, 40, 63), Color.FromRgb(220, 112, 76), Color.FromRgb(61, 93, 181) };
            for (int i = 0; i < titles.Length; i++)
            {
                string file = Path.Combine(store.Inbox, titles[i] + ".png");
                byte[] image = Asset(titles[i], "截图 · 标注 · 收藏", colors[i]);
                File.WriteAllBytes(file, image); controller.Gallery.Add(file, ImageContent.Decode(image));
            }
            var shelf = controller.Shelf;
            var cards = controller.Gallery.Items.Select(item => shelf.CardFor(item.Path)).ToArray();
            var originals = cards.Select(card => File.ReadAllBytes(card.Item.Path)).ToArray();
            var screen = shelf.Display;
            double scale = WindowsNative.MonitorScale(screen);
            var backdrop = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                Left = screen.WorkingArea.Left / scale, Top = screen.WorkingArea.Top / scale,
                Width = screen.WorkingArea.Width / scale, Height = screen.WorkingArea.Height / scale - 40,
                Background = LightTheme.Brush("#F3F4F4"), ShowInTaskbar = false, ShowActivated = false, Topmost = true };
            backdrop.Show();
            WindowsNative.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(backdrop).Handle, shelf.Handle, 0, 0, 0, 0, 0x13);
            string directory = Path.Combine(Path.GetDirectoryName(PinPreviewPath("")), "皮肤主题");
            Directory.CreateDirectory(directory);
            try
            {
                shelf.Reveal(true, true, 0);
                await Task.Delay(1300);
                var appearanceMenu = controller.TrayMenu.AppearanceButton;
                appearanceMenu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var chooser = controller.Appearance;
                await Task.Delay(200);
                Check(chooser != null && chooser.Options.Count == 5, "外观入口打开五套主题的选择面板");
                appearanceMenu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(controller.Appearance == chooser, "重复打开外观只激活同一个面板");
                SetCursorPos(screen.WorkingArea.Right - 20, screen.WorkingArea.Bottom - 20);
                await Task.Delay(2300);
                Check(shelf.Revealed, "外观面板打开期间保留晾衣绳的实时预览");
                CaptureScreen(new Rect(chooser.PointToScreen(new Point()), chooser.PointToScreen(new Point(chooser.ActualWidth, chooser.ActualHeight))), Path.Combine(directory, "实际-外观切换.png"));
                foreach (var option in chooser.Options)
                {
                    var theme = (LineTheme)option.Tag;
                    Point target = option.PointToScreen(new Point(option.ActualWidth / 2, option.ActualHeight / 2));
                    SetCursorPos((int)target.X, (int)target.Y); await Task.Delay(50);
                    mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                    await Task.Delay(140);
                    Check(shelf.Appearance == theme && cards.All(mounted => ((ClothesClip)mounted.Grip.Content).Appearance == theme)
                        && new StateStore(store.Root).State.Theme == theme.Id, theme.Name + "实际点击后整套生效并保存");
                    Check(chooser.Options.Count(button => System.Windows.Automation.AutomationProperties.GetItemStatus(button) == "已选择") == 1,
                        theme.Name + "切换后只有一个主题处于选中状态");
                    SetCursorPos(screen.WorkingArea.Right - 20, screen.WorkingArea.Bottom - 20);
                    shelf.Reveal(true, true, 0); await Task.Delay(100);
                    CaptureScreen(new Rect(shelf.PointToScreen(new Point()), shelf.PointToScreen(new Point(shelf.ActualWidth, 180))), Path.Combine(directory, "实际-" + theme.Name + ".png"));
                    var card = cards[0];
                    var releaseClock = System.Diagnostics.Stopwatch.StartNew();
                    card.Grip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => shelf.PinWindows.Any());
                    Check(releaseClock.ElapsedMilliseconds < 220, theme.Name + "点击夹子及时放开照片");
                    var pin = shelf.PinWindows.Single();
                    await Until(() => pin.IsHitTestVisible);
                    Check(((Thumbtack)pin.PinButton.Content).Appearance == theme, theme.Name + "桌面图钉继承夹子的主题");
                    ExportPinPreview(pin, "图钉-" + theme.Name + ".png");
                    LineTheme alternate = theme == LineTheme.All[4] ? LineTheme.All[0] : LineTheme.All[4];
                    var alternateOption = chooser.Options.Single(button => button.Tag == alternate);
                    Rect before = pin.PhotoRect();
                    alternateOption.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(shelf.PinWindows.Single() == pin && pin.PhotoRect() == before, "切换主题保留现有桌面贴图的位置和尺寸");
                    Check(((Thumbtack)pin.PinButton.Content).Appearance == alternate, "已有桌面图钉同步切换主题");
                    pin.PinButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => !shelf.PinWindows.Any());
                    var flight = shelf.MotionWindows.Single();
                    var flightClip = ((Canvas)((Canvas)flight.Content).Children[0]).Children.OfType<ClothesClip>().Single();
                    Check(flightClip.Appearance == alternate, "挂回照片使用当前主题的夹子");
                    option.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(flightClip.Appearance == theme, "飞行动画途中切换主题，夹子同步更新");
                    await Until(() => !shelf.MotionWindows.Any() && !card.Flying);
                    Check(card.Grip.IsEnabled && card.Appearance == theme, theme.Name + "挂回后夹子合拢并可再次使用");
                    Check(cards.Select((entry, index) => shelf.CardFor(entry.Item.Path) == entry
                        && File.ReadAllBytes(entry.Item.Path).SequenceEqual(originals[index])).All(unchanged => unchanged), "切换保留原卡片与原图文件");
                }
                string added = Path.Combine(store.Inbox, "主题切换后新图.png");
                File.WriteAllBytes(added, originals[0]); controller.Gallery.Add(added, ImageContent.Decode(originals[0]));
                Check(shelf.CardFor(added).Appearance.Id == "graphite", "新增截图继承当前整套主题");
                controller.Dispose(); controller = new DesktopController(new StateStore(store.Root), watched, false);
                await controller.Start();
                Check(controller.Shelf.Appearance.Id == "graphite" && controller.Gallery.Items.Count == 4
                    && controller.Gallery.Items.All(item => controller.Shelf.CardFor(item.Path).Appearance.Id == "graphite"), "重新启动恢复主题与全部挂图");
                Check(!File.Exists(Path.Combine(store.Root, "error.log")), "主题选择、下落、切换、飞回和重启全过程没有运行错误");
            }
            finally { backdrop.Close(); }
        }

        static async Task CheckPins(StateStore store)
        {
            controller.Gallery.Clear();
            Directory.CreateDirectory(store.Inbox);
            string path = Path.Combine(store.Inbox, "桌面贴图.png");
            byte[] bytes = Asset("工作流程", "截图 · 标注 · 收藏", Color.FromRgb(61, 93, 160));
            foreach (string name in new[] { "界面参考.png", "桌面贴图.png", "临时笔记.png" })
            {
                string imagePath = Path.Combine(store.Inbox, name);
                File.WriteAllBytes(imagePath, bytes);
                controller.Gallery.Add(imagePath, ImageContent.Decode(bytes));
            }
            var shelf = controller.Shelf;
            var card = shelf.CardFor(path);
            shelf.Reveal(true, true, 0);
            await Task.Delay(1500);
            var cardClose = ((Grid)card.Surface.Child).Children.OfType<Button>().Single();
            Check(CloseIconOffset(cardClose) < 0.6, "挂图左上角叉号的实际笔画居中");
            uint clipboard = WindowsNative.GetClipboardSequenceNumber();
            IntPtr focus = WindowsNative.GetForegroundWindow();
            Rect origin = card.PhotoRect();
            CaptureScreen(new Rect(card.Grip.PointToScreen(new Point(-20, -10)), card.Grip.PointToScreen(new Point(48, 44))), PinPreviewPath("夹子闭合.png"));
            var hint = (ToolTip)card.Grip.ToolTip;
            hint.PlacementTarget = card.Grip;
            hint.IsOpen = true;
            Task motion = RecordMotion(shelf);
            var releaseClock = System.Diagnostics.Stopwatch.StartNew();
            card.Grip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            card.Grip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100);
            var jaws = ((Canvas)card.Grip.Content).Children.OfType<Canvas>().ToArray();
            Check(((RotateTransform)jaws[0].RenderTransform).Angle < -3 && ((RotateTransform)jaws[1].RenderTransform).Angle > 3, "点击夹子后上端轻捏、下端松开照片");
            Check(!hint.IsOpen && !ToolTipService.GetIsEnabled(card.Grip), "点击后立即收起提示，不遮挡松开动作");
            while (!shelf.PinWindows.Any() && releaseClock.ElapsedMilliseconds < 3000) await Task.Delay(10);
            double releaseMilliseconds = releaseClock.Elapsed.TotalMilliseconds;
            Console.WriteLine("点击到照片松开：{0:F0} 毫秒", releaseMilliseconds);
            Check(releaseMilliseconds < 200, "单击后及时松开照片，没有额外停顿");
            CaptureScreen(new Rect(card.Grip.PointToScreen(new Point(-20, -10)), card.Grip.PointToScreen(new Point(48, 44))), PinPreviewPath("夹子松开.png"));
            var pin = shelf.PinWindows.Single();
            Check(shelf.PinWindows.Count() == 1 && card.Detached && controller.Gallery.Items.Count == 3, "连续点击只生成一张贴图，保留展示记录和原文件");
            Check(!card.Grip.IsEnabled, "照片松开后夹子停止接收重复点击");
            var landingClock = System.Diagnostics.Stopwatch.StartNew();
            await Until(() => pin.IsHitTestVisible);
            Check(landingClock.Elapsed.TotalMilliseconds < 800, "下落与落地及时完成，贴图可以立即操作");
            await motion;
            Rect landed = pin.PhotoRect();
            Check(landed.Width > origin.Width * 1.5, "照片下落时从挂图尺寸平滑放大为桌面贴图");
            Check(landed.Y > origin.Y + 100 && card.Opacity == 0, "照片自动落到绳子下方的桌面区域");
            await Task.Delay(200);
            Check(Math.Abs(pin.PhotoRect().Y - landed.Y) < 0.1 && Math.Abs(pin.PhotoRect().X - landed.X) < 0.1, "照片落稳后固定位置，不跟随鼠标");
            Check(WindowsNative.GetForegroundWindow() == focus && WindowsNative.GetClipboardSequenceNumber() == clipboard, "点击夹子不抢前台焦点，也不写入剪贴板");
            IntPtr pinHandle = new System.Windows.Interop.WindowInteropHelper(pin).Handle;
            Check(pin.Topmost && !pin.ShowInTaskbar && GetProp(pinHandle, "NonRudeHWND") == new IntPtr(1), "桌面贴图置顶且不占任务栏，不触发任务栏让位");
            Check(pin.Picture.PixelWidth == card.Item.Image.PixelWidth && pin.Picture.PixelHeight == card.Item.Image.PixelHeight, "桌面贴图加载完整分辨率，不放大模糊的缩略图");
            Check(pin.PinButton.Opacity > 0.99 && pin.PinButton.IsEnabled
                && System.Windows.Automation.AutomationProperties.GetName(pin.PinButton) == "挂回晾衣绳", "图钉落稳后常显，并明确提供挂回操作");
            Check(((Grid)pin.Frame.Child).Children.OfType<Button>().Count() == 0, "图钉替代图片内的叉号，不遮挡截图内容");
            Rect pinBounds = new Rect(pin.PinButton.PointToScreen(new Point()), pin.PinButton.PointToScreen(new Point(pin.PinButton.ActualWidth, pin.PinButton.ActualHeight)));
            Check(Math.Abs(pinBounds.X + pinBounds.Width / 2 - landed.X - landed.Width / 2) < 1
                && pinBounds.Y < landed.Y && pin.PinButton.ActualWidth >= 28, "图钉居中位于上沿外侧，保留足够的点击区域");
            Rect beforeMove = pin.PhotoRect();
            var cursor = WindowsNative.Cursor();
            pin.Frame.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            await Task.Delay(110);
            Check(((TranslateTransform)pin.PinButton.RenderTransform).Y < -3, "拖动图片时图钉轻轻提起");
            ExportPinPreview(pin, "图钉提起.png");
            SetCursorPos(cursor.X + 60, cursor.Y + 50);
            pin.Frame.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseMoveEvent });
            pin.Frame.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            SetCursorPos(cursor.X, cursor.Y);
            await Task.Delay(180);
            Check(Math.Abs(((TranslateTransform)pin.PinButton.RenderTransform).Y) < 0.01, "松手后图钉重新按下");
            Rect afterMove = pin.PhotoRect();
            Check(Math.Abs(afterMove.X - beforeMove.X - 60) < 5 && Math.Abs(afterMove.Y - beforeMove.Y - 50) < 5, "落稳后拖动贴图可调整位置");
            pin.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            await Task.Delay(100);
            Check(pin.PhotoRect().Width > afterMove.Width + 10, "滚轮能够缩放桌面贴图");
            Check(Math.Abs(pin.PinButton.ActualWidth * WindowsNative.MonitorScale(shelf.Display) - pinBounds.Width) < 1
                && Math.Abs(pin.PinButton.PointToScreen(new Point(pin.PinButton.ActualWidth / 2, 0)).X - pin.PhotoRect().X - pin.PhotoRect().Width / 2) < 1,
                "缩放图片时图钉保持固定大小并跟随上沿中心");
            pin.Frame.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            await Task.Delay(110);
            pin.Frame.ReleaseMouseCapture();
            await Task.Delay(180);
            Check(Math.Abs(((TranslateTransform)pin.PinButton.RenderTransform).Y) < 0.01, "拖动中失去鼠标捕获后图钉恢复钉住状态");
            ExportPinPreview(pin, "桌面贴图.png");
            shelf.Reveal(false, false, 0);
            await Task.Delay(300);
            Check(pin.IsVisible && shelf.PinWindows.Count() == 1, "晾衣绳收起后桌面贴图仍保持显示");
            Point pinTarget = pin.PinButton.PointToScreen(new Point(pin.PinButton.ActualWidth / 2, pin.PinButton.ActualHeight / 2));
            SetCursorPos((int)pinTarget.X, (int)pinTarget.Y); await Task.Delay(50);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(30);
            Check(!pin.PinButton.IsEnabled && card.Detached, "点击图钉先拔起，阻止重复触发挂回");
            pin.PinButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !shelf.PinWindows.Any());
            Check(shelf.Revealed && shelf.MotionWindows.Count() == 1 && !card.Detached, "拔下图钉后展开绳子，并沿弧线飞回原位置");
            await Until(() => !shelf.MotionWindows.Any() && !card.Flying);
            Check(card.Opacity > 0.9 && card.Grip.IsEnabled && File.ReadAllBytes(path).SequenceEqual(bytes), "贴图挂回后夹子合拢，可再次点击，原文件保持不变");
            card.Grip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => shelf.PinWindows.Any());
            controller.Gallery.Clear();
            Check(!shelf.PinWindows.Any() && File.Exists(path), "全部取下时清理桌面贴图窗口，保留原文件");
            controller.Gallery.Add(path, ImageContent.Decode(bytes));
            shelf.CardFor(path).Grip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            controller.Gallery.Clear();
            await Task.Delay(220);
            Check(!shelf.PinWindows.Any(), "夹子张开期间清空列表，不会留下延迟出现的贴图");
            Check(!File.Exists(Path.Combine(store.Root, "error.log")), "松开、下落、贴图和回挂全过程没有运行错误");
        }

        static async Task CheckCopyFeedback(ShotCard card)
        {
            var feedback = card.Children.OfType<FrameworkElement>().Single(child => !child.IsHitTestVisible);
            Check(feedback.Opacity > 0.99, "复制后提示完整显示");
            feedback.UpdateLayout();
            const int scale = 4;
            int width = (int)Math.Ceiling(feedback.ActualWidth * scale), height = (int)Math.Ceiling(feedback.ActualHeight * scale);
            var drawing = new DrawingVisual();
            using (var painter = drawing.RenderOpen())
                painter.DrawRectangle(new VisualBrush(feedback), null, new Rect(0, 0, feedback.ActualWidth, feedback.ActualHeight));
            var image = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            image.Render(drawing);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "copied-feedback.png"))) encoder.Save(output);
            var pixels = new byte[width * height * 4];
            image.CopyPixels(pixels, width * 4, 0);
            int minimumAlpha = 255;
            double darkest = 1, lightest = 0;
            for (int y = height / 3; y < height * 2 / 3; y++)
                for (int x = 12 * scale; x < width - 12 * scale; x++)
                {
                    int index = (y * width + x) * 4;
                    minimumAlpha = Math.Min(minimumAlpha, pixels[index + 3]);
                    double luminance = 0;
                    for (int channel = 0; channel < 3; channel++)
                    {
                        double value = pixels[index + channel] / 255.0;
                        luminance += (channel == 0 ? 0.0722 : channel == 1 ? 0.7152 : 0.2126)
                            * (value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4));
                    }
                    darkest = Math.Min(darkest, luminance); lightest = Math.Max(lightest, luminance);
                }
            Check(minimumAlpha >= 250, "复制提示的文字区域遮住壁纸，深浅背景均不透底");
            Check((lightest + 0.05) / (darkest + 0.05) >= 4.5, "复制提示实际文字与底色的对比度至少为 4.5:1");
            Check(Math.Abs(Canvas.GetLeft(feedback) + feedback.ActualWidth / 2 - card.Width / 2) < 1, "复制提示位于图片下方中央");
            await Task.Delay(1000);
            Check(feedback.Opacity < 0.01, "复制提示按时淡出");
            card.FlashCopied();
            await Task.Delay(200);
            Check(feedback.Opacity > 0.99, "再次复制可重新显示提示");
        }

        static double CloseIconOffset(Button button)
        {
            button.BeginAnimation(UIElement.OpacityProperty, null);
            double opacity = button.Opacity;
            button.Opacity = 1;
            button.UpdateLayout();
            const int scale = 4;
            int width = (int)Math.Round(button.ActualWidth * scale), height = (int)Math.Round(button.ActualHeight * scale);
            var image = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            Vector offset = VisualTreeHelper.GetOffset(button);
            using (var painter = drawing.RenderOpen())
                painter.DrawRectangle(new VisualBrush(button) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(offset.X, offset.Y, button.ActualWidth, button.ActualHeight) }, null, new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            image.Render(drawing);
            button.Opacity = opacity;
            var pixels = new byte[width * height * 4];
            image.CopyPixels(pixels, width * 4, 0);
            int left = width, right = -1, top = height, bottom = -1;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int index = (y * width + x) * 4;
                    if (pixels[index + 3] < 200 || pixels[index] > 110 || pixels[index + 1] > 110 || pixels[index + 2] > 110) continue;
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
            Check(right >= left && bottom >= top, "关闭按钮的叉号可见");
            return Math.Max(Math.Abs((left + right + 1 - width) / (2.0 * scale)), Math.Abs((top + bottom + 1 - height) / (2.0 * scale)));
        }

        static void CaptureScreen(Rect area, string path)
        {
            using (var image = new System.Drawing.Bitmap((int)area.Width, (int)area.Height))
            using (var painter = System.Drawing.Graphics.FromImage(image))
            {
                painter.CopyFromScreen((int)area.X, (int)area.Y, 0, 0, image.Size);
                image.Save(path);
            }
        }

        static void ExportPinPreview(PinnedImageWindow pin, string name)
        {
            var drawing = new DrawingVisual();
            using (var painter = drawing.RenderOpen())
            {
                painter.DrawRectangle(LightTheme.Brush("#F0F2F5"), null, new Rect(0, 0, 840, 640));
                painter.DrawRectangle(new VisualBrush(pin), null, new Rect((840 - pin.ActualWidth) / 2, (640 - pin.ActualHeight) / 2, pin.ActualWidth, pin.ActualHeight));
            }
            var bitmap = new RenderTargetBitmap(840, 640, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(PinPreviewPath(name))) encoder.Save(output);
        }

        static string PinPreviewPath(string name)
        {
            string project = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            if (!Directory.Exists(Path.Combine(project, "src"))) project = Path.GetDirectoryName(project);
            string directory = Path.Combine(project, "previews", "桌面贴图");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, name);
        }

        static async Task CheckIdleWind(StateStore store)
        {
            var shelf = controller.Shelf;
            Directory.CreateDirectory(store.Inbox);
            string path = Path.Combine(store.Inbox, "微风验证.png");
            byte[] bytes = Asset("随机微风", "静止等待 · 自然轻摆", Color.FromRgb(61, 93, 160));
            File.WriteAllBytes(path, bytes);
            controller.Gallery.Add(path, ImageContent.Decode(bytes));
            SetCursorPos(shelf.Display.WorkingArea.Left + 300, shelf.Display.WorkingArea.Top + 450);
            WindowsNative.ActivateEditor(new System.Windows.Interop.WindowInteropHelper(testContext).Handle);
            shelf.Reveal(false, false, 0);
            await Task.Delay(300);
            shelf.Reveal(true, true, 0);
            var timer = typeof(ShelfWindow).GetField("nextGust", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var card = shelf.CardFor(path);
            var rotation = (RotateTransform)card.RenderTransform;
            DateTime due = (DateTime)timer.GetValue(shelf);
            double delay = (due - DateTime.UtcNow).TotalSeconds;
            Check(delay > 0 && delay <= 24, "展开时已安排自然微风倒计时");
            shelf.Reveal(false, false, 0);
            await Task.Delay(300);
            shelf.Reveal(true, true, 0);
            Check((DateTime)timer.GetValue(shelf) == due, "重新展开时保留已有微风倒计时");
            await Task.Delay(4000);
            typeof(ShelfWindow).GetMethod("StopRendering", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(shelf, null);
            Check(!shelf.Animating, "静止场景暂停逐帧刷新");
            await Task.Delay(TimeSpan.FromSeconds((due - DateTime.UtcNow).TotalSeconds + 1));
            Check(shelf.Revealed, "静止等待时绳子保持展开");
            Check((DateTime)timer.GetValue(shelf) > due, "无需滚动或鼠标操作，静止时自动触发自然微风");
            await Until(() => Math.Abs(rotation.Angle - card.Tilt) > 0.2);
            Check(Math.Abs(rotation.Angle - card.Tilt) > 0.2, "自然微风实际吹动图片");
            timer.SetValue(shelf, DateTime.UtcNow.AddMinutes(1));
            await Task.Delay(4000);
            Check(Math.Abs(rotation.Angle - card.Tilt) < 0.2, "自然微风结束后图片回稳");
            shelf.Reveal(false, false, 0);
            due = DateTime.UtcNow;
            timer.SetValue(shelf, due);
            await Task.Delay(400);
            Check(!shelf.Animating && (DateTime)timer.GetValue(shelf) == due, "收起时停止动画并保留到期微风");
            shelf.Reveal(true, true, 0);
            await Until(() => (DateTime)timer.GetValue(shelf) > due);
            Check((DateTime)timer.GetValue(shelf) > due, "收起期间到期的微风在重新展开后触发");
            shelf.Reveal(false, false, 0);
        }

        static async Task CheckScrollBoundaries(StateStore store)
        {
            var shelf = controller.Shelf;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var viewport = (ScrollViewer)typeof(ShelfWindow).GetField("viewport", flags).GetValue(shelf);
            var rope = (System.Windows.Shapes.Path)typeof(ShelfWindow).GetField("rope", flags).GetValue(shelf);
            var motion = (SpringMotion)typeof(ShelfWindow).GetField("scrollMotion", flags).GetValue(shelf);
            double width = shelf.Width;
            bool choosing = shelf.ChoosingBasket;
            try
            {
                shelf.ChoosingBasket = true;
                controller.Gallery.Clear();
                Directory.CreateDirectory(store.Inbox);
                for (int i = 0; i < 50; i++)
                {
                    string path = Path.Combine(store.Inbox, "边界验证_" + i + ".png");
                    byte[] bytes = Asset("第 " + (i + 1) + " 张截图", "工作参考 · 随手取用", Color.FromRgb((byte)(30 + i * 3), (byte)(65 + i), (byte)(130 + i)));
                    File.WriteAllBytes(path, bytes);
                    controller.Gallery.Add(path, ImageContent.Decode(bytes));
                }
                shelf.Width = 840;
                motion.Snap(0); viewport.ScrollToHorizontalOffset(0);
                shelf.Reveal(true, true, 60);
                typeof(ShelfWindow).GetField("nextGust", flags).SetValue(shelf, DateTime.UtcNow.AddMinutes(5));
                await Task.Delay(900);
                Point ropeStart = rope.TransformToAncestor(shelf).Transform(new Point(0, 18));
                Point basketStart = shelf.BasketButton.PointToScreen(new Point());
                var card = shelf.CardFor(controller.Gallery.Items[2].Path);
                viewport.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                await Until(() => viewport.HorizontalOffset > 40);
                Point after = rope.TransformToAncestor(shelf).Transform(new Point(0, 18));
                Check(Math.Abs(after.X - ropeStart.X) < 0.1 && Math.Abs(after.Y - ropeStart.Y) < 0.1, "50 张图片滚动时绳子保持固定，衣篓边缘不会断线");
                Check((shelf.BasketButton.PointToScreen(new Point()) - basketStart).Length < 0.1, "滚动长列表不会挪动衣篓入口");
                await Task.Delay(650);
                Point pin = card.TransformToAncestor(viewport).Transform(new Point(82, 8));
                var curve = (QuadraticBezierSegment)((PathGeometry)rope.Data).Figures[0].Segments[0];
                double fraction = pin.X / curve.Point2.X;
                Check(Math.Abs(pin.Y - (18 + 2 * (curve.Point1.Y - 18) * fraction * (1 - fraction))) < 0.4, "滚动中的夹子继续贴合屏幕上固定的绳子");
                viewport.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120000) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                bool bounded = true;
                for (int i = 0; i < 65; i++)
                {
                    await Task.Delay(16);
                    bounded &= motion.Position >= -0.1 && motion.Position <= viewport.ScrollableWidth + 0.1;
                }
                Check(bounded, "快速滚到末尾时内部滚动位置也不会越界回弹");
                var last = shelf.CardFor(controller.Gallery.Items.Last().Path);
                Rect lastBounds = last.Surface.TransformToAncestor(viewport).TransformBounds(new Rect(last.Surface.RenderSize));
                Check(lastBounds.Left >= 0 && lastBounds.Right <= viewport.ActualWidth + 0.5, "滚到末尾可以完整看见最后一张图");
                viewport.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                await Task.Delay(100);
                Check(viewport.HorizontalOffset < viewport.ScrollableWidth - 5, "到达末尾后反向滚动立即响应");
                viewport.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120000) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                bounded = true;
                for (int i = 0; i < 65; i++)
                {
                    await Task.Delay(16);
                    bounded &= motion.Position >= -0.1 && motion.Position <= viewport.ScrollableWidth + 0.1;
                }
                Check(bounded && viewport.HorizontalOffset < 0.5, "快速反向滚到开头不会越界或留下空白");
                var hidden = shelf.CardFor(controller.Gallery.Items.Last().Path);
                Point hiddenCenter = hidden.Surface.PointToScreen(new Point(hidden.Surface.ActualWidth / 2, hidden.Surface.ActualHeight / 2));
                bool hit = (bool)typeof(ShelfWindow).GetMethod("OverControl", flags).Invoke(shelf, new object[] { new WindowsNative.Point { X = (int)hiddenCenter.X, Y = (int)hiddenCenter.Y } });
                Check(!hit, "屏幕外的图片不会留下可点击区域");
                viewport.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120000) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                await Task.Delay(750);
                shelf.Width = 1040;
                await Task.Delay(400);
                Check(motion.Position <= viewport.ScrollableWidth + 0.1, "调整窗口或显示器宽度后滚动位置同步收回边界");
                last.Grip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => shelf.PinWindows.Any());
                var pinned = shelf.PinWindows.Single();
                await Until(() => pinned.IsHitTestVisible);
                pinned.PinButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => !shelf.PinWindows.Any() && !last.Flying);
                Check(!last.Detached && controller.Gallery.Contains(last.Item.Path), "滚动至末尾后仍可取下贴图并挂回绳子");
                await Task.Delay(250);
                var drawing = new DrawingVisual();
                using (var painter = drawing.RenderOpen())
                {
                    painter.DrawRectangle(LightTheme.Brush("#07172F"), null, new Rect(0, 0, shelf.ActualWidth, 220));
                    painter.DrawRectangle(new VisualBrush(shelf), null, new Rect(0, 0, shelf.ActualWidth, 220));
                }
                var bitmap = new RenderTargetBitmap((int)shelf.ActualWidth, 220, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scroll-boundaries.png"))) encoder.Save(file);
                foreach (var item in controller.Gallery.Items.Skip(2).ToArray()) controller.Gallery.Remove(item);
                await Task.Delay(450);
                Check(viewport.HorizontalOffset < 0.5 && Math.Abs(motion.Position) < 0.1, "取下大量图片后收回滚动空白，保留的图片仍在视口内");
            }
            finally { shelf.ChoosingBasket = choosing; controller.Gallery.Clear(); shelf.Width = width; }
        }

        static async Task CheckScrollAndWind(StateStore store)
        {
            var shelf = controller.Shelf;
            double width = shelf.Width;
            var timer = typeof(ShelfWindow).GetField("nextGust", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var viewportField = typeof(ShelfWindow).GetField("viewport", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var viewport = (ScrollViewer)viewportField.GetValue(shelf);
            var randomField = typeof(ShelfWindow).GetField("random", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var originalRandom = (Random)randomField.GetValue(shelf);
            try
            {
                controller.Gallery.Clear();
                Directory.CreateDirectory(store.Inbox);
                for (int i = 0; i < 10; i++)
                {
                    string path = Path.Combine(store.Inbox, "滚动验证_" + i + ".png");
                    byte[] bytes = Asset("工作流程", "截图 · 标注 · 收藏", Color.FromRgb((byte)(61 + i * 6), (byte)(93 + i * 4), 160));
                    File.WriteAllBytes(path, bytes);
                    controller.Gallery.Add(path, ImageContent.Decode(bytes));
                }
                shelf.Width = 840;
                SetCursorPos(300, 450);
                WindowsNative.ActivateEditor(new System.Windows.Interop.WindowInteropHelper(testContext).Handle);
                shelf.Reveal(true, true, 60);
                timer.SetValue(shelf, DateTime.UtcNow.AddMinutes(1));
                await Task.Delay(4900);
                var card = shelf.CardFor(controller.Gallery.Items[2].Path);
                var rotation = (RotateTransform)card.RenderTransform;
                double start = viewport.HorizontalOffset;
                await WheelOnCard(card, -120);
                await Until(() => viewport.HorizontalOffset > start + 30 && rotation.Angle - card.Tilt < -0.2);
                Check(viewport.HorizontalOffset > start + 30, "实际滚轮可横向移动图片列表");
                Check(rotation.Angle - card.Tilt < -0.2, "向左滚动时图片向相反方向轻摆，呈现惯性");
                Check(((Grid)shelf.Content).Children.Contains(viewport), "滚动图片区和固定衣篓入口同时保留");
                await Task.Delay(900);
                double reverseStart = viewport.HorizontalOffset;
                await WheelOnCard(card, 120);
                await Until(() => viewport.HorizontalOffset < reverseStart - 30 && rotation.Angle - card.Tilt > 0.2);
                Check(rotation.Angle - card.Tilt > 0.2, "反向滚动时轻摆方向随之改变");
                timer.SetValue(shelf, DateTime.UtcNow.AddMinutes(1));
                SetCursorPos(shelf.Display.WorkingArea.Left + 10, shelf.Display.WorkingArea.Top + 100);
                await Task.Delay(2800);
                Check(shelf.Revealed, "等待滚动回稳时轨道保持展开");
                Check(Math.Abs(rotation.Angle - card.Tilt) < 0.2, "停止滚动后图片逐渐恢复原来的悬挂角度");
                await WheelOnCard(card, 120);
                await Task.Delay(200);
                Check(viewport.HorizontalOffset < 1 && Math.Abs(rotation.Angle - card.Tilt) < 0.2, "列表已到边界时不会凭空触发滚动摆动");
                await WheelOnCard(card, -120);
                await Until(() => viewport.HorizontalOffset > 20);
                DateTime scrollingDue = DateTime.UtcNow;
                timer.SetValue(shelf, scrollingDue);
                await Task.Delay(60);
                Check((DateTime)timer.GetValue(shelf) == scrollingDue, "滚动期间暂缓已到期的微风");
                await Until(() => (DateTime)timer.GetValue(shelf) > scrollingDue);
                Check((DateTime)timer.GetValue(shelf) > scrollingDue, "滚动结束后恢复微风触发");
                randomField.SetValue(shelf, new Random(1234));
                var angleField = typeof(ShotCard).GetField("breezeAngle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var swingField = typeof(ShotCard).GetField("swing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var delays = new System.Collections.Generic.List<double>();
                bool left = false, right = false, matchingDirection = true;
                for (int i = 0; i < 16; i++)
                {
                    foreach (var item in controller.Gallery.Items)
                    {
                        shelf.CardFor(item.Path).Breeze(-1, 0);
                        ((SpringMotion)swingField.GetValue(shelf.CardFor(item.Path))).Snap(0);
                    }
                    // 清理上一阵风尚未触发的延迟，并等待画面同步到复位状态。
                    await Task.Delay(50);
                    DateTime due = DateTime.UtcNow;
                    timer.SetValue(shelf, due);
                    await Until(() => (DateTime)timer.GetValue(shelf) > due);
                    double delay = ((DateTime)timer.GetValue(shelf) - DateTime.UtcNow).TotalSeconds;
                    delays.Add(delay);
                    int direction = Math.Sign((double)angleField.GetValue(card));
                    foreach (var item in controller.Gallery.Items)
                        matchingDirection &= Math.Sign((double)angleField.GetValue(shelf.CardFor(item.Path))) == direction;
                    await Until(() => Math.Abs(rotation.Angle - card.Tilt) > 0.05);
                    matchingDirection &= Math.Sign(rotation.Angle - card.Tilt) == direction;
                    left |= direction < 0;
                    right |= direction > 0;
                }
                Check(delays.All(delay => delay > 6.8 && delay <= 24), "每阵风的间隔随机落在 7～24 秒之间");
                Check(delays.Max() - delays.Min() > 8, "吹动间隔有明显变化，避免接近固定周期");
                Check(left && right && matchingDirection, "随机风能向左右两侧吹动，同一阵风的图片方向一致");
                timer.SetValue(shelf, DateTime.UtcNow.AddMinutes(1));
                await Task.Delay(3200);
                Check(Math.Abs(rotation.Angle - card.Tilt) < 0.2, "风吹结束后图片逐渐回稳");
                shelf.Reveal(false, false, 0);
            }
            finally { randomField.SetValue(shelf, originalRandom); shelf.Width = width; }
        }

        static async Task WheelOnCard(ShotCard card, int delta)
        {
            Point center = card.Surface.PointToScreen(new Point(card.Surface.ActualWidth / 2, card.Surface.ActualHeight / 2));
            SetCursorPos((int)center.X, (int)center.Y);
            await Until(() => WindowFromPoint(WindowsNative.Cursor()) == controller.Shelf.Handle);
            SetCursorPos((int)center.X + 1, (int)center.Y);
            await Task.Delay(120);
            mouse_event(0x0800, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
        }

        static async Task CheckVisibility()
        {
            WindowsNative.Rectangle bounds;
            WindowsNative.GetWindowRect(controller.Shelf.Handle, out bounds);
            controller.Shelf.Reveal(true, false, 0);
            SetCursorPos(bounds.Left + 400, bounds.Top + 110);
            await Task.Delay(150);
            SetCursorPos(bounds.Left + 400, bounds.Bottom + 200);
            await Task.Delay(900);
            Check(controller.Shelf.Revealed, "鼠标移开 0.9 秒后仍保持展开，留出操作时间");
            SetCursorPos(bounds.Left + 400, bounds.Top + 110);
            await Task.Delay(150);
            SetCursorPos(bounds.Left + 400, bounds.Bottom + 200);
            await Task.Delay(1000);
            Check(controller.Shelf.Revealed, "鼠标返回绳子后重新计算收起等待时间");
            await Until(() => !controller.Shelf.Revealed);
            Check(!controller.Shelf.Revealed, "鼠标移开后自动收起");
            await Until(() => !controller.Shelf.Animating);
            Check(!controller.Shelf.Animating && !controller.Shelf.MotionWindows.Any(), "收起后停止逐帧动画并清理临时窗口");
            SetCursorPos(bounds.Left + 400, controller.Shelf.Display.Bounds.Top + 1);
            await Until(() => controller.Shelf.Revealed);
            Check(controller.Shelf.Revealed, "鼠标停在屏幕上沿可再次展开");
            await CheckViewerClose();
        }

        static async Task CheckBasketMotion(StateStore store)
        {
            var shelf = controller.Shelf;
            shelf.ChoosingBasket = true; shelf.Width = 1000; shelf.Reveal(true, true, 60);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var image = (Image)typeof(ShelfWindow).GetField("basketImage", flags).GetValue(shelf);
            Directory.CreateDirectory(store.Inbox);
            string path = Path.Combine(store.Inbox, "投篮动效.png");
            byte[] bytes = Asset("工作参考", "取下放篮子", Color.FromRgb(41, 87, 141)); File.WriteAllBytes(path, bytes);
            var picture = ImageContent.Decode(bytes);
            foreach (var theme in LineTheme.All)
            {
                shelf.ApplyTheme(theme); controller.Gallery.Add(path, picture);
                await Task.Delay(450);
                var item = controller.Gallery.Items.Single();
                Rect origin = shelf.CardFor(path).PhotoRect();
                controller.Act(ShotAction.Remove, item);
                var flight = shelf.MotionWindows.Single();
                Check(!flight.Falling, theme.Name + "取下图片使用投篮轨迹，不再向下掉落");
                Check(!flight.ShowActivated && !flight.IsHitTestVisible, "投篮窗口不抢焦点、不遮挡鼠标操作");
                var frame = (Canvas)typeof(ShotFlight).GetField("frame", flags).GetValue(flight);
                await Task.Delay(220);
                Point center = flight.PointToScreen(new Point(Canvas.GetLeft(frame) + frame.Width / 2, Canvas.GetTop(frame) + frame.Height / 2));
                Point basket = image.PointToScreen(new Point(image.ActualWidth / 2, image.ActualHeight * 0.6));
                Check(frame.Width < origin.Width / WindowsNative.MonitorScale(shelf.Display)
                    && Math.Abs(center.X - basket.X) < Math.Abs(origin.X + origin.Width / 2 - basket.X), "飞行时缩小并向当前篮口靠近");
                var clip = (ClothesClip)typeof(ShotFlight).GetField("clip", flags).GetValue(flight);
                Check(clip.Opacity == 0, "投放图片不携带夹子");
                await Until(() => !shelf.MotionWindows.Any());
                var motion = (SpringMotion)typeof(ShelfWindow).GetField("basketMotion", flags).GetValue(shelf);
                Check(Math.Abs(motion.Velocity) + Math.Abs(motion.Position) > 0.1, "接住图片后篮子有回弹反馈");
                Check(File.Exists(path) && controller.Gallery.Collected.Contains(item) && !controller.Gallery.Contains(path), "投篮只改变挂图状态，原图和收集记录保持完整");
                await Task.Delay(1000);
                Check(Math.Abs(motion.Position) < 0.05, "接收回弹结束后恢复静止");
            }
            Point anchor = image.PointToScreen(PictureBasket.Hook(shelf.Appearance));
            shelf.BasketButton.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
            await Task.Delay(160);
            var turn = (RotateTransform)image.RenderTransform;
            Check(Math.Abs(turn.Angle) > 0.1 && (image.PointToScreen(PictureBasket.Hook(shelf.Appearance)) - anchor).Length < 0.3,
                "悬停时围绕挂钩轻摆，挂钩不脱离绳子");
            shelf.BasketButton.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
            await Task.Delay(700);
            controller.Gallery.Add(path, picture); await Task.Delay(200);
            controller.Act(ShotAction.Trash, controller.Gallery.Items.Single());
            Check(shelf.MotionWindows.Single().Falling, "移到回收站继续使用下落删除动画");
            var basketMotion = (SpringMotion)typeof(ShelfWindow).GetField("basketMotion", flags).GetValue(shelf);
            Check(Math.Abs(basketMotion.Position) + Math.Abs(basketMotion.Velocity) < 0.05, "删除图片不会误触发篮子的接收反馈");
            await Until(() => !shelf.MotionWindows.Any());
            File.WriteAllBytes(path, bytes); controller.Gallery.Add(path, picture); await Task.Delay(250);
            shelf.CardFor(path).Grip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => shelf.PinWindows.Any());
            await Until(() => shelf.PinWindows.Single().IsHitTestVisible);
            shelf.PinWindows.Single().PinButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !shelf.PinWindows.Any() && shelf.MotionWindows.Any());
            Check(shelf.MotionWindows.All(flight => !flight.Falling && !flight.Collecting), "桌面贴图挂回时仍使用原来的挂绳动画");
            await Until(() => !shelf.MotionWindows.Any());
            controller.Act(ShotAction.Remove, controller.Gallery.Items.Single());
            shelf.Reveal(false, false, 0); await Task.Delay(350);
            Check(!shelf.MotionWindows.Any() && Math.Abs(turn.Angle) < 0.01 && !shelf.Animating, "投篮中收起轨道会取消临时窗口并复位篮子动效");
            shelf.ApplyTheme(LineTheme.All[0]); shelf.Reveal(true, true, 60);
            controller.Gallery.Add(path, picture);
            for (int index = 0; index < 2; index++)
            {
                string other = Path.Combine(store.Inbox, "参考_" + index + ".png");
                byte[] otherBytes = Asset(index == 0 ? "临时笔记" : "界面参考", "截图 · 留在手边", index == 0 ? Color.FromRgb(188, 111, 76) : Color.FromRgb(54, 87, 151));
                File.WriteAllBytes(other, otherBytes); controller.Gallery.Add(other, ImageContent.Decode(otherBytes));
            }
            await Task.Delay(650);
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "basket-motion-frames"); Directory.CreateDirectory(directory);
            for (int index = 0; index < 42; index++)
            {
                if (index == 5) controller.Act(ShotAction.Remove, controller.Gallery.Items.Single(item => item.Path == path));
                var drawing = new DrawingVisual();
                using (var painter = drawing.RenderOpen())
                {
                    var area = new Rect(0, 0, 1000, 220);
                    painter.DrawRectangle(LightTheme.Brush("#071A30"), null, area);
                    painter.DrawRectangle(new VisualBrush(shelf), null, area);
                    foreach (var flight in shelf.MotionWindows)
                    {
                        Point offset = shelf.PointFromScreen(flight.PointToScreen(new Point()));
                        painter.DrawRectangle(new VisualBrush(flight) { ViewboxUnits = BrushMappingMode.Absolute,
                            Viewbox = new Rect(-offset.X, -offset.Y, 1000, 220) }, null, area);
                    }
                }
                var bitmap = new RenderTargetBitmap(1000, 220, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(Path.Combine(directory, index.ToString("D3") + ".png"))) encoder.Save(output);
                await Task.Delay(40);
            }
        }

        static async Task CheckDiscard(StateStore store)
        {
            controller.Gallery.Clear();
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            Check(taskbar != IntPtr.Zero, "任务栏存在，可验证删除动效不会使其让位");
            WindowsNative.Rectangle barBounds;
            WindowsNative.GetWindowRect(taskbar, out barBounds);
            var barPoint = new WindowsNative.Point { X = barBounds.Left + (barBounds.Right - barBounds.Left) / 4, Y = barBounds.Top + (barBounds.Bottom - barBounds.Top) / 2 };
            foreach (var action in new[] { ShotAction.Remove, ShotAction.Trash })
            {
                string path = Path.Combine(action == ShotAction.Remove ? store.Inbox : store.Root, action + ".png");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                byte[] bytes = Asset("删除验证", "取下图片 · 保留动效", Color.FromRgb(61, 93, 181));
                File.WriteAllBytes(path, bytes);
                controller.Gallery.Add(path, ImageContent.Decode(bytes));
                var item = controller.Gallery.Items.Single();
                controller.Shelf.Reveal(true, false, 3);
                testContext.Activate();
                await Task.Delay(500);
                IntPtr focus = WindowsNative.GetForegroundWindow();
                IntPtr beforeHit = GetAncestor(WindowFromPoint(barPoint), 2);
                controller.Act(action, item);
                var flight = controller.Shelf.MotionWindows.Single();
                Check(flight.Falling == (action == ShotAction.Trash), "放篮子与移到回收站使用不同动画");
                IntPtr handle = new System.Windows.Interop.WindowInteropHelper(flight).Handle;
                bool taskbarStable = true;
                for (int i = 0; i < 4; i++)
                {
                    await Task.Delay(60);
                    taskbarStable &= GetAncestor(WindowFromPoint(barPoint), 2) == beforeHit;
                }
                Check(taskbarStable, "删除动效期间任务栏的可见与点击层级保持不变");
                Check(GetProp(handle, "NonRudeHWND") == new IntPtr(1), "透明动画窗口明确声明为非全屏，不触发任务栏让位");
                Check(WindowsNative.GetForegroundWindow() == focus, "删除动效不会抢走前台窗口焦点");
                Check(File.Exists(path) == (action == ShotAction.Remove) && controller.Gallery.Items.Count == 0, "取下保留原图，明确删除时移入回收站");
                await Until(() => !controller.Shelf.MotionWindows.Any());
                Check(!controller.Shelf.MotionWindows.Any(), "取下或删除动效结束后清理临时窗口");
            }
        }

        static async Task CheckViewerClose()
        {
            var viewer = new Window { Title = "轨道查看验证.png", Width = 500, Height = 300, Left = 100, Top = 300, ShowInTaskbar = false };
            try
            {
                controller.Shelf.Reveal(true, false, 0);
                controller.Shelf.FollowImageViewer(viewer.Title);
                viewer.Show(); viewer.Activate();
                WindowsNative.ActivateEditor(new System.Windows.Interop.WindowInteropHelper(viewer).Handle);
                await Until(() => WindowsNative.GetForegroundWindow() == new System.Windows.Interop.WindowInteropHelper(viewer).Handle);
                SetCursorPos(350, 450);
                await Task.Delay(2300);
                Check(controller.Shelf.Revealed, "查看图片时不会沿用鼠标离开的计时收起轨道");
                viewer.Close(); testContext.Activate();
                await Task.Delay(200);
                Check(controller.Shelf.Revealed, "关闭图片查看窗口后轨道保持展开");
                await Task.Delay(900);
                Check(controller.Shelf.Revealed, "查看结束后重新留出收起等待时间");
                await Until(() => !controller.Shelf.Revealed);
                Check(!controller.Shelf.Revealed, "查看结束后鼠标未返回时仍会正常自动收起");

                viewer = new Window { Title = "全屏查看验证.png", WindowStyle = WindowStyle.None, ShowInTaskbar = false };
                controller.Shelf.Reveal(true, false, 0);
                controller.Shelf.FollowImageViewer(viewer.Title);
                viewer.Show(); viewer.Activate();
                var bounds = controller.Shelf.Display.Bounds;
                WindowsNative.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(viewer).Handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0044);
                WindowsNative.ActivateEditor(new System.Windows.Interop.WindowInteropHelper(viewer).Handle);
                await Until(() => WindowsNative.GetForegroundWindow() == new System.Windows.Interop.WindowInteropHelper(viewer).Handle && !controller.Shelf.Revealed);
                Check(!controller.Shelf.Revealed, "全屏查看图片时仍会暂时收起轨道");
                viewer.Close(); testContext.Activate();
                await Task.Delay(200);
                Check(controller.Shelf.Revealed, "关闭全屏图片查看后自动恢复轨道");

                viewer = new Window { Title = "手动收起验证.png", Width = 500, Height = 300, Left = 100, Top = 300, ShowInTaskbar = false };
                controller.Shelf.FollowImageViewer(viewer.Title);
                viewer.Show(); viewer.Activate();
                WindowsNative.ActivateEditor(new System.Windows.Interop.WindowInteropHelper(viewer).Handle);
                await Until(() => WindowsNative.GetForegroundWindow() == new System.Windows.Interop.WindowInteropHelper(viewer).Handle);
                await Task.Delay(150);
                controller.Shelf.Toggle();
                viewer.Close(); testContext.Activate();
                await Task.Delay(200);
                Check(!controller.Shelf.Revealed, "查看时手动收起轨道，关闭图片后不会擅自展开");
            }
            finally { if (viewer.IsVisible) viewer.Close(); }
        }

        static async Task CheckThirdPartyFormats(byte[] second, byte[] third, StateStore store)
        {
            await SetClipboardImage(second, "image/png");
            await Until(() => controller.Gallery.Items.Count == 1);
            Check(controller.Gallery.Items[0].Image.Fingerprint == ImageContent.Decode(second).Fingerprint, "仅 MIME PNG 的截图可自动收集且像素完整");

            var decoder = BitmapDecoder.Create(new MemoryStream(third), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var encoder = new BmpBitmapEncoder();
            encoder.Frames.Add(decoder.Frames[0]);
            byte[] dib;
            using (var memory = new MemoryStream())
            {
                encoder.Save(memory);
                // Windows 剪贴板中的 DIB 不含 BMP 文件头的前 14 字节。
                byte[] bmp = memory.ToArray();
                dib = new byte[bmp.Length - 14];
                Buffer.BlockCopy(bmp, 14, dib, 0, dib.Length);
            }
            await WindowsClipboard.Write(new DataObject(DataFormats.Dib, new MemoryStream(dib)));
            lastTestSequence = WindowsNative.GetClipboardSequenceNumber();
            await Until(() => controller.Gallery.Items.Count == 2);
            Check(controller.Gallery.Items[1].Image.Fingerprint == ImageContent.Decode(third).Fingerprint, "仅 DIB 的截图可自动收集且像素完整");
            Check(!File.Exists(Path.Combine(store.Root, "error.log")), "第三方截图格式读取没有运行错误日志");
        }

        static async Task SetClipboardImage(byte[] bytes, string format = "PNG")
        {
            await WindowsClipboard.Write(new DataObject(format, new MemoryStream(bytes)));
            lastTestSequence = WindowsNative.GetClipboardSequenceNumber();
        }

        static async Task<string> DragTo(string source, string destination, bool move)
        {
            Directory.CreateDirectory(destination);
            var target = new Window
            {
                Title = "拖拽验证", Width = 440, Height = 190, Left = 100, Top = 390,
                ShowActivated = false, ShowInTaskbar = false, Topmost = true, AllowDrop = true,
                Background = Brushes.White, Content = new System.Windows.Controls.TextBlock { Text = "拖放区域", Margin = new Thickness(30), FontSize = 24 }
            };
            bool dropped = false;
            target.DragOver += delegate(object sender, DragEventArgs args)
            {
                args.Effects = args.Data.GetDataPresent(DataFormats.FileDrop) ? (move ? DragDropEffects.Move : DragDropEffects.Copy) : DragDropEffects.None;
                args.Handled = true;
            };
            target.Drop += delegate(object sender, DragEventArgs args)
            {
                string[] files = args.Data.GetData(DataFormats.FileDrop) as string[];
                if (move) File.Move(files[0], Path.Combine(destination, Path.GetFileName(files[0])));
                else File.Copy(files[0], Path.Combine(destination, Path.GetFileName(files[0])));
                args.Effects = move ? DragDropEffects.Move : DragDropEffects.Copy;
                args.Handled = true;
                dropped = true;
            };
            target.Show();
            controller.Shelf.Reveal(true, true, 0);
            await Task.Delay(400);
            var card = controller.Shelf.CardFor(source).Surface;
            Point from = card.PointToScreen(new Point(card.ActualWidth / 2, card.ActualHeight / 2));
            Point to = target.PointToScreen(new Point(220, 110));
            SetCursorPos((int)from.X, (int)from.Y);
            await Until(() => WindowFromPoint(WindowsNative.Cursor()) == controller.Shelf.Handle);
            try
            {
                await Task.Run(delegate
                {
                    mouse_event(2, 0, 0, 0, UIntPtr.Zero);
                    System.Threading.Thread.Sleep(40);
                    for (int step = 1; step <= 8; step++)
                    {
                        SetCursorPos((int)(from.X + (to.X - from.X) * step / 8), (int)(from.Y + (to.Y - from.Y) * step / 8));
                        System.Threading.Thread.Sleep(45);
                    }
                    mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                });
                await Until(() => dropped);
                return Path.Combine(destination, Path.GetFileName(source));
            }
            finally { target.Close(); }
        }

        static byte[] Asset(string title, string subtitle, Color color)
        {
            var drawing = new DrawingVisual();
            using (var painter = drawing.RenderOpen())
            {
                painter.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, 360, 240));
                painter.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), null, new Rect(22, 100, 316, 116), 14, 14);
                painter.DrawText(new FormattedText(title, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 27, Brushes.White, 1), new Point(26, 28));
                painter.DrawText(new FormattedText(subtitle, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 16, Brushes.White, 1), new Point(26, 70));
                for (int i = 0; i < 3; i++) painter.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), null, new Rect(40 + i * 100, 126 + i * 12, 76, 36), 7, 7);
            }
            var bitmap = new RenderTargetBitmap(360, 240, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            return ImageContent.EncodeClipboard(bitmap);
        }

        static void ExportPreview(ShelfWindow shelf)
        {
            var drawing = new DrawingVisual();
            using (var painter = drawing.RenderOpen())
            {
                painter.DrawRectangle(new SolidColorBrush(Color.FromRgb(238, 242, 248)), null, new Rect(0, 0, shelf.ActualWidth, shelf.ActualHeight));
                painter.DrawRectangle(new VisualBrush(shelf), null, new Rect(0, 0, shelf.ActualWidth, shelf.ActualHeight));
            }
            var bitmap = new RenderTargetBitmap((int)shelf.ActualWidth, (int)shelf.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview.png");
            using (var file = File.Create(output)) encoder.Save(file);
            Console.WriteLine("界面预览：" + output);
        }

        static async Task CheckEditor(StateStore store)
        {
            Directory.CreateDirectory(store.Inbox);
            string path = Path.Combine(store.Inbox, "项目文件.png");
            byte[] source = EditorAsset();
            File.WriteAllBytes(path, source);
            controller.Gallery.Add(path, ImageContent.Decode(source));
            var item = controller.Gallery.Items[0];
            string original = item.Image.Fingerprint;
            controller.Shelf.Reveal(true, true, 0);
            await Task.Delay(650);
            var card = controller.Shelf.CardFor(path);
            Point center = card.Surface.PointToScreen(new Point(card.Surface.ActualWidth / 2, card.Surface.ActualHeight / 2));
            SetCursorPos((int)center.X, (int)center.Y);
            await Task.Delay(120);
            card.Surface.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonUpEvent });
            await Until(() => card.ActionMenu.IsOpen);
            testContext.Topmost = true;
            testContext.Activate();
            card.ActionMenu.Actions[ShotAction.Edit].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            card.ActionMenu.IsOpen = false;
            await Until(() => controller.Editors.Count == 1);
            var window = controller.Editors[0];
            if (await Task.WhenAny(window.Ready, Task.Delay(20000)) != window.Ready) throw new Exception("内置编辑器加载超时");
            await window.Ready;
            Check(window.Topmost && window.IsActive && WindowsNative.GetForegroundWindow() == new System.Windows.Interop.WindowInteropHelper(window).Handle, "编辑器打开时置于前台，不被其他置顶窗口遮挡");
            testContext.Topmost = false;
            Check(window.Browser.CoreWebView2.Source.StartsWith("https://screenline-editor.invalid/"), "图片右键编辑入口打开本地内置 Filerobot 窗口");
            Check((await window.Browser.ExecuteScriptAsync("document.querySelector('#save').disabled")) == "false", "真实截图加载后保存按钮可用");
            Check(await window.Browser.ExecuteScriptAsync("document.getElementById('save').textContent==='完成'&&getComputedStyle(document.querySelector('.FIE_tabs')).display==='none'") == "true", "桌面窗口使用单行工具栏和完成按钮");
            var image = new JavaScriptSerializer().Deserialize<int[]>(await window.Browser.ExecuteScriptAsync("(()=>{const d=editor.getCurrentImgData().imageData;return[d.width,d.height]})()"));
            Check(image[0] == 960 && image[1] == 600, "编辑器加载原图，未误用晾衣绳缩略图");
            controller.Act(ShotAction.Edit, item);
            Check(controller.Editors.Count == 1, "同一张图片重复编辑会激活已有窗口");
            await window.Browser.ExecuteScriptAsync("chrome.webview.postMessage({action:'save',data:'data:image/png;base64,iVBORw0KGgo='});");
            await Task.Delay(400);
            Check(File.ReadAllBytes(path).SequenceEqual(source), "损坏的网页编辑结果不会覆盖原图");
            await DrawEditorArrow(window);
            string count = await window.Browser.ExecuteScriptAsync("Object.keys(editor.getCurrentImgData().designState.annotations).length");
            Check(count == "1", "编辑画布拖动可添加箭头标注");
            Check(await window.Browser.ExecuteScriptAsync("Object.values(editor.getCurrentImgData().designState.annotations)[0].stroke==='#d64c46'") == "true", "桌面标注默认使用红色");
            await EnterEditorText(window);
            Check(await window.Browser.ExecuteScriptAsync("document.activeElement.matches('textarea[aria-label=\"输入文字\"]')&&document.activeElement.value==='单击输入中文'") == "true", "桌面编辑器单击画布后直接输入中文");
            Check(await window.Browser.ExecuteScriptAsync("document.querySelectorAll('[data-size]').length===3&&!document.querySelector('#textFont')") == "true", "桌面文字工具只提供三档字号和固定颜色");
            string previewDirectory = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "previews", "图片编辑"));
            Directory.CreateDirectory(previewDirectory);
            controller.Captures.CollectClipboard = true;
            await ClickEditor(window, "#copy");
            await Until(() => Clipboard.GetDataObject() != null && Clipboard.GetDataObject().GetDataPresent("PNG"));
            lastTestSequence = WindowsNative.GetClipboardSequenceNumber();
            await Task.Delay(500);
            Check(Clipboard.ContainsImage() && Clipboard.GetImage().PixelWidth == 960, "编辑器复制的是完整分辨率标注结果");
            Check(await window.Browser.ExecuteScriptAsync("Object.values(editor.getCurrentImgData().designState.annotations).some(item=>item.name==='Text'&&item.text==='单击输入中文'&&item.fontFamily==='Microsoft YaHei')") == "true", "输入中的中文在复制前提交，保留中文字体");
            Check(controller.Gallery.Items.Count == 1 && File.ReadAllBytes(path).SequenceEqual(source), "复制不会覆盖原图或被剪贴板收集再次挂上");
            var textPoint = new JavaScriptSerializer().Deserialize<double[]>(await window.Browser.ExecuteScriptAsync("(()=>{const item=Object.values(editor.getCurrentImgData().designState.annotations).find(item=>item.name==='Text'),node=canvasStage.findOne('#'+item.id),r=canvasStage.container().getBoundingClientRect(),p=node.getAbsoluteTransform().point({x:10,y:10});return[r.x+p.x,r.y+p.y]})()"));
            await window.Browser.ExecuteScriptAsync("window.originalUpdateState=editor.updateState;editor.updateState=payload=>typeof payload==='function'?setTimeout(()=>originalUpdateState(payload),80):originalUpdateState(payload);");
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await window.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", new JavaScriptSerializer().Serialize(new { type = "mousePressed", x = textPoint[0], y = textPoint[1], button = "left", buttons = 1, clickCount = 1 }));
                await window.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", new JavaScriptSerializer().Serialize(new { type = "mouseReleased", x = textPoint[0], y = textPoint[1], button = "left", buttons = 0, clickCount = 1 }));
            }
            await Task.Delay(250);
            await window.Browser.ExecuteScriptAsync("editor.updateState=originalUpdateState;");
            Check(await window.Browser.ExecuteScriptAsync("document.querySelectorAll('.text-input').length===1&&document.activeElement.classList.contains('text-input')") == "true", "桌面编辑器快速点回已有文字时保持唯一输入框和键盘焦点");
            await window.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", new JavaScriptSerializer().Serialize(new { text = "继续输入" }));
            Check(await window.Browser.ExecuteScriptAsync("document.activeElement.value.includes('继续输入')") == "true", "桌面重新编辑已有文字后可以继续输入中文");
            await window.Browser.ExecuteScriptAsync("finishText(true);");
            await Task.Delay(100);
            var resizePoint = new JavaScriptSerializer().Deserialize<double[]>(await window.Browser.ExecuteScriptAsync("(()=>{const h=document.querySelector('[data-text-corner=se]').getBoundingClientRect(),r=document.getElementById('textBounds').getBoundingClientRect();return[h.x+h.width/2,h.y+h.height/2,r.width*.3,r.height*.3]})()"));
            await window.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", new JavaScriptSerializer().Serialize(new { type = "mousePressed", x = resizePoint[0], y = resizePoint[1], button = "left", buttons = 1, clickCount = 1 }));
            for (int step = 1; step <= 8; step++)
                await window.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", new JavaScriptSerializer().Serialize(new { type = "mouseMoved", x = resizePoint[0] + resizePoint[2] * step / 8, y = resizePoint[1] + resizePoint[3] * step / 8, button = "left", buttons = 1 }));
            await window.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", new JavaScriptSerializer().Serialize(new { type = "mouseReleased", x = resizePoint[0] + resizePoint[2], y = resizePoint[1] + resizePoint[3], button = "left", buttons = 0, clickCount = 1 }));
            await Task.Delay(150);
            Check(await window.Browser.ExecuteScriptAsync("Object.values(editor.getCurrentImgData().designState.annotations).some(item=>item.name==='Text'&&item.fontSize>28&&item.fill==='#d64c46'&&item.text==='单击输入中文')") == "true", "桌面窗口拖动文本框后中文字号同步放大，保留红色和完整文字");
            await ClickEditor(window, "#saveOriginal");
            await Until(() => item.Image.Fingerprint != original);
            Check(ImageContent.Read(path).Fingerprint == item.Image.Fingerprint && card.Item.Image == item.Image, "保存回原图后立即刷新晾衣绳缩略图");
            Check(!controller.Shelf.Revealed && !controller.Shelf.MotionWindows.Any(), "继续编辑时保存不触发挂回动画");
            await Task.Delay(200);
            using (var output = File.Create(Path.Combine(previewDirectory, "内置编辑器.png")))
                await window.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
            byte[] originalSaved = File.ReadAllBytes(path);
            string copyPath = Path.Combine(store.Inbox, "项目文件_副本.png");
            IntPtr editorHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var pickerTask = Task.Run(async delegate
            {
                var dialog = AutomationElement.FromHandle(editorHandle);
                AutomationElement filename = null;
                var filenameCondition = new AndCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "1001"), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                for (int attempt = 0; attempt < 40 && filename == null; attempt++)
                {
                    await Task.Delay(100);
                    filename = dialog.FindFirst(TreeScope.Descendants, filenameCondition);
                }
                if (filename == null) throw new Exception("未找到另存为文件名输入框");
                ((ValuePattern)filename.GetCurrentPattern(ValuePattern.Pattern)).SetValue(copyPath);
                filename.SetFocus();
                Forms.SendKeys.SendWait("{END} {BACKSPACE}{TAB}");
                var save = dialog.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "1"), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
                ((InvokePattern)save.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            });
            await ClickEditor(window, "#saveAs");
            await pickerTask;
            await Until(() => controller.Gallery.Items.Count == 2 && window.ImagePath == copyPath);
            Check(File.Exists(copyPath) && File.ReadAllBytes(path).SequenceEqual(originalSaved), "实际另存为创建副本并挂上，原图保持已保存内容");
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            await Task.Delay(300);
            Check(await window.Browser.ExecuteScriptAsync("document.documentElement.scrollWidth<=innerWidth") == "true", "缩小编辑窗口后布局没有横向溢出");
            window.Close();
            await Until(() => controller.Editors.Count == 0);
            Check(controller.Shelf.Revealed && controller.Shelf.MotionWindows.Any(flight => !flight.Falling), "关闭编辑器后图片向上飞回展开的绳子");
            await Until(() => !controller.Shelf.MotionWindows.Any());
            Check(!controller.Shelf.CardFor(copyPath).Flying && controller.Shelf.CardFor(copyPath).Opacity > 0.9, "另存后挂回新图片，落稳后显示缩略图");
            Check(File.Exists(copyPath), "关闭已保存编辑器可释放窗口并保留图片");
            controller.Shelf.Reveal(true, true, 0);
            await Task.Delay(850);
            center = card.Surface.PointToScreen(new Point(card.Surface.ActualWidth / 2, card.Surface.ActualHeight / 2));
            SetCursorPos((int)center.X, (int)center.Y);
            await Task.Delay(150);
            card.Surface.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            await Until(() => controller.Editors.Count == 1);
            card.Surface.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            window = controller.Editors[0];
            await window.Ready;
            Check(window.ImagePath == path, "长按计时结束后进入同一内置编辑器");
            Check(System.Windows.Input.Mouse.Captured == null, "长按打开编辑器前释放卡片的鼠标捕获");
            Check(window.IsActive && WindowsNative.GetForegroundWindow() == new System.Windows.Interop.WindowInteropHelper(window).Handle, "长按后编辑窗口置于前台，不被原窗口遮挡");
            testContext.Activate();
            await Task.Delay(100);
            Check(!window.Topmost, "转到其他窗口后编辑器解除置顶");
            window.Activate();
            await DrawEditorArrow(window);
            await EnterEditorText(window);
            await Task.Delay(150);
            var cancel = AnswerEditorClose(2);
            window.Close();
            await cancel;
            Check(window.IsVisible, "未保存时关闭会询问，取消后保留编辑内容");
            var accept = AnswerEditorClose(6);
            window.Close();
            await accept;
            await Until(() => controller.Editors.Count == 0);
            Check(!File.ReadAllBytes(path).SequenceEqual(originalSaved), "关闭时选择保存可先回写图片再释放窗口");
            controller.Act(ShotAction.Edit, controller.Gallery.Items.Single(entry => entry.Path == copyPath));
            await Until(() => controller.Editors.Count == 1);
            window = controller.Editors[0];
            await window.Ready;
            string beforeFinish = ImageContent.Read(copyPath).Fingerprint;
            await DrawEditorArrow(window);
            var discard = AnswerEditorClose(7);
            window.Close();
            await discard;
            await Until(() => controller.Editors.Count == 0);
            Check(ImageContent.Read(copyPath).Fingerprint == beforeFinish, "选择不保存可关闭窗口并保留原文件");
            controller.Act(ShotAction.Edit, controller.Gallery.Items.Single(entry => entry.Path == copyPath));
            await Until(() => controller.Editors.Count == 1);
            window = controller.Editors[0];
            await window.Ready;
            await DrawEditorArrow(window);
            var escape = AnswerEditorClose(27);
            window.Close();
            await escape;
            Check(window.IsVisible, "Escape 关闭保存确认并继续编辑");
            await ClickEditor(window, "#save");
            await Until(() => controller.Editors.Count == 0);
            Check(ImageContent.Read(copyPath).Fingerprint != beforeFinish, "完成按钮保存编辑结果并关闭窗口");
            Check(controller.Shelf.Revealed && controller.Shelf.MotionWindows.Count(flight => !flight.Falling) == 1
                && controller.Shelf.CardFor(copyPath).Flying, "完成后只播放一次挂回动画，落地前隐藏绳子上的图片");
            await RecordMotion(controller.Shelf);
            await Until(() => !controller.Shelf.MotionWindows.Any());
            Check(!controller.Shelf.CardFor(copyPath).Flying && controller.Shelf.CardFor(copyPath).Opacity > 0.9, "完成后编辑结果落稳在绳子上，飞行动画窗口正常释放");
            IntPtr foreground = WindowsNative.GetForegroundWindow();
            var notice = new LightNotice("无法读取这张图片，请换一张图片重试。");
            notice.Show();
            await Task.Delay(120);
            Check(WindowsNative.GetForegroundWindow() == foreground, "浅色提示卡片显示时不会抢走当前键盘焦点");
            CaptureWindow(notice, "操作提示.png");
            notice.Close();
            string invalidPath = Path.Combine(store.Inbox, "无法读取.png");
            File.WriteAllText(invalidPath, "invalid image");
            var failure = new ImageEditorWindow(invalidPath, store, pathValue => Task.FromResult(0), delegate { });
            failure.Show();
            try { await failure.Ready; } catch { }
            Check(failure.IsVisible && failure.Browser.Visibility == Visibility.Collapsed, "无法读取图片时显示统一错误卡片");
            CaptureWindow(failure, "编辑器错误.png");
            failure.CloseWithoutPrompt();
            controller.Captures.CollectClipboard = false;
            Console.WriteLine("内置编辑器预览：" + Path.Combine(previewDirectory, "内置编辑器.png"));
        }

        static async Task ClickEditor(ImageEditorWindow window, string selector)
        {
            await window.Browser.ExecuteScriptAsync("document.querySelector('" + selector + "').click()");
            await Task.Delay(200);
        }

        static Task AnswerEditorClose(int control)
        {
            return Task.Run(async delegate
            {
                int process = System.Diagnostics.Process.GetCurrentProcess().Id;
                string title = control == 6 ? "保存并关闭" : control == 7 ? "不保存" : "继续编辑";
                for (int attempt = 0; attempt < 40; attempt++)
                {
                    await Task.Delay(100);
                    var candidates = AutomationElement.RootElement.FindAll(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ProcessIdProperty, process));
                    AutomationElement dialog = null;
                    foreach (AutomationElement parent in candidates)
                    {
                        dialog = parent.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "SaveChangesDialog"));
                        if (dialog != null) break;
                    }
                    if (dialog == null) continue;
                    bool modal = ((WindowPattern)dialog.GetCurrentPattern(WindowPattern.Pattern)).Current.IsModal;
                    await Application.Current.Dispatcher.InvokeAsync(new Action(delegate
                    {
                        var prompt = Application.Current.Windows.OfType<SaveChangesDialog>().Single();
                        Check(prompt.Owner is ImageEditorWindow && modal, "保存确认使用编辑窗口所属的模态弹窗");
                        CaptureWindow(prompt, "保存确认.png");
                    }));
                    var button = dialog.FindFirst(TreeScope.Descendants, new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                        new PropertyCondition(AutomationElement.NameProperty, title)));
                    if (button == null) throw new Exception("未找到保存提醒按钮：" + title);
                    if (control == 27)
                    {
                        dialog.SetFocus();
                        Forms.SendKeys.SendWait("{ESC}");
                    }
                    else ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    return;
                }
                throw new Exception("未找到图片保存提醒的操作按钮");
            });
        }

        static void CaptureWindow(Window window, string name)
        {
            window.UpdateLayout();
            var surface = window.WindowStyle == WindowStyle.None ? (FrameworkElement)window : (FrameworkElement)window.Content;
            var image = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            string directory = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "previews", "弹窗"));
            Directory.CreateDirectory(directory);
            using (var output = File.Create(Path.Combine(directory, name))) encoder.Save(output);
        }

        static async Task DrawEditorArrow(ImageEditorWindow window)
        {
            await window.Browser.ExecuteScriptAsync("document.querySelector('button[data-tool=Arrow]').click()");
            await Task.Delay(80);
            var location = new JavaScriptSerializer().Deserialize<double[]>(await window.Browser.ExecuteScriptAsync("(()=>{const r=document.querySelector('.konvajs-content').getBoundingClientRect();return[r.x+r.width/2,r.y+r.height/2]})()"));
            Point start = new Point(location[0] - 110, location[1] - 45);
            Point end = new Point(location[0] + 95, location[1] + 40);
            var json = new JavaScriptSerializer();
            await window.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", json.Serialize(new { type = "mousePressed", x = start.X, y = start.Y, button = "left", buttons = 1, clickCount = 1 }));
            for (int step = 1; step <= 15; step++)
            {
                await window.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", json.Serialize(new { type = "mouseMoved", x = start.X + (end.X - start.X) * step / 15, y = start.Y + (end.Y - start.Y) * step / 15, button = "left", buttons = 1 }));
                await Task.Delay(12);
            }
            await window.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", json.Serialize(new { type = "mouseReleased", x = end.X, y = end.Y, button = "left", buttons = 0, clickCount = 1 }));
            await Task.Delay(200);
        }

        static async Task EnterEditorText(ImageEditorWindow window)
        {
            await ClickEditor(window, "button[data-tool=Text]");
            var point = new JavaScriptSerializer().Deserialize<double[]>(await window.Browser.ExecuteScriptAsync("(()=>{const r=document.querySelector('.konvajs-content').getBoundingClientRect();return[r.x+r.width*.24,r.y+r.height*.73]})()"));
            var json = new JavaScriptSerializer();
            await window.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", json.Serialize(new { type = "mousePressed", x = point[0], y = point[1], button = "left", buttons = 1, clickCount = 1 }));
            await window.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", json.Serialize(new { type = "mouseReleased", x = point[0], y = point[1], button = "left", buttons = 0, clickCount = 1 }));
            await Task.Delay(100);
            await window.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", json.Serialize(new { text = "单击输入中文" }));
            await Task.Delay(100);
        }

        static byte[] EditorAsset()
        {
            var drawing = new DrawingVisual();
            using (var painter = drawing.RenderOpen())
            {
                painter.DrawRectangle(new SolidColorBrush(Color.FromRgb(244, 247, 251)), null, new Rect(0, 0, 960, 600));
                painter.DrawRoundedRectangle(Brushes.White, null, new Rect(64, 54, 832, 492), 18, 18);
                painter.DrawText(new FormattedText("项目文件", CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 28, new SolidColorBrush(Color.FromRgb(40, 51, 67)), 1), new Point(108, 90));
                string[] titles = { "界面参考", "工作流程", "临时笔记" };
                for (int i = 0; i < titles.Length; i++)
                {
                    painter.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb((byte)(232 + i * 5), (byte)(240 - i * 2), (byte)(252 - i * 10))), null, new Rect(108 + i * 252, 166, 226, 310), 12, 12);
                    painter.DrawText(new FormattedText(titles[i], CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 22, Brushes.SlateGray, 1), new Point(128 + i * 252, 190));
                    for (int line = 0; line < 4; line++) painter.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(182, 198, 217)), null, new Rect(130 + i * 252, 254 + line * 34, 140 - line * 15, 10), 4, 4);
                }
            }
            var image = new RenderTargetBitmap(960, 600, 96, 96, PixelFormats.Pbgra32);
            image.Render(drawing);
            return ImageContent.EncodeClipboard(image);
        }

        static async Task CheckMenus(StateStore store)
        {
            string path = Path.Combine(store.Inbox, "菜单验证.png");
            Directory.CreateDirectory(store.Inbox);
            byte[] content = Asset("临时笔记", "截图收集 · 随手取用", Color.FromRgb(220, 112, 76));
            File.WriteAllBytes(path, content);
            controller.Gallery.Add(path, ImageContent.Decode(content));
            controller.Shelf.Reveal(true, false, 0);
            await Task.Delay(700);
            var card = controller.Shelf.CardFor(path);
            var hint = new ToolTip { Content = "从绳子上取下，保留原文件", PlacementTarget = card.Surface };
            try
            {
                hint.IsOpen = true;
                await Task.Delay(100);
                Check(hint.IsVisible && hint.ActualWidth > 0, "浅色工具提示可以正常显示，不触发模板异常");
            }
            finally { hint.IsOpen = false; }
            Point center = card.Surface.PointToScreen(new Point(card.Surface.ActualWidth / 2, card.Surface.ActualHeight / 2));
            SetCursorPos((int)center.X, (int)center.Y);
            await Task.Delay(120);
            mouse_event(8, 0, 0, 0, UIntPtr.Zero);
            mouse_event(16, 0, 0, 0, UIntPtr.Zero);
            await Until(() => card.ActionMenu.IsOpen && card.ActionMenu.ActualHeight > 0);
            Check(card.ActionMenu.Actions.Count == 6 && !card.ActionMenu.Actions.ContainsKey(ShotAction.Copy), "图片右键保留六个操作，复制直接使用左键");
            Check(card.ActionMenu.Actions.Values.All(button => !button.IsKeyboardFocused), "右键展开时不默认选中任何操作");
            Check(card.ActionMenu.IsKeyboardFocused, "菜单容器接收键盘输入，操作项保持未选中");
            Check(card.ActionMenu.ActualWidth <= 220 && card.ActionMenu.ActualHeight < 290, "图片菜单以窄版单列显示，整体尺寸紧凑");
            var openButton = card.ActionMenu.Actions[ShotAction.Open];
            var editButton = card.ActionMenu.Actions[ShotAction.Edit];
            var openBounds = openButton.TransformToAncestor(card.ActionMenu).TransformBounds(new Rect(openButton.RenderSize));
            var editBounds = editButton.TransformToAncestor(card.ActionMenu).TransformBounds(new Rect(editButton.RenderSize));
            Check(Math.Abs(openBounds.Left - editBounds.Left) < 1 && openBounds.Bottom <= editBounds.Top, "图片菜单操作纵向排列，点击区域分开");
            var imagePreview = RenderMenu(card.ActionMenu);
            keybd_event(40, 0, 0, UIntPtr.Zero); keybd_event(40, 0, 2, UIntPtr.Zero);
            await Until(() => openButton.IsKeyboardFocused);
            Check(openButton.IsKeyboardFocused, "主动按方向键后才高亮第一项");
            SetCursorPos(600, 700);
            await Task.Delay(2100);
            Check(controller.Shelf.Revealed && card.Busy, "菜单打开时移走鼠标，绳子仍保持显示");
            keybd_event(27, 0, 0, UIntPtr.Zero); keybd_event(27, 0, 2, UIntPtr.Zero);
            await Until(() => !card.ActionMenu.IsOpen);
            Check(!card.ActionMenu.IsOpen && File.Exists(path), "图片菜单支持 Escape 关闭，不触发操作");
            card.ActionMenu.IsOpen = true;
            await Until(() => card.ActionMenu.IsOpen);
            Check(card.ActionMenu.Actions.Values.All(button => !button.IsKeyboardFocused), "重新展开菜单时清除上次操作焦点");
            card.ActionMenu.IsOpen = false;
            await ClickMenu(card.Surface);
            await Until(() => Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Contains(path));
            lastTestSequence = WindowsNative.GetClipboardSequenceNumber();
            Check(Clipboard.GetDataObject().GetDataPresent("PNG"), "图片左键仍同时复制图片和文件");

            var screen = Forms.Screen.PrimaryScreen;
            SetCursorPos(screen.WorkingArea.Right - 35, screen.WorkingArea.Bottom + 12);
            controller.ShowTrayMenu();
            await Until(() => controller.TrayMenu.IsOpen && controller.TrayMenu.ActualHeight > 0);
            var trayMenu = controller.TrayMenu;
            Check(trayMenu.ActualHeight < 480 && trayMenu.ActualWidth <= 360, "托盘主面板紧凑显示，屏幕选择直接可见");
            Point topLeft = trayMenu.PointToScreen(new Point());
            Point bottomRight = trayMenu.PointToScreen(new Point(trayMenu.ActualWidth, trayMenu.ActualHeight));
            Check(topLeft.X >= screen.Bounds.Left && topLeft.Y >= screen.Bounds.Top
                && bottomRight.X <= screen.Bounds.Right + 1 && bottomRight.Y <= screen.Bounds.Bottom + 1, "屏幕右下角的托盘菜单完整显示，没有超出屏幕");
            Check(trayMenu.MoreMenu.Items.OfType<MenuItem>().Count(x => x.IsEnabled) == 5, "更多操作保留添加、文件夹、全部取下、开机启动和更新入口");
            Check(trayMenu.ScreenButton.IsVisible && trayMenu.ScreenMode.Text == "跟随鼠标", "显示屏幕入口直接呈现当前模式");
            await ClickMenu(trayMenu.ScreenButton);
            await Until(() => trayMenu.ScreenMenu.IsOpen && trayMenu.ScreenMenu.ActualHeight > 0);
            var screenOptions = trayMenu.ScreenMenu.Items.OfType<MenuItem>().Where(item => item.IsCheckable).ToArray();
            Check(screenOptions.Length == Forms.Screen.AllScreens.Length + 1 && screenOptions[0].IsChecked
                && screenOptions.Count(item => item.IsChecked) == 1, "屏幕菜单列出跟随鼠标与所有已连接屏幕，当前模式单选标记");
            var screenMenuBounds = new Rect(trayMenu.ScreenMenu.PointToScreen(new Point()), trayMenu.ScreenMenu.PointToScreen(new Point(trayMenu.ScreenMenu.ActualWidth, trayMenu.ScreenMenu.ActualHeight)));
            Check(screenMenuBounds.Left >= screen.Bounds.Left && screenMenuBounds.Top >= screen.Bounds.Top
                && screenMenuBounds.Right <= screen.Bounds.Right + 1 && screenMenuBounds.Bottom <= screen.Bounds.Bottom + 1, "屏幕选择菜单在屏幕边缘完整显示");
            var screenPreview = new PngBitmapEncoder(); screenPreview.Frames.Add(BitmapFrame.Create(RenderMenu(trayMenu.ScreenMenu)));
            string screenPreviewPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "previews", "托盘菜单", "屏幕选择.png");
            Directory.CreateDirectory(Path.GetDirectoryName(screenPreviewPath));
            using (var output = File.Create(screenPreviewPath)) screenPreview.Save(output);
            await ClickMenu(screenOptions[1]);
            Check(!trayMenu.IsOpen && !trayMenu.ScreenMenu.IsOpen && store.State.DisplayDevice == Forms.Screen.AllScreens[0].DeviceName
                && new StateStore(store.Root).State.DisplayDevice == store.State.DisplayDevice, "点击固定屏幕后立即保存并关闭菜单");
            controller.ShowTrayMenu(); await Until(() => trayMenu.IsOpen);
            Check(trayMenu.ScreenMode.Text == "屏幕 1", "托盘重新打开后呈现已选屏幕");
            await ClickMenu(trayMenu.ScreenButton); await Until(() => trayMenu.ScreenMenu.IsOpen);
            Check(trayMenu.ScreenMenu.Items.OfType<MenuItem>().Count(item => item.IsChecked) == 1
                && trayMenu.ScreenMenu.Items.OfType<MenuItem>().Single(item => item.IsChecked).Header as string == "屏幕 1" + (Forms.Screen.AllScreens[0].Primary ? " · 主屏" : ""), "固定屏幕显示唯一选中标记");
            keybd_event(27, 0, 0, UIntPtr.Zero); keybd_event(27, 0, 2, UIntPtr.Zero);
            await Until(() => !trayMenu.ScreenMenu.IsOpen);
            Check(trayMenu.IsOpen, "Escape 先关闭屏幕选择，保留主面板");
            await ClickMenu(trayMenu.ScreenButton); await Until(() => trayMenu.ScreenMenu.IsOpen);
            await ClickMenu(trayMenu.ScreenMenu.Items.OfType<MenuItem>().Single(item => item.Header as string == "跟随鼠标"));
            Check(store.State.DisplayDevice == null && new StateStore(store.Root).State.DisplayDevice == null, "点击跟随鼠标取消固定并保存");
            SetCursorPos(screen.WorkingArea.Right - 35, screen.WorkingArea.Bottom + 12);
            controller.ShowTrayMenu(); await Until(() => trayMenu.IsOpen);
            await ClickMenu(trayMenu.Pictures);
            Check(trayMenu.IsOpen && controller.Captures.CollectClipboard && new StateStore(store.Root).State.CollectClipboard, "图片收集开关实际点击后开启并保存，面板保持打开");
            await ClickMenu(trayMenu.Text);
            Check(trayMenu.IsOpen && controller.Texts.CollectClipboard && new StateStore(store.Root).State.CollectTextClipboard && controller.Captures.CollectClipboard, "文字收集可独立开启，不影响图片收集");
            await ClickMenu(trayMenu.AutoHang);
            Check(trayMenu.IsOpen && !controller.Captures.AutoHang && !new StateStore(store.Root).State.AutoHang, "自动挂上开关即时保存，面板保持打开");
            controller.Captures.AutoHang = true;
            Check(trayMenu.AutoHang.IsChecked == true, "其他窗口改变自动挂上后，托盘同步开关状态");
            var headingPoint = trayMenu.PointToScreen(new Point(280, 38));
            SetCursorPos((int)headingPoint.X, (int)headingPoint.Y);
            await Task.Delay(120);
            var trayPreview = RenderMenu(trayMenu);
            ExportMenus(imagePreview, trayPreview);
            await ClickMenu(trayMenu.Pictures);
            await ClickMenu(trayMenu.Text);
            Check(trayMenu.IsOpen && !controller.Captures.CollectClipboard && !controller.Texts.CollectClipboard && !new StateStore(store.Root).State.CollectClipboard, "再次点击可关闭收集开关，原记录保留");
            await ClickMenu(trayMenu.MoreButton);
            await Until(() => trayMenu.MoreMenu.IsOpen && trayMenu.MoreMenu.ActualHeight > 0);
            var more = trayMenu.MoreMenu;
            Check(trayMenu.IsOpen, "展开更多操作时主面板保持可见");
            var moreBounds = new Rect(more.PointToScreen(new Point()), more.PointToScreen(new Point(more.ActualWidth, more.ActualHeight)));
            Check(screen.Bounds.Contains((int)moreBounds.Left, (int)moreBounds.Top) && moreBounds.Right <= screen.Bounds.Right + 1 && moreBounds.Bottom <= screen.Bounds.Bottom + 1, "更多操作靠近屏幕边缘也完整显示");
            await ClickMenu(more.Items.OfType<MenuItem>().Single(item => item.Header as string == "全部取下（保留文件）"));
            await Until(() => !more.IsOpen && controller.Gallery.Items.Count == 0);
            Check(File.Exists(path) && !trayMenu.IsOpen, "更多操作中的全部取下仍保留原图，操作后关闭菜单");
            SetCursorPos(screen.WorkingArea.Right - 35, screen.WorkingArea.Bottom + 12);
            controller.ShowTrayMenu();
            await Until(() => trayMenu.IsOpen);
            await ClickMenu(trayMenu.MoreButton); await Until(() => more.IsOpen);
            keybd_event(27, 0, 0, UIntPtr.Zero); keybd_event(27, 0, 2, UIntPtr.Zero);
            await Until(() => !more.IsOpen);
            Check(trayMenu.IsOpen, "Escape 先关闭更多操作，保留主面板");
            trayMenu.Pictures.Focus();
            await Task.Delay(100);
            keybd_event(32, 0, 0, UIntPtr.Zero); keybd_event(32, 0, 2, UIntPtr.Zero);
            await Task.Delay(100);
            Check(trayMenu.IsOpen && controller.Captures.CollectClipboard, "键盘空格可切换收集开关并保持面板打开");
            trayMenu.Pictures.IsChecked = false;
            keybd_event(27, 0, 0, UIntPtr.Zero);
            keybd_event(27, 0, 2, UIntPtr.Zero);
            await Until(() => !trayMenu.IsOpen);
            Check(!trayMenu.IsOpen, "Escape 可关闭托盘菜单");
            controller.ShowTrayMenu();
            await Until(() => trayMenu.IsOpen);
            SetCursorPos(950, 450);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            await Until(() => !trayMenu.IsOpen);
            Check(!trayMenu.IsOpen && File.Exists(path), "点击菜单外部可关闭菜单，不触发图片操作");
            SetCursorPos(screen.WorkingArea.Right - 35, screen.WorkingArea.Bottom + 12);
            controller.ShowTrayMenu(); await Until(() => trayMenu.IsOpen);
            await ClickMenu(trayMenu.MoreButton); await Until(() => more.IsOpen);
            SetCursorPos(950, 450);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            await Until(() => !trayMenu.IsOpen && !more.IsOpen);
            Check(!more.IsOpen, "更多操作展开时，点击外部同时关闭两层菜单");
            SetCursorPos(screen.WorkingArea.Right - 35, screen.WorkingArea.Bottom + 12);
            controller.ShowTrayMenu(); await Until(() => trayMenu.IsOpen);
            await ClickMenu(trayMenu.BasketButton);
            Check(!trayMenu.IsOpen && controller.Basket != null, "图片篮子快捷入口打开篮子并关闭菜单");
            controller.Basket.Close();
            await Task.Delay(200);
            SetCursorPos(screen.WorkingArea.Right - 35, screen.WorkingArea.Bottom + 12);
            controller.ShowTrayMenu(); await Until(() => trayMenu.IsOpen);
            await ClickMenu(trayMenu.TextButton);
            Check(!trayMenu.IsOpen && controller.TextWindow != null, "文字信封快捷入口打开文字列表并关闭菜单");
            controller.TextWindow.Close();
        }

        static async Task ClickMenu(FrameworkElement entry)
        {
            var toggle = entry as CheckBox;
            if (toggle != null)
            {
                var peer = new System.Windows.Automation.Peers.CheckBoxAutomationPeer(toggle);
                ((System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
                await Task.Delay(60); return;
            }
            var button = entry as Button;
            if (button != null)
            {
                var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
                ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
                await Task.Delay(100); return;
            }
            Point center = entry.PointToScreen(new Point(entry.ActualWidth / 2, entry.ActualHeight / 2));
            SetCursorPos((int)center.X, (int)center.Y);
            await Task.Delay(100);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(120);
        }

        static RenderTargetBitmap RenderMenu(ContextMenu menu)
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth * 2), (int)Math.Ceiling(menu.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(menu);
            bitmap.Freeze();
            return bitmap;
        }

        static void ExportMenus(BitmapSource image, BitmapSource tray)
        {
            int width = (int)(tray.Width + 348), height = (int)Math.Max(image.Height, tray.Height) + 72;
            var drawing = new DrawingVisual();
            using (var painter = drawing.RenderOpen())
            {
                painter.DrawRectangle(new SolidColorBrush(Color.FromRgb(238, 242, 248)), null, new Rect(0, 0, width, height));
                painter.DrawText(new FormattedText("图片右键菜单", CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 15, Brushes.DimGray, 1), new Point(41, 22));
                painter.DrawText(new FormattedText("托盘右键菜单", CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 15, Brushes.DimGray, 1), new Point(341, 22));
                painter.DrawImage(image, new Rect(24, 48, image.Width, image.Height));
                painter.DrawImage(tray, new Rect(324, 48, tray.Width, tray.Height));
            }
            var bitmap = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "previews", "右键菜单", "实际菜单.png"));
            using (var output = File.Create(path)) encoder.Save(output);
            encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(Path.Combine(Path.GetDirectoryName(path), "图片菜单实装.png"))) encoder.Save(output);
            encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(tray));
            string trayPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "previews", "托盘菜单", "实装.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(trayPath));
            using (var output = File.Create(trayPath)) encoder.Save(output);
            Console.WriteLine("实际菜单预览：" + path);
        }

        static async Task RecordMotion(ShelfWindow shelf)
        {
            var frames = new System.Collections.Generic.List<RenderTargetBitmap>();
            double scale = WindowsNative.MonitorScale(shelf.Display);
            double width = shelf.ActualWidth, height = shelf.Display.Bounds.Height / scale;
            double ratio = 960 / width;
            for (int i = 0; i < 48; i++)
            {
                var drawing = new DrawingVisual();
                using (var painter = drawing.RenderOpen())
                {
                    painter.PushTransform(new ScaleTransform(ratio, ratio));
                    painter.DrawRectangle(new SolidColorBrush(Color.FromRgb(238, 242, 248)), null, new Rect(0, 0, width, height));
                    painter.DrawRectangle(new VisualBrush(shelf), null, new Rect(0, 0, width, shelf.ActualHeight));
                    foreach (var flight in shelf.MotionWindows)
                        painter.DrawRectangle(new VisualBrush(flight), null, new Rect(0, 0, flight.ActualWidth, flight.ActualHeight));
                    foreach (var pin in shelf.PinWindows)
                    {
                        pin.UpdateLayout();
                        WindowsNative.Rectangle bounds;
                        WindowsNative.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(pin).Handle, out bounds);
                        painter.DrawRectangle(new VisualBrush(pin), null, new Rect((bounds.Left - shelf.Display.Bounds.Left) / scale, (bounds.Top - shelf.Display.Bounds.Top) / scale, pin.ActualWidth, pin.ActualHeight));
                    }
                    painter.Pop();
                }
                var bitmap = new RenderTargetBitmap(960, (int)(height * ratio), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                bitmap.Freeze();
                frames.Add(bitmap);
                await Task.Delay(25);
            }
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "motion-frames");
            Directory.CreateDirectory(directory);
            for (int i = 0; i < frames.Count; i++)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(frames[i]));
                using (var output = File.Create(Path.Combine(directory, i.ToString("D3") + ".png"))) encoder.Save(output);
            }
            Console.WriteLine("动效预览帧：" + directory);
        }

        static async Task Until(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(7);
            while (!condition())
            {
                if (DateTime.UtcNow >= deadline) throw new Exception("等待结果超时，当前图片数量：" + controller.Gallery.Items.Count
                    + "，展开：" + controller.Shelf.Revealed + "，鼠标命中：" + WindowFromPoint(WindowsNative.Cursor())
                    + "，绳子窗口：" + controller.Shelf.Handle
                    + "，鼠标位置：" + WindowsNative.Cursor().X + "," + WindowsNative.Cursor().Y
                    + "，全屏：" + WindowsNative.FullScreen(controller.Shelf.Display, controller.Shelf.Handle));
                await Task.Delay(70);
            }
        }

        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            checks++;
            Console.WriteLine("通过：" + name);
        }
    }
}
