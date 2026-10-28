using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("PicRope · 图绳")]
[assembly: AssemblyProduct("PicRope")]
[assembly: AssemblyDescription("图绳：把截图挂在屏幕上沿，随手复制、拖拽和保存。")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]
[assembly: AssemblyInformationalVersion("1.2.0")]

namespace ScreenLine
{
    internal sealed class DesktopController : IDisposable
    {
        public readonly Gallery Gallery;
        public readonly CapturePipeline Captures;
        public readonly TextCollection Texts;
        public readonly ShelfWindow Shelf;
        readonly WindowsEvents events;
        readonly Forms.NotifyIcon tray = new Forms.NotifyIcon();
        LightNotice notice;
        readonly CheckBox collect;
        readonly CheckBox collectText;
        readonly CheckBox autoHang;
        readonly CheckBox watchFolder;
        internal readonly TrayPanel TrayMenu;
        internal AppearanceWindow Appearance { get; private set; }
        internal BasketWindow Basket { get; private set; }
        internal TextWindow TextWindow { get; private set; }
        internal CaptureWindow Capture { get; private set; }
        internal ShortcutWindow ShortcutSettings { get; private set; }
        internal WatchFolderWindow WatchManager { get; private set; }
        readonly MenuItem captureMenu;
        internal readonly List<ImageEditorWindow> Editors = new List<ImageEditorWindow>();
        readonly AppUpdates updates = new AppUpdates();
        readonly CancellationTokenSource updateLifetime = new CancellationTokenSource();
        readonly DispatcherTimer updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        readonly MenuItem updateMenu;
        readonly bool automaticUpdates;
        UpdateRelease availableUpdate;
        UpdateWindow updateWindow;
        bool checkingUpdates, disposed;
        uint textClipboardSequence;
        int textClipboardVersion;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public DesktopController(StateStore store, string screenshots, bool automaticUpdates = true)
        {
            this.automaticUpdates = automaticUpdates;
            Gallery = new Gallery(store);
            Texts = new TextCollection(store);
            Captures = new CapturePipeline(Gallery, Application.Current.Dispatcher, screenshots);
            Shelf = new ShelfWindow(Gallery, Act, EnableClipboard, Captures.IsWatchedPath);
            Shelf.BasketRequested += ShowBasket;
            Shelf.TextRequested += ShowTexts;
            Texts.Changed += Shelf.UpdateTexts;
            events = new WindowsEvents();
            events.RegisterCapture(store.State.CaptureModifiers, store.State.CaptureKey);
            events.CaptureRequested += ShowCapture;
            events.ToggleRequested += Shelf.Toggle;
            events.ExitRequested += Exit;
            events.ClipboardChanged += Captures.ClipboardChanged;
            textClipboardSequence = WindowsNative.GetClipboardSequenceNumber();
            events.ClipboardChanged += CollectClipboardText;
            Captures.Arrived += Shelf.ShowNewCapture;
            Captures.Failed += Notice;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PicRope.Icon"))
                tray.Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            tray.Text = "PicRope · 图绳";
            TrayMenu = new TrayPanel();
            SyncScreens();
            TrayMenu.Opened += delegate { SyncScreens(); };
            TrayMenu.ScreenMenu.Opened += delegate { SyncScreens(); };
            TrayMenu.OnAction(TrayMenu.VisibilityButton, delegate { Shelf.Toggle(); });
            if (events.ShortcutAvailable) TrayMenu.Shortcut.Text = "Ctrl+Alt+T";
            else Notice("Ctrl+Alt+T 已被占用，可点击托盘图标打开。");
            TrayMenu.OnAction(TrayMenu.BasketButton, ShowBasket);
            TrayMenu.OnAction(TrayMenu.ManageWatchButton, ShowWatchFolders);
            TrayMenu.OnAction(TrayMenu.TextButton, ShowTexts);
            autoHang = TrayMenu.AutoHang;
            TrayMenu.OnToggle(autoHang, Captures.AutoHang, delegate(bool value) { if (Captures.AutoHang != value) Captures.AutoHang = value; });
            Gallery.Changed += SyncAutoHang;
            Gallery.Changed += SyncWatchFolders;
            watchFolder = TrayMenu.WatchFolder;
            TrayMenu.OnToggle(watchFolder, Captures.WatchFoldersEnabled, delegate(bool value)
            {
                if (Captures.WatchFoldersEnabled == value) return;
                // 列表为空时不直接开启，先打开管理窗口引导添加目录，添加后自动开启。
                if (value && Captures.WatchFolders.Count == 0) { SyncWatchFolders(); ShowWatchFolders(); return; }
                if (value) EnableWatchFolders(); else DisableWatchFolders();
            });
            collect = TrayMenu.Pictures;
            TrayMenu.OnToggle(collect, Captures.CollectClipboard, delegate(bool value) { Captures.CollectClipboard = value; Shelf.Update(); });
            collectText = TrayMenu.Text;
            TrayMenu.OnToggle(collectText, Texts.CollectClipboard, delegate(bool value)
            {
                textClipboardVersion++;
                textClipboardSequence = WindowsNative.GetClipboardSequenceNumber();
                Texts.CollectClipboard = value;
            });
            TrayMenu.OnAction(TrayMenu.AppearanceButton, ShowAppearance);
            var more = TrayMenu.MoreMenu;
            MenuItem previewToggle = null;
            previewToggle = more.AddAction("悬停预览", "picture", delegate
            {
                bool previous = store.State.HoverPreview;
                store.State.HoverPreview = previewToggle.IsChecked;
                try { store.Save(); Shelf.Preview.SetEnabled(store.State.HoverPreview); }
                catch (Exception error) { store.State.HoverPreview = previous; previewToggle.IsChecked = previous; store.Log(error); Notice("预览设置保存失败"); }
            });
            previewToggle.IsCheckable = true;
            previewToggle.IsChecked = store.State.HoverPreview;
            captureMenu = more.AddAction("区域截图", "capture", ShowCapture);
            UpdateCaptureLabel();
            more.AddAction("截图快捷键…", "shortcut", delegate
            {
                if (ShortcutSettings != null) { ShortcutSettings.Activate(); return; }
                ShortcutSettings = new ShortcutWindow(store.State.CaptureModifiers, store.State.CaptureKey, SetCaptureShortcut);
                ShortcutSettings.Closed += delegate { ShortcutSettings = null; }; ShortcutSettings.Show();
            });
            more.Separator();
            more.AddAction("添加图片…", "add", async delegate
            {
                var picker = new OpenFileDialog { Title = "添加图片", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff", Multiselect = true };
                if (picker.ShowDialog() != true) return;
                foreach (string file in picker.FileNames)
                    if (!await Captures.Import(file, null, true)) Notice("无法读取图片：" + Path.GetFileName(file));
            });
            more.AddAction("打开收集文件夹", "folder", delegate
            {
                try { WindowsShell.OpenInbox(store); }
                catch (Exception error) { store.Log(error); Notice("无法打开收集文件夹：" + error.Message); }
            });
            more.AddAction("全部取下（保留文件）", "clear", delegate { Gallery.Clear(); });
            more.Separator();
            MenuItem startup = null;
            startup = more.AddAction("开机启动", "startup", delegate
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (startup.IsChecked) key.SetValue("PicRope", Quote(Assembly.GetEntryAssembly().Location));
                    else key.DeleteValue("PicRope", false);
                }
            });
            startup.IsCheckable = true;
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) startup.IsChecked = key != null && key.GetValue("PicRope") != null;
            updateMenu = more.AddAction("检查更新…", "save", async delegate { await CheckForUpdates(true); });
            updateMenu.InputGestureText = "v" + AppUpdates.CurrentVersion;
            updateTimer.Tick += async delegate { updateTimer.Interval = TimeSpan.FromHours(12); await CheckForUpdates(false); };
            tray.BalloonTipClicked += delegate { ShowUpdate(); };
            TrayMenu.OnAction(TrayMenu.ExitButton, Exit);
            tray.MouseUp += delegate(object sender, Forms.MouseEventArgs args) { if (args.Button == Forms.MouseButtons.Right) ShowTrayMenu(); };
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs args) { if (args.Button == Forms.MouseButtons.Left) Shelf.Toggle(); };
            tray.Visible = true;
        }

        async void Exit()
        {
            foreach (var editor in Editors.ToArray()) if (!await editor.ConfirmClose()) return;
            Application.Current.Shutdown();
        }

        public async Task Start()
        {
            Shelf.Show();
            Shelf.Reveal(true, false, 4);
            await Captures.Start();
            if (automaticUpdates) updateTimer.Start();
        }

        void UpdateCaptureLabel()
        {
            captureMenu.InputGestureText = events.CaptureShortcutAvailable ? ShortcutWindow.Label(Gallery.Store.State.CaptureModifiers, Gallery.Store.State.CaptureKey) : "快捷键被占用";
        }

        internal bool SetCaptureShortcut(uint modifiers, uint key)
        {
            if (!events.RegisterCapture(modifiers, key)) return false;
            uint previousModifiers = Gallery.Store.State.CaptureModifiers, previousKey = Gallery.Store.State.CaptureKey;
            Gallery.Store.State.CaptureModifiers = modifiers; Gallery.Store.State.CaptureKey = key;
            try { Gallery.Store.Save(); }
            catch (Exception error)
            {
                Gallery.Store.State.CaptureModifiers = previousModifiers; Gallery.Store.State.CaptureKey = previousKey;
                events.RegisterCapture(previousModifiers, previousKey); Gallery.Store.Log(error); Notice("无法保存快捷键，请重试。"); return false;
            }
            UpdateCaptureLabel(); return true;
        }

        internal async void ShowCapture()
        {
            if (disposed || Shelf.Capturing) return;
            Shelf.Capturing = true;
            var display = WindowsNative.ScreenAtPointer();
            bool revealed = Shelf.Revealed;
            try
            {
                TrayMenu.Close(); if (Basket != null) Basket.Close(); if (TextWindow != null) TextWindow.Close();
                if (WatchManager != null) WatchManager.Close();
                if (Appearance != null) Appearance.Close(); if (ShortcutSettings != null) ShortcutSettings.Close();
                Shelf.Reveal(false, false, 0); await Task.Delay(260);
                if (disposed) return;
                Capture = new CaptureWindow(display);
                var result = await Capture.PickAsync(); Capture = null;
                if (disposed) return;
                if (result != null) await Captures.AcceptCapture(result);
                else if (revealed) Shelf.Reveal(true, false, 2);
            }
            catch (Exception error) { if (!disposed) { Gallery.Store.Log(error); Notice("截图未完成，请重试。"); } }
            finally { if (Capture != null) Capture.Close(); Capture = null; Shelf.Capturing = false; }
        }

        async Task CheckForUpdates(bool manual)
        {
            if (disposed || checkingUpdates) return;
            if (manual && availableUpdate != null) { ShowUpdate(); return; }
            checkingUpdates = true; updateMenu.IsEnabled = false; updateMenu.Header = "正在检查更新…";
            try
            {
                availableUpdate = await updates.CheckAsync(updateLifetime.Token);
                if (disposed) return;
                if (availableUpdate == null) { if (manual) Notice("当前已是最新版 " + AppUpdates.CurrentVersion + "。"); }
                else if (manual) ShowUpdate();
                else if (Gallery.Store.State.LastUpdateNotification != availableUpdate.Version)
                {
                    tray.ShowBalloonTip(10000, "图绳有新版本 " + availableUpdate.Version, "点击查看更新并下载安装。", Forms.ToolTipIcon.Info);
                    Gallery.Store.State.LastUpdateNotification = availableUpdate.Version; Gallery.Store.Save();
                }
            }
            catch (Exception error)
            {
                if (!disposed && manual) Notice("暂时无法检查更新，请检查网络后重试。" + (error is System.IO.InvalidDataException ? "\n" + error.Message : ""));
            }
            finally
            {
                checkingUpdates = false; updateMenu.IsEnabled = true;
                updateMenu.Header = availableUpdate == null ? "检查更新…" : "更新到 " + availableUpdate.Version + "…";
            }
        }

        void ShowUpdate()
        {
            if (disposed || availableUpdate == null) return;
            if (updateWindow != null) { updateWindow.Activate(); return; }
            updateWindow = new UpdateWindow(availableUpdate, updates, async delegate(string work)
            {
                foreach (var editor in Editors.ToArray()) if (!await editor.ConfirmClose()) return false;
                AppUpdates.Install(work);
                Application.Current.Shutdown();
                return true;
            });
            updateWindow.Closed += delegate { updateWindow = null; };
            updateWindow.Show();
        }

        void EnableClipboard()
        {
            Captures.CollectClipboard = true;
            collect.IsChecked = true;
        }

        void SyncAutoHang() { autoHang.IsChecked = Captures.AutoHang; }

        void EnableWatchFolders()
        {
            try { Captures.EnableWatchFolders(); }
            catch (Exception error)
            {
                Gallery.Store.Log(error);
                Notice("监控设置保存失败，请重试。");
            }
            SyncWatchFolders();
        }

        void DisableWatchFolders()
        {
            try { Captures.DisableWatchFolders(); }
            catch (Exception error)
            {
                Gallery.Store.Log(error);
                Notice("监控设置保存失败，请重试。");
            }
            SyncWatchFolders();
        }

        void SyncWatchFolders()
        {
            watchFolder.IsChecked = Captures.WatchFoldersEnabled;
            TrayMenu.WatchStatus.Text = WatchFolderRules.Summarize(Captures.WatchFolders);
        }

        internal void ShowWatchFolders()
        {
            if (WatchManager != null) { WatchManager.Activate(); return; }
            WatchManager = new WatchFolderWindow(Captures, Gallery);
            WatchManager.Owner = Shelf;
            WatchManager.Closed += delegate { WatchManager = null; };
            WatchManager.Show();
        }

        void SyncScreens()
        {
            TrayMenu.SetScreens(WindowsNative.Screens, Gallery.Store.State.DisplayDevice, SelectScreen);
        }

        void SelectScreen(string device)
        {
            TrayMenu.Close();
            try { Shelf.SetDisplay(device); }
            catch (Exception error) { Gallery.Store.Log(error); Notice("无法保存屏幕设置：" + error.Message); }
            SyncScreens();
        }

        async void CollectClipboardText()
        {
            uint sequence = WindowsNative.GetClipboardSequenceNumber();
            if (disposed || !Texts.CollectClipboard || sequence == textClipboardSequence) return;
            textClipboardSequence = sequence;
            int version = textClipboardVersion;
            try
            {
                string content = await WindowsClipboard.ReadText(sequence);
                if (disposed || !Texts.CollectClipboard || version != textClipboardVersion || sequence != WindowsNative.GetClipboardSequenceNumber()) return;
                Texts.Add(content);
            }
            catch (Exception error) { if (!disposed) Gallery.Store.Log(error); }
        }

        internal void ShowBasket()
        {
            if (Basket != null) { Basket.Activate(); return; }
            Gallery.Prune();
            Basket = new BasketWindow(Gallery, delegate(bool value) { Captures.AutoHang = value; }, Captures.Hang, Act);
            Basket.Owner = Shelf;
            Shelf.ChoosingBasket = true;
            Shelf.Reveal(true, false, 0);
            Basket.Closed += delegate { Basket = null; Shelf.ChoosingBasket = false; };
            Basket.Show();
        }

        internal void ShowTexts()
        {
            if (TextWindow != null) { TextWindow.Close(); return; }
            Shelf.Reveal(true, false, 0);
            Shelf.ChoosingTexts = true;
            TextWindow = new TextWindow(Texts, Shelf);
            TextWindow.Closed += delegate { TextWindow = null; Shelf.ChoosingTexts = false; };
            TextWindow.Show();
        }

        void ShowAppearance()
        {
            if (Appearance != null) { Appearance.Activate(); return; }
            Appearance = new AppearanceWindow(Shelf.Appearance, Gallery, delegate(LineTheme theme)
            {
                Gallery.Store.State.Theme = theme.Id;
                Gallery.Store.Save();
                Shelf.ApplyTheme(theme);
                Shelf.Reveal(true, false, 3);
            });
            Appearance.Owner = Shelf;
            Shelf.ChoosingAppearance = true;
            Shelf.Reveal(true, false, 0);
            Appearance.Closed += delegate { Appearance = null; Shelf.ChoosingAppearance = false; };
            Appearance.Show();
        }

        internal void ShowTrayMenu()
        {
            WindowsNative.SetForegroundWindow(Shelf.Handle);
            var cursor = WindowsNative.Cursor();
            var point = Shelf.PointFromScreen(new Point(cursor.X, cursor.Y));
            TrayMenu.PlacementTarget = Shelf;
            TrayMenu.Placement = PlacementMode.RelativePoint;
            TrayMenu.HorizontalOffset = point.X - 16;
            TrayMenu.VerticalOffset = point.Y - 16;
            TrayMenu.IsOpen = true;
        }

        void Notice(string message)
        {
            if (notice != null) notice.Close();
            notice = new LightNotice(message);
            var current = notice;
            current.Closed += delegate { if (notice == current) notice = null; };
            notice.Show();
        }

        static string Quote(string path) { return "\"" + path + "\""; }

        public async void Act(ShotAction action, CaptureItem item)
        {
            try
            {
                if (!File.Exists(item.Path)) { Gallery.Prune(); return; }
                switch (action)
                {
                    case ShotAction.Copy:
                        var payload = new DataObject();
                        byte[] png;
                        using (var stream = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                            payload.SetImage(image);
                            png = ImageContent.EncodeClipboard(image);
                        }
                        payload.SetData("PNG", new MemoryStream(png));
                        payload.SetFileDropList(new StringCollection { item.Path });
                        payload.SetData(ClipboardRules.OwnFormat, "1");
                        await WindowsClipboard.Write(payload);
                        Shelf.ShowCopied(item.Path);
                        break;
                    case ShotAction.Open:
                        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                        Shelf.FollowImageViewer(item.Path);
                        break;
                    case ShotAction.Edit:
                        var existing = Editors.Find(editor => FileRules.SameFile(editor.ImagePath, item.Path));
                        if (existing != null)
                        {
                            existing.Present();
                            break;
                        }
                        var window = new ImageEditorWindow(item.Path, Gallery.Store, RefreshEditedImage, Captures.IgnoreCurrentClipboard);
                        Editors.Add(window);
                        window.Closed += delegate
                        {
                            Editors.Remove(window);
                            if (!window.ReturnBounds.IsEmpty) Shelf.ShowEditedImage(window.ImagePath, window.ReturnBounds, window.ReturnPicture);
                        };
                        Shelf.Reveal(false, false, 0);
                        window.Present();
                        break;
                    case ShotAction.Save:
                        var dialog = new SaveFileDialog { Title = "保存副本", FileName = Path.GetFileName(item.Path), Filter = "图片|*" + Path.GetExtension(item.Path), OverwritePrompt = true };
                        if (dialog.ShowDialog() == true && !FileRules.SameFile(item.Path, dialog.FileName)) File.Copy(item.Path, dialog.FileName, true);
                        break;
                    case ShotAction.Locate:
                        Process.Start("explorer.exe", "/select," + Quote(item.Path));
                        break;
                    case ShotAction.Remove:
                        Shelf.ShowCollect(item);
                        Gallery.Remove(item);
                        break;
                    case ShotAction.Trash:
                        if (Captures.IsWatchedPath(item.Path))
                        {
                            // 监控文件夹中的原文件只读：删除仅下架记录，不动源文件。
                            if (Gallery.Items.Contains(item)) { Shelf.ShowCollect(item); Gallery.Remove(item); }
                            else Gallery.Forget(item);
                            break;
                        }
                        FileSystem.DeleteFile(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                        Shelf.ShowDiscard(item);
                        Gallery.Remove(item);
                        Gallery.Prune();
                        break;
                }
            }
            catch (Exception error) { Gallery.Store.Log(error); Notice("操作未完成：" + error.Message); }
        }

        public void Dispose()
        {
            disposed = true; updateTimer.Stop(); updateLifetime.Cancel();
            if (Capture != null) Capture.Close();
            if (ShortcutSettings != null) ShortcutSettings.Close();
            if (updateWindow != null) updateWindow.Close();
            updates.Dispose();
            if (Basket != null) Basket.Close();
            if (TextWindow != null) TextWindow.Close();
            if (Appearance != null) Appearance.Close();
            if (WatchManager != null) WatchManager.Close();
            if (notice != null) notice.Close();
            foreach (var editor in Editors.ToArray()) editor.CloseWithoutPrompt();
            TrayMenu.Close();
            Gallery.Changed -= SyncAutoHang;
            Gallery.Changed -= SyncWatchFolders;
            Texts.Changed -= Shelf.UpdateTexts;
            Captures.Dispose();
            events.Dispose();
            tray.Visible = false;
            var icon = tray.Icon;
            tray.Dispose();
            icon.Dispose();
            Shelf.Close();
        }

        async Task RefreshEditedImage(string path)
        {
            ImageContent image = await Task.Run(() => ImageContent.Read(path));
            var item = Gallery.Items.Find(candidate => FileRules.SameFile(candidate.Path, path));
            if (item == null) Gallery.Add(path, image);
            else
            {
                item.Image = image;
                item.Written = File.GetLastWriteTimeUtc(path);
                Gallery.Notify();
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        public static void Main(string[] arguments)
        {
            if (Array.IndexOf(arguments, "--quit") >= 0) { WindowsNative.RequestExit(); return; }
            bool first;
            using (var instance = new Mutex(true, @"Local\TangHao.ScreenLine.Windows", out first))
            {
                if (!first) { WindowsNative.ShowExisting(); return; }
                var store = new StateStore(StateStore.DefaultRoot);
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                LightTheme.Install(app);
                DesktopController controller = null;
                app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args) { store.Log(args.Exception); args.Handled = true; };
                app.Startup += async delegate { controller = new DesktopController(store, WindowsNative.ScreenshotFolder()); await controller.Start(); };
                app.Exit += delegate { if (controller != null) controller.Dispose(); };
                app.Run();
            }
        }
    }
}
