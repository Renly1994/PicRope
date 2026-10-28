using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace ScreenLine.Tests
{
    internal static class ReleaseTests
    {
        static int checks;
        static string root;
        static DesktopController controller;
        static CaptureWindow capture;
        static Forms.IDataObject clipboard;
        static uint writtenSequence;
        static WindowsNative.Point pointer;
        static IntPtr focus;
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(WindowsNative.Point point);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, int flags);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, int message, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

        [STAThread]
        public static void Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LightTheme.Install(app);
            app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) { Console.WriteLine(e.Exception); e.Handled = true; app.Shutdown(1); };
            app.Startup += async delegate
            {
                pointer = WindowsNative.Cursor(); focus = WindowsNative.GetForegroundWindow();
                var previous = Forms.Clipboard.GetDataObject();
                if (previous != null)
                {
                    var snapshot = new Forms.DataObject();
                    foreach (string format in previous.GetFormats(false))
                    {
                        object value = previous.GetData(format, false);
                        var stream = value as MemoryStream; if (stream != null) value = new MemoryStream(stream.ToArray());
                        var image = value as System.Drawing.Image; if (image != null) value = new System.Drawing.Bitmap(image);
                        if (value != null) snapshot.SetData(format, false, value);
                    }
                    clipboard = snapshot;
                }
                try { await Run(); Console.WriteLine("1.2.0 专项验证：{0} 项通过", checks); }
                catch (Exception error) { Console.WriteLine("专项验证失败：" + error); Environment.ExitCode = 1; }
                finally
                {
                    mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                    if (capture != null) capture.Close();
                    if (controller != null) controller.Dispose();
                }
                if (writtenSequence != 0 && writtenSequence == WindowsNative.GetClipboardSequenceNumber())
                { if (clipboard != null) await WindowsClipboard.Write(clipboard); else Forms.Clipboard.Clear(); }
                SetCursorPos(pointer.X, pointer.Y); WindowsNative.SetForegroundWindow(focus);
                app.Shutdown(Environment.ExitCode);
            };
            app.Run();
        }

        static async Task Run()
        {
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "release-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var store = new StateStore(Path.Combine(root, "profile"));
            controller = new DesktopController(store, Path.Combine(root, "watched"), false);
            await controller.Start(); controller.Shelf.Capturing = true; controller.Shelf.Reveal(true, true, 60);
            string original = Path.Combine(root, "本地原图.png");
            File.WriteAllBytes(original, ImageContent.EncodeClipboard(Pattern(40, 30)));
            Check(await controller.Captures.Import(original, null, false), "可以添加本地图片");
            var imported = controller.Gallery.Items.Single();
            Check(FileRules.IsInside(imported.Path, store.Inbox) && !FileRules.SameFile(original, imported.Path), "添加本地图片时收集副本");
            controller.Act(ShotAction.Trash, imported);
            Check(File.Exists(original) && !File.Exists(imported.Path), "删除导入图片只删除副本");
            controller.Gallery.Add(original, ImageContent.Read(original));
            var legacy = controller.Gallery.Items.Single();
            controller.Act(ShotAction.Trash, legacy);
            Check(File.Exists(original) && !controller.Gallery.Collected.Contains(legacy), "删除旧版外部图片记录时保留原图");
            controller.Gallery.Add(original, ImageContent.Read(original));
            controller.Act(ShotAction.Edit, controller.Gallery.Items.Single());
            for (int i = 0; i < 60 && controller.Editors.Count == 0; i++) await Task.Delay(50);
            Check(controller.Editors.Count == 1 && FileRules.IsInside(controller.Editors[0].ImagePath, store.Inbox)
                && File.Exists(original), "编辑旧版外部图片前创建副本");
            controller.Editors[0].CloseWithoutPrompt();
            controller.Act(ShotAction.Trash, controller.Gallery.Items.Single());
            Check(File.Exists(original), "编辑后的副本删除时保留原图");
            var bulk = new List<CaptureItem>();
            for (int i = 0; i < 3; i++)
            {
                string path = Path.Combine(root, "批量" + i + ".png");
                File.WriteAllBytes(path, ImageContent.EncodeClipboard(Pattern(40, 30)));
                controller.Gallery.Add(path, ImageContent.Read(path));
                bulk.Add(controller.Gallery.Items.Last());
            }
            controller.Shelf.CardFor(bulk[0].Path).Detached = true;
            controller.Act(ShotAction.RemoveAll, bulk[1]);
            Check(controller.Gallery.Items.SequenceEqual(new[] { bulk[0] }), "批量取下不影响已取下的桌面贴图");
            controller.Shelf.CardFor(bulk[0].Path).Detached = false;
            controller.Gallery.Clear(); foreach (var item in bulk) controller.Gallery.Add(item.Path, item.Image);
            controller.Act(ShotAction.RemoveLeft, bulk[1]);
            Check(controller.Gallery.Items.SequenceEqual(bulk.Skip(1)) && File.Exists(bulk[0].Path), "批量取下左侧保留原文件和其余图片");
            controller.Gallery.Clear(); foreach (var item in bulk) controller.Gallery.Add(item.Path, item.Image);
            controller.Act(ShotAction.RemoveRight, bulk[1]);
            Check(controller.Gallery.Items.SequenceEqual(bulk.Take(2)) && File.Exists(bulk[2].Path), "批量取下右侧保留原文件和其余图片");
            controller.Gallery.Clear(); foreach (var item in bulk) controller.Gallery.Add(item.Path, item.Image);
            controller.Act(ShotAction.RemoveOthers, bulk[1]);
            Check(controller.Gallery.Items.SequenceEqual(new[] { bulk[1] }) && bulk.All(item => controller.Gallery.Collected.Contains(item)), "仅保留当前图片，其余保留在篮子");
            controller.Act(ShotAction.RemoveAll, bulk[1]);
            Check(controller.Gallery.Items.Count == 0 && bulk.All(item => File.Exists(item.Path)), "批量取下全部不删除文件");
            foreach (var item in bulk) controller.Gallery.Forget(item);
            controller.Shelf.Capturing = true; controller.Shelf.Reveal(true, true, 60);
            await Task.Delay(300);
            Check(!controller.Shelf.BasketButton.IsVisible && !controller.Shelf.TextButton.IsVisible, "两项关闭时隐藏全部挂件");
            controller.TrayMenu.Pictures.IsChecked = true; await Task.Delay(40);
            Check(controller.Shelf.BasketButton.IsVisible && !controller.Shelf.TextButton.IsVisible, "仅开启图片时只显示篮子");
            controller.TrayMenu.Text.IsChecked = true; await Task.Delay(40);
            Check(controller.Shelf.BasketButton.IsVisible && controller.Shelf.TextButton.IsVisible, "两项开启时显示两个挂件");
            controller.Shelf.UpdateLayout();
            var basketBounds = controller.Shelf.BasketButton.TransformToAncestor(controller.Shelf).TransformBounds(new Rect(controller.Shelf.BasketButton.RenderSize));
            var textBounds = controller.Shelf.TextButton.TransformToAncestor(controller.Shelf).TransformBounds(new Rect(controller.Shelf.TextButton.RenderSize));
            double rightGap = controller.Shelf.ActualWidth - Math.Max(basketBounds.Right, textBounds.Right);
            Check(rightGap >= 29 && rightGap <= 31, "篮子恢复原来的靠右位置");
            var rope = (System.Windows.Shapes.Path)typeof(ShelfWindow).GetField("rope", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(controller.Shelf);
            var curve = (PathGeometry)rope.Data;
            var start = rope.TransformToAncestor(controller.Shelf).Transform(curve.Figures[0].StartPoint);
            var end = rope.TransformToAncestor(controller.Shelf).Transform(((QuadraticBezierSegment)curve.Figures[0].Segments[0]).Point2);
            Check(Math.Abs(start.X - 24) < 1 && Math.Abs(controller.Shelf.ActualWidth - end.X - 24) < 1,
                "避让窗口按钮时绳子仍延伸到屏幕两端");
            var backdrop = new Window { Title = "窗口控制按钮验证", WindowState = WindowState.Maximized, ShowInTaskbar = false };
            backdrop.Show(); backdrop.Activate(); controller.Shelf.Capturing = false;
            var area = controller.Shelf.Display.WorkingArea;
            double scale = WindowsNative.MonitorScale(controller.Shelf.Display);
            foreach (int offset in new[] { 23, 69, 115 })
            {
                var caption = new WindowsNative.Point { X = area.Right - (int)Math.Round(offset * scale), Y = area.Top + (int)Math.Round(16 * scale) };
                SetCursorPos(caption.X, caption.Y); await Task.Delay(160);
                Check(!controller.Shelf.Revealed, "指向窗口控制按钮时临时收起绳子");
                Check(GetAncestor(WindowFromPoint(caption), 2) == new System.Windows.Interop.WindowInteropHelper(backdrop).Handle,
                    "右上角窗口控制按钮区域可正常点击");
            }
            SetCursorPos(area.Right - (int)Math.Round(115 * scale), area.Top + (int)Math.Round(16 * scale));
            mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(200);
            Check(backdrop.WindowState == WindowState.Minimized, "避让后实际点击最小化按钮生效");
            backdrop.WindowState = WindowState.Maximized; backdrop.Activate();
            SetCursorPos(area.Right - (int)Math.Round(80 * scale), area.Top + (int)Math.Round(160 * scale));
            await Task.Delay(450);
            Check(controller.Shelf.Revealed, "移出控制按钮区域后恢复展开，仍能接近篮子");
            Point basketPoint = controller.Shelf.BasketButton.PointToScreen(new Point(controller.Shelf.BasketButton.ActualWidth / 2, 90));
            SetCursorPos((int)basketPoint.X, (int)basketPoint.Y); await Task.Delay(160);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(150);
            Check(controller.Basket != null && controller.Basket.IsVisible, "从下方接近时仍可实际点击篮子");
            controller.Basket.Close();
            backdrop.WindowState = WindowState.Normal;
            var backdropHandle = new System.Windows.Interop.WindowInteropHelper(backdrop).Handle;
            WindowsNative.SetWindowPos(backdropHandle, IntPtr.Zero, area.Left + 300, area.Top + 60, 1000, 700, 0x14);
            backdrop.Activate(); await Task.Delay(150);
            WindowsNative.Rectangle normalBounds;
            Check(WindowsNative.GetWindowRect(backdropHandle, out normalBounds), "读取非最大化窗口的位置");
            var normalCaption = new WindowsNative.Point { X = normalBounds.Right - (int)Math.Round(23 * scale), Y = normalBounds.Top + (int)Math.Round(16 * scale) };
            SetCursorPos(normalCaption.X, normalCaption.Y); await Task.Delay(180);
            Check(controller.Shelf.Revealed && !WindowsNative.OverCaptionButtons(controller.Shelf.Display, normalCaption, controller.Shelf.Handle),
                "普通窗口右上角不触发避让");
            backdrop.Close(); controller.Shelf.Capturing = true;
            controller.Shelf.Reveal(true, true, 60);
            var before = controller.Shelf.TextButton.PointToScreen(new Point());
            controller.TrayMenu.Pictures.IsChecked = false; await Task.Delay(40);
            var after = controller.Shelf.TextButton.PointToScreen(new Point());
            Check(!controller.Shelf.BasketButton.IsVisible && controller.Shelf.TextButton.IsVisible && after.X > before.X + 100, "仅文字开启时信封向右补位");
            controller.Shelf.UpdateLayout();
            textBounds = controller.Shelf.TextButton.TransformToAncestor(controller.Shelf).TransformBounds(new Rect(controller.Shelf.TextButton.RenderSize));
            Check(controller.Shelf.ActualWidth - textBounds.Right >= 31 && controller.Shelf.ActualWidth - textBounds.Right <= 33,
                "单独显示信封时恢复原来的靠右位置");
            controller.TrayMenu.Pictures.IsChecked = true;
            var content = Pattern(900, 600);
            for (int i = 0; i < 3; i++)
            {
                string path = Path.Combine(root, "图片" + i + ".png"); File.WriteAllBytes(path, ImageContent.EncodeClipboard(content));
                controller.Gallery.Collect(path, ImageContent.Read(path));
            }
            controller.Captures.Hang(controller.Gallery.Collected[0]); controller.ShowBasket(); await Task.Delay(150);
            var basket = controller.Basket;
            Click(basket.SelectAllButton); Check(basket.SelectedCount == 2 && (string)basket.SelectAllButton.Content == "取消全选", "图片全选仅选当前待挂标签");
            Click(basket.SelectAllButton); Check(basket.SelectedCount == 0, "图片取消全选清空选择");
            Click(basket.AllTab); Click(basket.SelectAllButton); Check(basket.SelectedCount == 3, "全部标签全选包含已挂图片");
            Click(basket.PendingTab); Check(basket.SelectedCount == 2, "切换标签后清理隐藏图片的选择");
            await Task.Delay(80);
            var thumb = ((Grid)((Border)((StackPanel)basket.ChoiceFor(controller.Gallery.Collected[1].Path).Content).Children[0]).Child).Children.OfType<Image>().Single();
            Check(thumb.Source != null, "进入当前标签后可见缩略图按需显示");
            Export("图片全选", basket); basket.Close();
            controller.Texts.Add("会议：确认录制时间"); controller.Texts.Add("会议：整理演示材料"); controller.Texts.Add("保留的参考链接");
            controller.ShowTexts(); await Task.Delay(50);
            var texts = controller.TextWindow; texts.Search.Text = "会议"; Click(texts.SelectAllButton);
            Check(controller.Texts.Items.Count(item => item.IsMarked) == 2, "文字全选仅选择搜索结果");
            Click(texts.DeleteSelectedButton); Check(controller.Texts.Items.Count == 3, "批量删除先确认数量");
            Export("文字批量删除", texts);
            Click(texts.DeleteSelectedButton); Check(controller.Texts.Items.Count == 1 && new StateStore(store.Root).State.Texts.Single().Content == "保留的参考链接", "确认后只删除选中文字并持久化");
            texts.Search.Clear(); Click(texts.SelectAllButton); texts.Search.Text = "无结果";
            Check(!controller.Texts.Items.Any(item => item.IsMarked) && !texts.DeleteSelectedButton.IsEnabled, "修改搜索条件清理不可见选择");
            texts.Close();
            var retained = controller.Texts.Items[0]; retained.IsMarked = true;
            store.Save(); Check(!File.ReadAllText(Path.Combine(store.Root, "state.json")).Contains("IsMarked"), "勾选状态不写进用户数据");
            Directory.CreateDirectory(Path.Combine(store.Root, "state.json.tmp"));
            bool failed = false;
            try { controller.Texts.RemoveMany(new[] { retained }); } catch (IOException) { failed = true; } catch (UnauthorizedAccessException) { failed = true; }
            Check(failed && controller.Texts.Items.Single() == retained, "批量删除保存失败时恢复原记录");
            Directory.Delete(Path.Combine(store.Root, "state.json.tmp"));
            using (var owner = new WindowsEvents())
            using (var conflict = new WindowsEvents())
            {
                Check(owner.RegisterCapture(6, 0x7B), "可注册自定义截图组合键");
                Check(!conflict.RegisterCapture(6, 0x7B), "冲突组合键被拒绝");
                Check(conflict.RegisterCapture(6, 0x7A), "可使用未冲突的组合键");
                Check(!owner.RegisterCapture(6, 0x7A) && owner.CaptureShortcutAvailable, "改键冲突保留旧绑定");
                Check(!owner.RegisterCapture(0, 0x41), "拒绝没有修饰键的全局快捷键");
            }
            Check(controller.SetCaptureShortcut(6, 0x79), "通过设置入口更改截图快捷键");
            var restored = new StateStore(store.Root);
            Check(restored.State.CaptureModifiers == 6 && restored.State.CaptureKey == 0x79, "截图快捷键重启后保留");
            var shortcut = new ShortcutWindow(6, 0x79, controller.SetCaptureShortcut);
            shortcut.Show(); await Task.Delay(150);
            Check(shortcut.Input.IsKeyboardFocused, "快捷键输入框获得实际键盘焦点");
            keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x10, 0, 0, UIntPtr.Zero); await Task.Delay(40);
            keybd_event(0x77, 0, 0, UIntPtr.Zero); await Task.Delay(40);
            keybd_event(0x77, 0, 2, UIntPtr.Zero); keybd_event(0x10, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero);
            await Task.Delay(40);
            Check(shortcut.Modifiers == 6 && shortcut.VirtualKey == 0x77 && shortcut.Input.Text == "Ctrl+Shift+F8", "设置窗口正确识别真实键盘组合键：" + shortcut.Input.Text);
            Click(shortcut.SaveButton); Check(!shortcut.IsVisible && new StateStore(store.Root).State.CaptureKey == 0x77, "设置窗口保存并生效");
            controller.Shelf.Reveal(false, false, 0); await Task.Delay(300);
            await CheckCapture();
            controller.Captures.AutoHang = false;
            int collected = controller.Gallery.Collected.Count;
            await controller.Captures.AcceptCapture(content); writtenSequence = WindowsNative.GetClipboardSequenceNumber(); await Task.Delay(400);
            Check(controller.Gallery.Collected.Count == collected + 1 && controller.Gallery.Items.Count == 1, "主动截图遵守自动挂图开关，剪贴板开启时不重复收集");
            controller.TrayMenu.Pictures.IsChecked = false;
            controller.Captures.AutoHang = true;
            await controller.Captures.AcceptCapture(content); writtenSequence = WindowsNative.GetClipboardSequenceNumber();
            int clipboardWidth = await ClipboardWidth();
            Check(controller.Gallery.Items.Count == 2 && clipboardWidth == 900, "关闭剪贴板收集后主动截图仍可挂图并复制完整结果");
            Check(controller.Texts.Items.Count == 1, "图片复制不误收为文字");
            await CheckShortcutMessage();
        }

        static async Task CheckCapture()
        {
            var display = WindowsNative.ScreenAtPointer();
            capture = new CaptureWindow(display, Pattern(display.Bounds.Width, display.Bounds.Height));
            var result = capture.PickAsync(); await Task.Delay(150);
            capture.Select(new Rect(120, 160, 640, 380));
            var plain = capture.Surface.Export();
            Check(plain.PixelWidth == 640 && plain.PixelHeight == 380, "导出尺寸为实际像素选区，排除工具条和遮罩");
            Check(Pixel(plain, 100, 100).SequenceEqual(Pixel(capture.Surface.Picture, 220, 260)), "无标注导出像素与原图一致");
            Click(capture.Tools["矩形"]);
            await Drag(new Point(190, 220), new Point(420, 340));
            Check(capture.Surface.Marks.Count == 1 && capture.Surface.Marks[0].Tool == "矩形", "真实鼠标拖拽生成矩形标注");
            var marked = capture.Surface.Export();
            Check(!Pixel(marked, 70, 60).SequenceEqual(Pixel(plain, 70, 60)), "标注进入最终导出像素");
            Click(capture.Tools["撤销"]); Check(capture.Surface.Marks.Count == 0 && Pixel(capture.Surface.Export(), 70, 60).SequenceEqual(Pixel(plain, 70, 60)), "撤销恢复原像素");
            Click(capture.Tools["重做"]); Check(capture.Surface.Marks.Count == 1, "重做恢复标注");
            foreach (string tool in new[] { "圆形", "箭头", "画笔", "马赛克" })
            {
                Click(capture.Tools[tool]); await Drag(new Point(440, 250), new Point(630, 390));
                Check(capture.Surface.Marks.Last().Tool == tool, "真实鼠标绘制" + tool);
            }
            Click(capture.Tools["文字"]);
            var origin = capture.PointToScreen(new Point(250 / WindowsNative.MonitorScale(display), 410 / WindowsNative.MonitorScale(display)));
            SetCursorPos((int)origin.X, (int)origin.Y); mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(40);
            var input = ((Canvas)((Grid)capture.Content).Children[1]).Children.OfType<TextBox>().Single();
            Check(input.IsVisible && input.IsKeyboardFocused, "文字工具打开原地输入框");
            input.Text = "截图标注 · 图绳 1.2.0"; Click(capture.Tools["选择"]);
            Check(capture.Surface.Marks.Last().Text == "截图标注 · 图绳 1.2.0", "文字标注保留中文内容");
            Export("区域截图与基础编辑", capture);
            await Drag(new Point(120, 160), new Point(140, 180));
            Check(capture.Selection == new Rect(140, 180, 620, 360), "拖动选区角点可调整截图边界");
            capture.Select(new Rect(120, 160, 640, 380));
            Click(capture.Tools["完成"]); var exported = await result; capture = null;
            Check(exported != null && exported.PixelWidth == 640 && exported.PixelHeight == 380, "完成操作返回最终图像并关闭截图窗口");
            Save("截图导出结果", exported);
            capture = new CaptureWindow(display, Pattern(display.Bounds.Width, display.Bounds.Height));
            result = capture.PickAsync(); capture.Close();
            Check(await result == null, "取消截图不返回图像或创建文件"); capture = null;
            var clipped = CaptureSurface.Region(new Point(-10, -20), new Point(100, 80), new Rect(0, 0, 1920, 1080));
            Check(clipped == new Rect(0, 0, 100, 80), "选区限制在实际屏幕像素边界内");
        }

        static async Task CheckShortcutMessage()
        {
            controller.Shelf.Capturing = false;
            var events = (WindowsEvents)typeof(DesktopController).GetField("events", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(controller);
            int id = (int)typeof(WindowsEvents).GetField("captureId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(events);
            var source = (System.Windows.Interop.HwndSource)typeof(WindowsEvents).GetField("source", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(events);
            int files = Directory.GetFiles(controller.Gallery.Store.Inbox).Length;
            PostMessage(source.Handle, 0x312, new IntPtr(id), IntPtr.Zero); await Task.Delay(500);
            Check(controller.Capture != null && controller.Capture.IsVisible, "全局热键消息打开截图编辑界面");
            var first = controller.Capture; controller.ShowCapture(); await Task.Delay(50);
            Check(controller.Capture == first, "重复截图请求只保留一个窗口");
            first.Close(); await Task.Delay(100);
            Check(!controller.Shelf.Capturing && controller.Capture == null && Directory.GetFiles(controller.Gallery.Store.Inbox).Length == files, "取消后解除截图状态且不留临时文件");
        }

        static async Task<int> ClipboardWidth()
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try { var picture = Clipboard.GetImage(); if (picture != null) return picture.PixelWidth; }
                catch (COMException) { }
                await Task.Delay(60);
            }
            return 0;
        }

        static async Task Drag(Point from, Point to)
        {
            double scale = WindowsNative.MonitorScale(WindowsNative.ScreenAtPointer());
            var a = capture.PointToScreen(new Point(from.X / scale, from.Y / scale));
            var b = capture.PointToScreen(new Point(to.X / scale, to.Y / scale));
            SetCursorPos((int)a.X, (int)a.Y); await Task.Delay(80);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(30);
            for (int i = 1; i <= 8; i++) { SetCursorPos((int)(a.X + (b.X - a.X) * i / 8), (int)(a.Y + (b.Y - a.Y) * i / 8)); await Task.Delay(16); }
            mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(40);
        }

        static BitmapSource Pattern(int width, int height)
        {
            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 4; bool line = x % 32 < 2 || y % 32 < 2;
                pixels[index] = (byte)(line ? 205 : 245); pixels[index + 1] = (byte)(line ? 190 : 240); pixels[index + 2] = (byte)(line ? 175 : 232); pixels[index + 3] = 255;
            }
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); image.Freeze(); return image;
        }

        static byte[] Pixel(BitmapSource image, int x, int y) { byte[] pixel = new byte[4]; new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return pixel; }
        static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        static void Export(string name, FrameworkElement window) { window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(window); Save(name, bitmap); }
        static void Save(string name, BitmapSource bitmap) { string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "release-preview"); Directory.CreateDirectory(directory); File.WriteAllBytes(Path.Combine(directory, name + ".png"), ImageContent.EncodeClipboard(bitmap)); }
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("通过：" + message); }
    }
}
