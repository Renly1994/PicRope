using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace ScreenLine.Tests
{
    internal static class MultiMonitorChecks
    {
        public static bool Simulating, MouseDown, CaptionButtons;
        public static WindowsNative.Point Pointer;
        public static Forms.Screen[] Screens;
        public static readonly Dictionary<string, double> Scales = new Dictionary<string, double>();
        public static readonly HashSet<string> FullScreens = new HashSet<string>();
        public static readonly Dictionary<IntPtr, WindowsNative.Rectangle> Positions = new Dictionary<IntPtr, WindowsNative.Rectangle>();
        static readonly BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly List<string> failures = new List<string>();
        static int checks, scenarios;
        static ShelfWindow shelf;
        static double slideAtMove;
        static bool switchingAtMove;
        static string projectRoot;

        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, int message, IntPtr w, IntPtr l);

        [STAThread]
        public static void Main(string[] arguments)
        {
            Console.OutputEncoding = Encoding.UTF8;
            projectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            LightTheme.Install(application);
            application.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
            {
                failures.Add("调度器异常：" + args.Exception); args.Handled = true;
            };
            application.Startup += async delegate
            {
                try
                {
                    await NativeSmoke();
                    Simulating = true;
                    var layouts = new[] {
                        new System.Drawing.Rectangle(1920, 0, 2560, 1440),
                        new System.Drawing.Rectangle(-1920, 0, 1920, 1080),
                        new System.Drawing.Rectangle(0, -1200, 1920, 1200),
                        new System.Drawing.Rectangle(0, 1080, 1920, 1080),
                        new System.Drawing.Rectangle(1920, -420, 1080, 1920)
                    };
                    var names = new[] { "右侧副屏", "左侧负坐标副屏", "上方负坐标副屏", "下方副屏", "竖向错位副屏" };
                    if (arguments.Contains("--switch-regression")) await Scenario(names[0], layouts[0], 1, false);
                    else
                    {
                        for (int layout = 0; layout < layouts.Length; layout++)
                            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                                await Scenario(names[layout], layouts[layout], scale, false);
                        await Scenario("顶部任务栏副屏", new System.Drawing.Rectangle(-2560, -300, 2560, 1440), 1.5, true);
                    }
                    Console.WriteLine("多屏验证完成：{0} 个模拟场景，{1} 项断言，{2} 项失败", scenarios, checks, failures.Count);
                    foreach (string failure in failures) Console.WriteLine("未通过：" + failure);
                }
                catch (Exception error) { failures.Add(error.ToString()); Console.WriteLine("验证异常：" + error); }
                finally { if (shelf != null) shelf.Close(); application.Shutdown(failures.Count == 0 ? 0 : 1); }
            };
            application.Run();
        }

        public static Forms.Screen ScreenAtPointer()
        {
            return Screens.FirstOrDefault(screen => screen.Bounds.Contains(Pointer.X, Pointer.Y)) ?? Screens[0];
        }

        public static bool SetWindowPos(IntPtr handle, int x, int y, int width, int height)
        {
            if (shelf != null && shelf.Handle == handle)
            {
                slideAtMove = ((System.Windows.Media.TranslateTransform)typeof(ShelfWindow).GetField("slide", instanceFlags).GetValue(shelf)).Y;
                switchingAtMove = (bool)typeof(ShelfWindow).GetField("switchingDisplay", instanceFlags).GetValue(shelf);
            }
            Positions[handle] = new WindowsNative.Rectangle { Left = x, Top = y, Right = x + width, Bottom = y + height };
            return true;
        }

        static Forms.Screen Screen(string name, System.Drawing.Rectangle bounds, System.Drawing.Rectangle work, bool primary)
        {
            var screen = (Forms.Screen)FormatterServices.GetUninitializedObject(typeof(Forms.Screen));
            typeof(Forms.Screen).GetField("bounds", instanceFlags).SetValue(screen, bounds);
            typeof(Forms.Screen).GetField("workingArea", instanceFlags).SetValue(screen, work);
            typeof(Forms.Screen).GetField("deviceName", instanceFlags).SetValue(screen, name);
            typeof(Forms.Screen).GetField("primary", instanceFlags).SetValue(screen, primary);
            object generation = typeof(Forms.Screen).GetProperty("DesktopChangedCount", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null, null);
            typeof(Forms.Screen).GetField("currentDesktopChangedCount", instanceFlags).SetValue(screen, generation);
            return screen;
        }

        static Gallery Open()
        {
            var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
            var gallery = new Gallery(new StateStore(root));
            shelf = new ShelfWindow(gallery, delegate { }, delegate { }) { Opacity = 0 };
            shelf.Show();
            return gallery;
        }

        static async Task NativeSmoke()
        {
            Console.WriteLine("实际系统：{0} 块显示器，运行原始 Win32 调用", Forms.Screen.AllScreens.Length);
            Open();
            shelf.SetRevealOnTopHover(false);
            var expected = WindowsNative.ScreenAtPointer();
            Check(shelf.Display.DeviceName == expected.DeviceName, "实际系统启动时选择鼠标所在屏幕");
            WindowsNative.Rectangle bounds;
            Check(WindowsNative.GetWindowRect(shelf.Handle, out bounds)
                && bounds.Left == expected.WorkingArea.Left && bounds.Top == expected.WorkingArea.Top
                && bounds.Right - bounds.Left == expected.WorkingArea.Width,
                "实际 Win32 窗口与当前屏幕工作区对齐");
            if (!WindowsNative.FullScreen(expected, shelf.Handle))
            {
                using (var events = new WindowsEvents())
                {
                    Console.WriteLine("实际 Ctrl+Alt+T 注册：" + (events.ShortcutAvailable ? "成功" : "被其他程序占用"));
                    events.ToggleRequested += shelf.Toggle;
                    IntPtr handle = ((HwndSource)typeof(WindowsEvents).GetField("source", instanceFlags).GetValue(events)).Handle;
                    shelf.Reveal(false, false, 0);
                    Check(PostMessage(handle, 0x0312, new IntPtr(1), new IntPtr((84 << 16) | 3)), "向实际消息窗口投递快捷键消息");
                    await Task.Delay(80);
                    Check(shelf.Revealed, "实际快捷键消息接通展开入口");
                    PostMessage(handle, 0x0312, new IntPtr(1), new IntPtr((84 << 16) | 3));
                    await Task.Delay(80);
                    Check(!shelf.Revealed, "实际快捷键消息再次触发后收起");
                }
            }
            else Console.WriteLine("跳过实际展开检查：当前前台窗口处于全屏状态");
            shelf.Close(); shelf = null;
        }

        static async Task Scenario(string name, System.Drawing.Rectangle bounds, double scale, bool topTaskbar)
        {
            scenarios++;
            int before = failures.Count;
            FullScreens.Clear(); Positions.Clear(); Scales.Clear(); MouseDown = false; CaptionButtons = false;
            var main = Screen("SIM-MAIN", new System.Drawing.Rectangle(0, 0, 1920, 1080), new System.Drawing.Rectangle(0, 0, 1920, 1040), true);
            var work = new System.Drawing.Rectangle(bounds.Left, bounds.Top + (topTaskbar ? 48 : 0), bounds.Width, bounds.Height - (topTaskbar ? 48 : 40));
            var secondary = Screen("SIM-SECONDARY", bounds, work, false);
            Screens = new[] { main, secondary }; Scales[main.DeviceName] = 1; Scales[secondary.DeviceName] = scale;
            Pointer = new WindowsNative.Point { X = 600, Y = 400 };
            var gallery = Open();
            shelf.SetRevealOnTopHover(false);
            string label = name + " " + scale * 100 + "%：";
            Check(shelf.Display == main, label + "从主屏启动");
            Pointer = new WindowsNative.Point { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + 200 };
            shelf.Height = 110;
            shelf.Toggle();
            Check(shelf.Revealed && shelf.Display == secondary, label + "收起状态下调用快捷键入口，在副屏展开");
            var positioned = Positions[shelf.Handle];
            Check(positioned.Left == work.Left && positioned.Top == work.Top && positioned.Right - positioned.Left == work.Width
                && positioned.Bottom - positioned.Top == (int)Math.Round(220 * scale)
                && Math.Abs(shelf.Width - work.Width / scale) < 0.01 && Math.Abs(shelf.Height - 220) < 0.01,
                label + "副屏坐标、工作区与缩放换算正确：" + positioned.Left + "," + positioned.Top + "," + (positioned.Right - positioned.Left) + "×" + (positioned.Bottom - positioned.Top) + "，WPF " + shelf.Width + "×" + shelf.Height);
            if (scenarios == 1 || scenarios == 4)
            {
                var textWindow = new TextWindow(new TextCollection(gallery.Store), shelf) { ShowActivated = false, Opacity = 0 };
                textWindow.SourceInitialized += delegate { textWindow.Width = 230; textWindow.Height = 228; };
                textWindow.Show();
                await Task.Delay(100);
                var textHandle = new WindowInteropHelper(textWindow).Handle;
                WindowsNative.Rectangle textBounds;
                Check(Positions.TryGetValue(textHandle, out textBounds) && Math.Abs(textWindow.Width - 460) < 0.01
                    && textBounds.Right - textBounds.Left == (int)Math.Round(460 * scale),
                    label + "文字面板加载后恢复目标屏幕的尺寸");
                textWindow.Width = 230; textWindow.Height = 228;
                var constructor = typeof(DpiChangedEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0];
                var dpiChange = (RoutedEventArgs)constructor.Invoke(new object[] {
                    new System.Windows.DpiScale(2, 2), new System.Windows.DpiScale(scale, scale), Window.DpiChangedEvent, textWindow });
                textWindow.RaiseEvent(dpiChange); await Task.Delay(100);
                Check(Math.Abs(textWindow.Width - 460) < 0.01 && Positions[textHandle].Right - Positions[textHandle].Left == (int)Math.Round(460 * scale),
                    label + "文字面板收到缩放变化后恢复目标尺寸");
                textWindow.Close();
            }
            shelf.Toggle();
            Check(!shelf.Revealed, label + "再次调用快捷键入口收起");
            await Task.Delay(70);
            Pointer.Y = topTaskbar ? work.Top - 1 : bounds.Top + 1;
            await Task.Delay(430);
            Check(!shelf.Revealed && shelf.Display == secondary, label + "关闭悬停展开后，跟随鼠标模式顶部停留不唤出");
            shelf.SetRevealOnTopHover(true);
            await Task.Delay(100);
            Check(!shelf.Revealed, label + "顶部短暂停留不会立即展开");
            Check(await Until(() => shelf.Revealed && shelf.Display == secondary), label + "实际定时器在顶部停留后展开到副屏");
            Pointer = new WindowsNative.Point { X = 700, Y = 1 };
            Check(await Until(() => !shelf.Revealed || shelf.Display == main), label + "跨屏切换启动");
            Check(!shelf.Revealed && shelf.Display == secondary, label + "跨屏时先在原屏收起，再调整目标屏幕尺寸");
            if (scenarios == 1)
            {
                typeof(ShelfWindow).GetMethod("Messages", instanceFlags).Invoke(shelf, new object[] { shelf.Handle, 0x02E0, IntPtr.Zero, IntPtr.Zero, false });
                await Task.Delay(40);
                Check(!shelf.Revealed && shelf.Display == secondary, label + "收起期间的 DPI 消息不会提前移动窗口");
            }
            Check(await Until(() => shelf.Revealed && shelf.Display == main), label + "展开时可从副屏切回主屏");
            Check(slideAtMove <= -299.9, label + "调整目标屏幕窗口尺寸时图绳已完全收起");
            if (scenarios == 1)
            {
                Pointer = new WindowsNative.Point { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + 1 };
                Check(await Until(() => !shelf.Revealed && shelf.Display == main), label + "再次切屏时先收起");
                Pointer = new WindowsNative.Point { X = 700, Y = 400 };
                Check(await Until(() => shelf.Revealed && shelf.Display == main), label + "收起期间鼠标返回原屏时保持跟随当前屏幕");
            }

            Pointer = new WindowsNative.Point { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + 200 };
            shelf.Reveal(false, false, 0);
            FullScreens.Add(secondary.DeviceName);
            shelf.Toggle();
            Check(!shelf.Revealed, label + "副屏全屏时抑制快捷键展开");
            FullScreens.Clear(); FullScreens.Add(main.DeviceName);
            shelf.Toggle();
            Check(shelf.Revealed && shelf.Display == secondary, label + "主屏全屏不阻止副屏展开");
            FullScreens.Clear();
            Pointer = new WindowsNative.Point { X = 700, Y = 400 };
            await Task.Delay(70);
            Check(shelf.Display == secondary, label + "鼠标经过其他屏幕内部不会带走图绳");

            if (scenarios == 1)
            {
                var menu = new TrayPanel();
                menu.SetScreens(Screens, null, shelf.SetDisplay);
                var choices = menu.ScreenMenu.Items.OfType<System.Windows.Controls.MenuItem>().Where(item => item.IsCheckable).ToArray();
                Check(choices.Length == 3 && choices[0].IsChecked, label + "模拟双屏菜单列出跟随鼠标、主屏和副屏");
                choices[2].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            }
            else shelf.SetDisplay(secondary.DeviceName);
            Check(shelf.Display == secondary && shelf.Revealed
                && new StateStore(gallery.Store.Root).State.DisplayDevice == secondary.DeviceName,
                label + "显式选择副屏后立即显示并保存设置");
            shelf.Reveal(false, false, 0);
            shelf.Toggle();
            Check(shelf.Revealed && shelf.Display == secondary, label + "鼠标在主屏时快捷键仍在固定副屏展开");
            shelf.Reveal(false, false, 0);
            Pointer.Y = 1;
            await Task.Delay(430);
            Check(!shelf.Revealed && shelf.Display == secondary, label + "固定副屏后，主屏顶部不会呼出或带走图绳");
            Pointer = new WindowsNative.Point { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + 1 };
            shelf.SetRevealOnTopHover(false);
            await Task.Delay(430);
            Check(!shelf.Revealed && shelf.Display == secondary, label + "关闭悬停展开后，固定屏幕顶部停留不唤出");
            shelf.SetRevealOnTopHover(true);
            Check(await Until(() => shelf.Revealed && shelf.Display == secondary), label + "固定副屏顶部仍可正常呼出");

            if (scenarios == 1)
            {
                Screens = new[] { main };
                Pointer = new WindowsNative.Point { X = 700, Y = 400 };
                typeof(ShelfWindow).GetMethod("DisplaysChanged", instanceFlags).Invoke(shelf, new object[] { null, EventArgs.Empty });
                await Task.Delay(80);
                Check(shelf.Display == main && gallery.Store.State.DisplayDevice == secondary.DeviceName,
                    label + "拔掉固定副屏后临时回到主屏并保留选择");
                var menu = new TrayPanel();
                menu.SetScreens(Screens, gallery.Store.State.DisplayDevice, shelf.SetDisplay);
                Check(menu.ScreenMode.Text.Contains("原屏幕未连接"), label + "断开副屏时菜单明确提示临时使用主屏");
                Screens = new[] { main, secondary };
                typeof(ShelfWindow).GetMethod("DisplaysChanged", instanceFlags).Invoke(shelf, new object[] { null, EventArgs.Empty });
                await Task.Delay(80);
                Check(shelf.Display == secondary, label + "重新接入固定副屏后自动恢复");
                shelf.Close(); shelf = null;
                shelf = new ShelfWindow(new Gallery(new StateStore(gallery.Store.Root)), delegate { }, delegate { }) { Opacity = 0 };
                shelf.Show();
                Check(shelf.Display == secondary, label + "重启后仍定位到固定副屏");
                shelf.Close(); shelf = null;
                shelf = new ShelfWindow(gallery, delegate { }, delegate { }) { Opacity = 0 };
                shelf.Show();
                string path = Path.Combine(projectRoot, "docs", "assets", "rope-preview.png");
                gallery.Add(path, ImageContent.Read(path));
                shelf.ShowNewCapture(gallery.Items.Single(), null);
                Check(shelf.Revealed && shelf.Display == secondary, label + "鼠标在主屏时新截图仍挂到固定副屏");
                shelf.ShowEditedImage(path, new Rect(500, 300, 300, 200), gallery.Items.Single().Image.Thumbnail);
                Check(shelf.Display == secondary, label + "编辑完成后仍使用设置中的固定副屏");
                foreach (var flight in shelf.MotionWindows.ToArray()) flight.Close();
            }
            Pointer = new WindowsNative.Point { X = 700, Y = 400 };
            shelf.SetDisplay(null);
            Check(shelf.Display == main && gallery.Store.State.DisplayDevice == null
                && new StateStore(gallery.Store.Root).State.DisplayDevice == null,
                label + "切回跟随鼠标后取消固定并保存");

            if (scenarios == 1)
            {
                shelf.Reveal(false, false, 0);
                Pointer = new WindowsNative.Point { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + 1 }; MouseDown = true;
                await Task.Delay(80); MouseDown = false;
                await Task.Delay(430);
                Check(!shelf.Revealed, label + "点击顶部后保持收起，避免立即反弹");
                Pointer.Y += 200; await Task.Delay(70); Pointer.Y = bounds.Top + 1;
                Check(await Until(() => shelf.Revealed), label + "离开后重新进入顶部恢复呼出");
                string path = Path.Combine(projectRoot, "docs", "assets", "rope-preview.png");
                gallery.Add(path, ImageContent.Read(path));
                var item = gallery.Items.Single();
                shelf.ShowEditedImage(path, new Rect(500, 300, 300, 200), item.Image.Thumbnail);
                Check(shelf.Display == secondary, label + "跟随鼠标模式不再由编辑窗口位置覆盖屏幕选择");
                foreach (var flight in shelf.MotionWindows.ToArray()) flight.Close();
                Pointer = new WindowsNative.Point { X = 700, Y = 400 };
                shelf.Reveal(false, false, 0); shelf.Toggle(); shelf.Toggle();
                Pointer = new WindowsNative.Point { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + 200 };
                shelf.ShowNewCapture(item, null);
                Check(shelf.Revealed && shelf.Display == secondary, label + "新截图按鼠标所在副屏呼出");
                Screens = new[] { main }; Pointer = new WindowsNative.Point { X = 700, Y = 400 };
                typeof(ShelfWindow).GetMethod("DisplaysChanged", instanceFlags).Invoke(shelf, new object[] { null, EventArgs.Empty });
                await Task.Delay(80);
                Check(shelf.Display == main, label + "模拟拔除副屏后显示设置处理回到主屏");
            }
            if (scenarios == 1) await CheckHoverTransitions(gallery, main, secondary);
            shelf.Close(); shelf = null;
            Console.WriteLine("{0} {1}%：{2}", name, scale * 100, failures.Count == before ? "通过" : "存在失败");
        }

        static void Tick()
        {
            typeof(ShelfWindow).GetMethod("Tick", instanceFlags).Invoke(shelf, new object[] { null, EventArgs.Empty });
        }

        static async Task CheckHoverTransitions(Gallery gallery, Forms.Screen main, Forms.Screen secondary)
        {
            var timer = (System.Windows.Threading.DispatcherTimer)typeof(ShelfWindow).GetField("pointer", instanceFlags).GetValue(shelf);
            timer.Stop();
            try
            {
                Screens = new[] { main, secondary };
                Pointer = new WindowsNative.Point { X = 700, Y = 1 };
                shelf.Reveal(false, false, 0);
                shelf.SetRevealOnTopHover(true);
                var edge = typeof(ShelfWindow).GetField("edgeSince", instanceFlags);
                edge.SetValue(shelf, DateTime.UtcNow.AddSeconds(-1));
                shelf.SetRevealOnTopHover(false);
                Check((DateTime)edge.GetValue(shelf) == DateTime.MaxValue, "关闭开关清除已有悬停计时");
                shelf.SetRevealOnTopHover(true); Tick();
                Check(!shelf.Revealed, "重新开启后需重新等待，旧计时不会立即触发展开");
                MouseDown = true; Tick(); MouseDown = false; Tick();
                Check(!shelf.Revealed && (DateTime)edge.GetValue(shelf) == DateTime.MaxValue, "按住鼠标拖过顶部和松手不会触发展开");
                Pointer.Y = 400; Tick();
                shelf.SetRevealOnTopHover(false);
                shelf.Toggle();
                Check(shelf.Revealed, "关闭悬停展开后手动入口仍可显示");
                shelf.Reveal(true, false, 0);
                typeof(ShelfWindow).GetField("awaySince", instanceFlags).SetValue(shelf, DateTime.UtcNow.AddSeconds(-2));
                Tick();
                Check(!shelf.Revealed, "关闭悬停展开后离开仍会自动收起");
                shelf.Reveal(true, false, 0);
                Pointer = new WindowsNative.Point { X = 1910, Y = 10 }; CaptionButtons = true; Tick();
                Check(!shelf.Revealed, "关闭悬停展开后仍避让右上角窗口按钮");
                CaptionButtons = false; Pointer = new WindowsNative.Point { X = 700, Y = 80 };
                typeof(ShelfWindow).GetField("hideTime", instanceFlags).SetValue(shelf, 0.3); Tick();
                Check(shelf.Revealed, "关闭悬停展开后临时避让结束仍可恢复");
                FullScreens.Add(main.DeviceName); Tick();
                Check(!shelf.Revealed, "关闭悬停展开后仍遵守全屏隐藏");
                FullScreens.Clear(); Pointer.Y = 400;
                var item = gallery.Items.Single();
                shelf.ShowNewCapture(item, null);
                Check(shelf.Revealed, "关闭悬停展开后新截图仍主动展示");
                shelf.Reveal(false, false, 0);
                shelf.ShowEditedImage(item.Path, new Rect(500, 300, 300, 200), item.Image.Thumbnail);
                Check(shelf.Revealed, "关闭悬停展开后编辑返回仍主动展示");
                foreach (var flight in shelf.MotionWindows.ToArray()) flight.Close();
                shelf.SetRevealOnTopHover(true);
                Pointer = new WindowsNative.Point { X = secondary.Bounds.Left + 700, Y = secondary.Bounds.Top + 1 };
                edge.SetValue(shelf, DateTime.UtcNow.AddSeconds(-1)); Tick();
                Check(!shelf.Revealed, "顶部悬停跨屏切换进入收起阶段");
                typeof(ShelfWindow).GetField("hideTime", instanceFlags).SetValue(shelf, 0.3); Tick();
                shelf.SetRevealOnTopHover(false);
                await Task.Delay(80);
                Check(!shelf.Revealed, "关闭开关取消已排队的跨屏自动展开");
                bool failed = false;
                using (var locked = new FileStream(Path.Combine(gallery.Store.Root, "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                    try { shelf.SetRevealOnTopHover(true); } catch (IOException) { failed = true; }
                Check(failed && !gallery.Store.State.RevealOnTopHover && !new StateStore(gallery.Store.Root).State.RevealOnTopHover,
                    "开关保存失败恢复内存状态并保留磁盘配置");
                Pointer = new WindowsNative.Point { X = 700, Y = 400 };
                shelf.SetRevealOnTopHover(true); shelf.Toggle();
                shelf.SetRevealOnTopHover(false);
                Check(shelf.Revealed, "关闭悬停不收起已由手动入口显示的图绳");
                shelf.SetRevealOnTopHover(true);
                Pointer = new WindowsNative.Point { X = secondary.Bounds.Left + 700, Y = secondary.Bounds.Top + 1 };
                edge.SetValue(shelf, DateTime.UtcNow.AddSeconds(-1)); Tick();
                typeof(ShelfWindow).GetField("hideTime", instanceFlags).SetValue(shelf, 0.3); Tick();
                shelf.SetRevealOnTopHover(false); shelf.Toggle();
                await Task.Delay(80);
                Check(shelf.Revealed && shelf.Display == secondary, "取消排队悬停后立即手动展开，旧回调不干扰手动入口");
                shelf.SetRevealOnTopHover(true);
                var switching = typeof(ShelfWindow).GetField("switchingDisplay", instanceFlags);
                var queued = typeof(ShelfWindow).GetField("displayMoveQueued", instanceFlags);
                var dwell = DateTime.UtcNow.AddMilliseconds(-200);
                edge.SetValue(shelf, dwell); switching.SetValue(shelf, true); queued.SetValue(shelf, true);
                failed = false;
                using (var locked = new FileStream(Path.Combine(gallery.Store.Root, "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                    try { shelf.SetRevealOnTopHover(false); } catch (IOException) { failed = true; }
                Check(failed && gallery.Store.State.RevealOnTopHover && new StateStore(gallery.Store.Root).State.RevealOnTopHover
                    && (DateTime)edge.GetValue(shelf) == dwell && (bool)switching.GetValue(shelf) && (bool)queued.GetValue(shelf),
                    "关闭开关保存失败时保留悬停计时、排队标志和原配置");
                switching.SetValue(shelf, false); queued.SetValue(shelf, false);
                await CheckDisplaySwitchCancellation(main, secondary);
            }
            finally { CaptionButtons = false; FullScreens.Clear(); MouseDown = false; timer.Start(); }
        }

        static void StartDisplaySwitch(Forms.Screen main, Forms.Screen secondary, bool queue)
        {
            MouseDown = false; CaptionButtons = false; FullScreens.Clear(); shelf.Capturing = false;
            Pointer = new WindowsNative.Point { X = 700, Y = 400 };
            shelf.SetRevealOnTopHover(true); shelf.Reveal(false, false, 0); shelf.Toggle();
            Pointer = new WindowsNative.Point { X = secondary.Bounds.Left + 700, Y = secondary.Bounds.Top + 1 };
            typeof(ShelfWindow).GetField("edgeSince", instanceFlags).SetValue(shelf, DateTime.UtcNow.AddSeconds(-1)); Tick();
            if (queue) { typeof(ShelfWindow).GetField("hideTime", instanceFlags).SetValue(shelf, 0.3); Tick(); }
        }

        static async Task CheckDisplaySwitchCancellation(Forms.Screen main, Forms.Screen secondary)
        {
            StartDisplaySwitch(main, secondary, false);
            MouseDown = true; Tick(); MouseDown = false; Tick();
            typeof(ShelfWindow).GetField("hideTime", instanceFlags).SetValue(shelf, 0.3); Tick(); await Task.Delay(80);
            Check(!shelf.Revealed && (bool)typeof(ShelfWindow).GetField("suppressEdge", instanceFlags).GetValue(shelf),
                "跨屏收起期间顶部短点击会取消展开，松手后保持抑制");
            Pointer.Y += 200; Tick();

            StartDisplaySwitch(main, secondary, true);
            shelf.Reveal(false, false, 0); await Task.Delay(80);
            Check(!shelf.Revealed, "显式收起使已排队跨屏展开失效");

            StartDisplaySwitch(main, secondary, true);
            shelf.Capturing = true; shelf.Reveal(false, false, 0); await Task.Delay(80);
            Check(!shelf.Revealed && shelf.Capturing, "截图开始后旧回调不会重新展开图绳"); shelf.Capturing = false;

            StartDisplaySwitch(main, secondary, true);
            MouseDown = true; await Task.Delay(80);
            Check(!shelf.Revealed, "回调执行前按住鼠标会取消排队展开");
            MouseDown = false; Pointer.Y += 200; Tick();

            StartDisplaySwitch(main, secondary, true);
            shelf.SetRevealOnTopHover(false); shelf.SetRevealOnTopHover(true); shelf.Toggle();
            Pointer = new WindowsNative.Point { X = 700, Y = 1 };
            typeof(ShelfWindow).GetField("edgeSince", instanceFlags).SetValue(shelf, DateTime.UtcNow.AddSeconds(-1)); Tick();
            await Task.Delay(80);
            Check(!shelf.Revealed && shelf.Display == secondary,
                "取消后的旧回调不能提前完成新一轮跨屏收起");
            typeof(ShelfWindow).GetField("hideTime", instanceFlags).SetValue(shelf, 0.3); Tick(); await Task.Delay(80);
            Check(shelf.Revealed && shelf.Display == main, "新一轮跨屏回调仍可正常完成");

            StartDisplaySwitch(main, secondary, true);
            FullScreens.Add(secondary.DeviceName); await Task.Delay(80);
            Check(!shelf.Revealed, "排队展开执行时仍遵守目标屏幕全屏限制"); FullScreens.Clear();
            Pointer = new WindowsNative.Point { X = 700, Y = 400 };
            shelf.Reveal(false, false, 0); shelf.Toggle(); FullScreens.Add(main.DeviceName);
            Pointer = new WindowsNative.Point { X = secondary.Bounds.Left + 700, Y = secondary.Bounds.Top + 1 };
            typeof(ShelfWindow).GetField("edgeSince", instanceFlags).SetValue(shelf, DateTime.UtcNow.AddSeconds(-1)); Tick();
            typeof(ShelfWindow).GetField("hideTime", instanceFlags).SetValue(shelf, 0.3); Tick(); await Task.Delay(80);
            Check(shelf.Revealed && shelf.Display == secondary, "原屏幕全屏不阻止非全屏目标屏幕的悬停展开"); FullScreens.Clear();
            StartDisplaySwitch(main, secondary, false);
            shelf.Toggle();
            Check(shelf.Revealed && shelf.Display == secondary && !switchingAtMove,
                "手动展开先取消跨屏等待，移动窗口时不屏蔽 DPI 校正");
        }

        static async Task<bool> Until(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(30);
            return condition();
        }

        static void Check(bool condition, string message)
        {
            checks++;
            if (!condition) { failures.Add(message); Console.WriteLine("未通过：" + message); }
        }
    }
}
