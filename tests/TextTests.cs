using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace ScreenLine.Tests
{
    internal static class TextTests
    {
        static int checks;
        static DesktopController controller;
        static TextWindow window;
        static Forms.IDataObject previous;
        static uint lastSequence;
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(WindowsNative.Point point);

        [STAThread]
        public static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            LightTheme.Install(app);
            app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
            {
                Console.WriteLine("文字功能异常：" + e.Exception); e.Handled = true; app.Shutdown(1);
            };
            app.Startup += async delegate
            {
                try { await Run(args.Contains("--selection-only")); Console.WriteLine("文字功能测试：{0} 项通过", checks); }
                catch (Exception error) { Console.WriteLine("文字功能测试失败：" + error); Environment.ExitCode = 1; }
                finally
                {
                    if (window != null) window.Close();
                    if (controller != null) controller.Dispose();
                    if (lastSequence != 0 && WindowsNative.GetClipboardSequenceNumber() == lastSequence)
                    {
                        if (previous == null) Forms.Clipboard.Clear();
                        else Forms.Clipboard.SetDataObject(previous, true, 8, 60);
                    }
                    app.Shutdown(Environment.ExitCode);
                }
            };
            app.Run();
        }

        static async Task Run(bool selectionOnly)
        {
            var original = Forms.Clipboard.GetDataObject();
            if (original != null)
            {
                var snapshot = new Forms.DataObject();
                foreach (string format in original.GetFormats(false))
                {
                    object value = original.GetData(format, false);
                    var stream = value as MemoryStream; if (stream != null) value = new MemoryStream(stream.ToArray());
                    var image = value as System.Drawing.Image; if (image != null) value = new System.Drawing.Bitmap(image);
                    if (value != null) snapshot.SetData(format, false, value);
                }
                previous = snapshot;
            }
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "texts-" + Guid.NewGuid().ToString("N"));
            string watched = Path.Combine(root, "screenshots"); Directory.CreateDirectory(watched);
            var store = new StateStore(Path.Combine(root, "profile"));
            controller = new DesktopController(store, watched, false);
            await controller.Start();
            var shelf = controller.Shelf;
            shelf.Reveal(true, true, 60); shelf.ChoosingTexts = true;
            await Task.Delay(500);
            Open();
            if (selectionOnly) { await CheckSingleSelection(); return; }
            Check(window.VisibleCount == 0 && AutomationProperties.GetItemStatus(shelf.TextButton) == "0 条文字", "初始为空，文字入口数量为零");
            Check(!shelf.TextButton.IsVisible && !shelf.BasketButton.IsVisible, "收集默认关闭时隐藏信封和篮子");

            var textSwitch = controller.TrayMenu.Text;
            Check(textSwitch.IsChecked == false, "托盘提供独立的文字剪贴板收集开关，默认关闭");
            Check(!window.SelectAllButton.IsEnabled && !window.DeleteSelectedButton.IsEnabled, "空列表禁用全选和批量删除");
            await WriteText("尚未开启文字收集时复制的内容"); await Task.Delay(180);
            Check(controller.Texts.Items.Count == 0, "开关关闭时复制文字不会收集");
            textSwitch.IsChecked = true;
            await Task.Delay(180);
            Check(controller.Texts.Items.Count == 0 && !controller.Captures.CollectClipboard, "开启文字收集不补录旧剪贴板，也不改变截图收集开关");

            string content = "  明天拍摄前记得确认：\r\n\t领夹麦电量、相机存储卡，还有桌面的补光。  ";
            await WriteText(content); await Until(() => controller.Texts.Items.Count == 1);
            Check(controller.Texts.Items.Single().Content == content && window.VisibleCount == 1, "复制后自动进入信封，完整保留缩进、换行和首尾空格");
            Check(AutomationProperties.GetItemStatus(shelf.TextButton) == "1 条文字" && new StateStore(store.Root).State.Texts.Single().Content == content,
                "自动收集后同步入口数量和本地保存");
            await WriteText(content); await Task.Delay(180);
            Check(controller.Texts.Items.Count == 1, "重复复制相同内容不会新增记录");
            await WriteText(" \r\n\t"); await Task.Delay(180);
            Check(controller.Texts.Items.Count == 1, "空白剪贴板不会创建记录");
            var imageData = new DataObject(); imageData.SetImage(ImageContent.Decode(File.ReadAllBytes(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "docs", "assets", "rope-preview.png"))).Thumbnail);
            imageData.SetText("图片附带的文件说明", TextDataFormat.UnicodeText);
            await WindowsClipboard.Write(imageData); lastSequence = WindowsNative.GetClipboardSequenceNumber(); await Task.Delay(180);
            Check(controller.Texts.Items.Count == 1, "图片附带的说明文字不会误收进信封");
            await WriteText("好的工具应该减少打断，让临时保存的内容，在需要时随手就能找到。");
            await Until(() => controller.Texts.Items.Count == 2);
            Check(window.VisibleCount == 2, "新的文字自动排在列表前面");
            var first = controller.Texts.Items[0];
            window.List.UpdateLayout();
            var firstRow = (ListBoxItem)window.List.ItemContainerGenerator.ContainerFromIndex(0);
            var copy = Children<Button>(firstRow).Single(button => (string)button.Tag == "copy");
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Clipboard.ContainsText() && Clipboard.GetText() == first.Content);
            lastSequence = WindowsNative.GetClipboardSequenceNumber();
            await Task.Delay(120);
            Check(controller.Texts.Items.Count == 2 && window.Feedback == "已复制", "行内复制写回完整文字，复制不会再次收集自己");
            window.Search.Text = "相机存储卡";
            Check(window.VisibleCount == 1 && ((TextItem)window.List.Items[0]).Content == content, "输入搜索词只显示正文匹配项");
            window.Search.Text = "没有这样的内容";
            Check(window.VisibleCount == 0 && window.Search.IsEnabled, "无结果时仍可继续搜索");
            window.Search.Clear(); window.List.UpdateLayout();
            firstRow = (ListBoxItem)window.List.ItemContainerGenerator.ContainerFromIndex(0);
            Children<Button>(firstRow).Single(button => (string)button.Tag == "delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(controller.Texts.Items.Count == 1 && window.VisibleCount == 1 && !new StateStore(store.Root).State.Texts.Any(item => item.Id == first.Id),
                "删除单条后即时刷新列表、数量和持久化记录，保留未删除项");

            var longItem = controller.Texts.Add(string.Join("\n", Enumerable.Repeat("长文字完整保留，可以选中阅读并复制原文。", 200)));
            window.List.SelectedItem = longItem; window.UpdateLayout();
            var longRow = (ListBoxItem)window.List.ItemContainerGenerator.ContainerFromIndex(0);
            var reader = Children<TextBox>(longRow).Single();
            Check(reader.IsVisible && reader.ActualHeight <= 220.1 && reader.Text == longItem.Content, "选中长文字可在行内滚动阅读，不撑坏整个面板");

            controller.Texts.Remove(longItem);
            controller.Texts.Add("会议链接和参考资料：https://example.com/notes");
            controller.Texts.Add("片头先展示截图整理前后的对比，再介绍操作。");
            controller.Texts.Add("好的工具应该减少打断，让临时保存的内容，在需要时随手就能找到。");
            store.State.Texts = new List<TextItem> { controller.Texts.Items.Last(), controller.Texts.Items[0], controller.Texts.Items[2], controller.Texts.Items[1] };
            for (int i = 0; i < 4; i++) store.State.Texts[i].CreatedUtc = DateTime.UtcNow.AddMinutes(-i * 30);
            string project = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            foreach (string name in new[] { "rope-preview.png", "picrope-banner.png" })
            {
                string imagePath = Path.Combine(root, name);
                File.Copy(Path.Combine(project, "docs", "assets", name), imagePath);
                controller.Gallery.Add(imagePath, ImageContent.Read(imagePath));
            }
            store.Save(); window.Close(); Open();
            window.List.SelectedItem = null;
            controller.TrayMenu.Pictures.IsChecked = true;
            foreach (var theme in LineTheme.All)
            {
                shelf.ApplyTheme(theme); shelf.UpdateLayout(); window.Position();
                await Task.Delay(80);
                var source = TextEnvelope.Source(theme);
                Check(source.IsFrozen && source.PixelWidth >= 240 && source.PixelWidth <= 384 && source.PixelHeight <= 384, theme.Name + "加载适配高 DPI 的紧凑透明信封素材");
                var clip = Children<ClothesClip>(shelf.TextButton).Single();
                Check(clip.Appearance == theme, theme.Name + "信封挂件与绳子皮肤同步");
                var envelope = Children<Image>(shelf.TextButton).Single();
                var counter = Children<Border>(shelf.TextButton).Single(border => border.Child is TextBlock);
                var envelopeBounds = envelope.TransformToAncestor(shelf.TextButton).TransformBounds(new Rect(envelope.RenderSize));
                var counterBounds = counter.TransformToAncestor(shelf.TextButton).TransformBounds(new Rect(counter.RenderSize));
                Check(counterBounds.Top - envelopeBounds.Bottom >= 5.9, theme.Name + "数量始终在信封下方，不遮住图标");
                Export(theme.Name, shelf, window);
            }

            window.Close();
            var envelopeIcon = Children<EnvelopeIcon>(shelf.TextButton).Single();
            foreach (var theme in LineTheme.All)
            {
                shelf.ApplyTheme(theme); shelf.UpdateLayout();
                var basketImage = Children<Image>(shelf.BasketButton).Single();
                var envelopeBounds = envelopeIcon.TransformToAncestor(shelf).TransformBounds(envelopeIcon.BodyBounds);
                var basketImageBounds = basketImage.TransformToAncestor(shelf).TransformBounds(new Rect(basketImage.RenderSize));
                double gap = basketImageBounds.Left - envelopeBounds.Right;
                Check(envelopeIcon.BodyBounds.Width <= 78.1 && envelopeIcon.BodyBounds.Height <= 86.1 && basketImage.ActualWidth <= 112.1,
                    theme.Name + "保留原图标尺寸，不随悬停放大");
                Check(gap >= 24 && gap <= 34, theme.Name + "信封与篮子保留适中间距：" + gap.ToString("F1"));
                shelf.TextButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                await Task.Delay(420);
                Check(envelopeIcon.Expansion > 0.98 && controller.TextWindow == null, theme.Name + "悬停展开信纸，不自动打开文字列表");
                var drawingBounds = VisualTreeHelper.GetDescendantBounds(envelopeIcon);
                Check(drawingBounds.Left >= 0.8 && drawingBounds.Top >= -8 && drawingBounds.Right <= envelopeIcon.ActualWidth - 0.8
                    && drawingBounds.Bottom <= envelopeIcon.ActualHeight + 0.8, theme.Name + "信纸在入口顶部留白内抽出，两侧不被截断");
                ExportEnvelope(theme.Name + "-展开", shelf);
                shelf.TextButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
                await Task.Delay(450);
                Check(envelopeIcon.Expansion < 0.01, theme.Name + "移开后信纸平滑收回");
            }
            shelf.ApplyTheme(LineTheme.All[0]); shelf.UpdateLayout();
            ExportEnvelope("实际尺寸", shelf);
            for (int i = 0; i < 48; i++)
            {
                if (i == 8) shelf.TextButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                if (i == 30) shelf.TextButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
                ExportEnvelope("frame-" + i.ToString("D2"), shelf);
                await Task.Delay(40);
            }
            shelf.TextButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            await Task.Delay(80);
            double beforeReverse = envelopeIcon.Expansion;
            shelf.TextButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            Check(envelopeIcon.Expansion == beforeReverse, "快速移入移出时，信纸位置连续，不跳回起点");
            await Task.Delay(450);
            var pointerBeforeHover = WindowsNative.Cursor();
            try
            {
                var target = envelopeIcon.PointToScreen(new Point(envelopeIcon.ActualWidth / 2, envelopeIcon.ActualHeight / 2));
                WindowsNative.SetWindowPos(shelf.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x13);
                SetCursorPos((int)target.X, (int)target.Y);
                await Until(() => WindowFromPoint(WindowsNative.Cursor()) == shelf.Handle);
                SetCursorPos((int)target.X + 1, (int)target.Y);
                await Until(() => shelf.TextButton.IsMouseOver && envelopeIcon.Expansion > 0.98);
                Check(shelf.TextButton.IsMouseOver && envelopeIcon.Expansion > 0.98, "真实鼠标移到信封时能触发展开");
                SetCursorPos(shelf.Display.WorkingArea.Right - 20, shelf.Display.WorkingArea.Bottom - 20);
                await Until(() => !shelf.TextButton.IsMouseOver && envelopeIcon.Expansion < 0.01);
                Check(!shelf.TextButton.IsMouseOver && envelopeIcon.Expansion < 0.01, "真实鼠标离开时能收回信纸");
            }
            finally { SetCursorPos(pointerBeforeHover.X, pointerBeforeHover.Y); }
            shelf.TextButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            await Task.Delay(180);
            shelf.Reveal(false, false, 0);
            Check(envelopeIcon.Expansion == 0, "收起绳子时重置信纸动效");
            shelf.Reveal(true, true, 60); shelf.ChoosingTexts = true;
            await Task.Delay(450); Open();

            for (int i = 0; i < 1200; i++) store.State.Texts.Add(new TextItem { Id = Guid.NewGuid().ToString("N"), Content = "归档文字 " + i, CreatedUtc = DateTime.UtcNow.AddMinutes(-i) });
            store.Save(); shelf.UpdateTexts(); window.Close(); Open(); window.UpdateLayout();
            int realized = Children<ListBoxItem>(window.List).Count();
            Check(window.VisibleCount == 1204 && realized < 25, "大量记录使用虚拟列表，仅创建可见行");
            var last = controller.Texts.Items.Last();
            window.List.ScrollIntoView(last); await Task.Delay(100); window.UpdateLayout();
            var lastRow = (ListBoxItem)window.List.ItemContainerGenerator.ContainerFromIndex(window.List.Items.Count - 1);
            Check(lastRow != null && ((TextItem)lastRow.DataContext).Id == last.Id, "长列表可滚到最后一条，回收行仍绑定正确内容");
            Children<Button>(lastRow).Single(button => (string)button.Tag == "delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!controller.Texts.Items.Contains(last) && controller.Texts.Items.Count == 1203, "滚动后的删除作用于当前记录，数量正确");
            window.Search.Text = "相机存储卡"; window.UpdateLayout();
            Check(window.VisibleCount == 1, "长列表滚动后搜索可以回到匹配结果");
            var row = (ListBoxItem)window.List.ItemContainerGenerator.ContainerFromIndex(0);
            Check(row != null, "搜索后不会停留在无内容的旧滚动位置");
            window.Close();
            Check(new TextCollection(new StateStore(store.Root)).Items.Count == 1203 && !File.Exists(Path.Combine(store.Root, "error.log")), "文字记录可重启恢复，操作过程无运行错误");
            controller.ShowTexts(); await Task.Delay(80);
            Check(controller.TextWindow != null && shelf.ChoosingTexts, "固定入口打开真实文字窗口并保持绳子展开");
            controller.ShowTexts();
            Check(controller.TextWindow == null && !shelf.ChoosingTexts, "再次点击入口关闭窗口并释放保持状态");
            controller.Dispose(); controller = new DesktopController(new StateStore(store.Root), watched, false);
            await controller.Start();
            textSwitch = controller.TrayMenu.Text;
            Check(textSwitch.IsChecked == true, "重启后恢复文字收集开关");
            await WriteText("重启后的文字自动收集"); await Until(() => controller.Texts.Items.Count == 1204);
            Check(controller.Texts.Items[0].Content == "重启后的文字自动收集", "信封关闭时复制文字也能自动收集");
            textSwitch.IsChecked = false;
            await WriteText("关闭开关后不收集"); await Task.Delay(180);
            Check(controller.Texts.Items.Count == 1204 && !new StateStore(store.Root).State.CollectTextClipboard,
                "关闭后立即停止收集，保存开关并保留原有文字");
            var copiedImage = controller.Gallery.Collected.First();
            controller.Act(ShotAction.Copy, copiedImage);
            await Until(() => Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Contains(copiedImage.Path));
            lastSequence = WindowsNative.GetClipboardSequenceNumber();
            Check(Clipboard.ContainsImage() && Clipboard.GetDataObject().GetData("PNG") is Stream && Clipboard.GetImage().PixelWidth > 0,
                "兼容写入同时保留图片、PNG 和文件拖拽格式");
            await CheckCaptureRestore();
        }

        static async Task CheckCaptureRestore()
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "restore-" + Guid.NewGuid().ToString("N"));
            var store = new StateStore(root);
            Directory.CreateDirectory(store.Inbox);
            byte[] picture = ImageContent.EncodeClipboard(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 60, 90, 120, 255 }, 4));
            string first = Path.Combine(root, "先收集.png"), second = Path.Combine(root, "后收集.png");
            File.WriteAllBytes(first, picture); File.WriteAllBytes(second, picture);
            var gallery = new Gallery(store);
            gallery.Collect(first, ImageContent.Decode(picture)); gallery.Collect(second, ImageContent.Decode(picture));
            gallery.Add(second, ImageContent.Decode(picture)); gallery.Add(first, ImageContent.Decode(picture));
            store.State.AutoHang = false; store.State.Theme = "graphite";
            new TextCollection(store).Add("重启后保留的文字");
            File.WriteAllBytes(Path.Combine(store.Inbox, "历史截图.png"), picture);
            var restored = new Gallery(new StateStore(root));
            using (var pipeline = new CapturePipeline(restored, Application.Current.Dispatcher, Path.Combine(root, "未启用的监控目录")))
                await pipeline.Start();
            Check(restored.Items.Select(item => item.Path).SequenceEqual(new[] { second, first }), "重启按实际挂图顺序恢复，不按收集先后重排");
            var saved = new StateStore(root).State;
            Check(saved.Images.SequenceEqual(new[] { second, first }) && saved.Collection.Count == 3 && !saved.AutoHang
                && saved.Theme == "graphite" && saved.Texts.Single().Content == "重启后保留的文字", "恢复挂图时保留待挂图片、文字和外观设置");
        }

        static async Task CheckSingleSelection()
        {
            var pointer = WindowsNative.Cursor();
            var foreground = WindowsNative.GetForegroundWindow();
            try
            {
                controller.Texts.Add("片头先展示操作结果，再说明具体步骤。");
                controller.Texts.Add("拍摄清单\n1. 检查麦克风电量\n2. 确认相机存储卡");
                controller.Texts.Add("会议时间：10月28日 14:30，提前准备演示材料。");
                WindowsNative.ActivateEditor(new System.Windows.Interop.WindowInteropHelper(window).Handle);
                var area = controller.Shelf.Display.WorkingArea;
                SetCursorPos(area.Left + 20, area.Bottom - 20);
                await Task.Delay(100); window.UpdateLayout();
                var rows = Enumerable.Range(0, 3).Select(i => (ListBoxItem)window.List.ItemContainerGenerator.ContainerFromIndex(i)).ToArray();
                var choices = rows.Select(row => Children<CheckBox>(row).Single()).ToArray();
                var mark = (Border)choices[0].Template.FindName("Mark", choices[0]);
                Check(mark.Opacity == 0 && choices.All(choice => choice.IsChecked == false), "未悬停且未选中时隐藏选择框");
                Export("单选-静止隐藏", controller.Shelf, window);
                var hover = rows[0].PointToScreen(new Point(70, 18));
                SetCursorPos((int)hover.X, (int)hover.Y); await Task.Delay(100);
                Check(rows[0].IsMouseOver && mark.Opacity == 1 && choices[0].IsChecked == false, "悬停正文时显示该条可点击的选择框");
                Export("单选-悬停可选", controller.Shelf, window);
                await Click(choices[0]);
                Check(controller.Texts.Items.Count(item => item.IsMarked) == 1 && ((TextItem)choices[0].DataContext).IsMarked
                    && window.DeleteSelectedButton.IsEnabled && window.DeleteSelectedButton.Content.ToString().Contains("1"), "真实鼠标仅勾选当前一条，并更新批量删除数量");
                SetCursorPos(area.Left + 20, area.Bottom - 20); await Task.Delay(60);
                Check(mark.Opacity == 1, "选中后移开鼠标仍保留勾选标记");
                Export("单选-鼠标勾选", controller.Shelf, window);
                await Click(choices[0]);
                Check(controller.Texts.Items.All(item => !item.IsMarked) && !window.DeleteSelectedButton.IsEnabled, "真实鼠标再次点击可取消单条选择");
                await Click(Children<TextBlock>(rows[1]).First());
                Check(rows[1].IsSelected && Children<TextBox>(rows[1]).Single().IsVisible, "点击正文仍可展开全文阅读");
                SetCursorPos(area.Left + 20, area.Bottom - 20); await Task.Delay(60);
                Check(((Border)choices[1].Template.FindName("Mark", choices[1])).Opacity == 1, "点击激活的条目移开鼠标后仍显示选择框");
                await Click(choices[1]);
                Check(controller.Texts.Items.Count(item => item.IsMarked) == 1 && ((TextItem)choices[1].DataContext).IsMarked, "展开全文后仍可用鼠标单独勾选该条");
                await Click(choices[1]);
                Check(controller.Texts.Items.Count == 3 && controller.Texts.Items.All(item => !item.IsMarked), "取消勾选后保留全部原文");
            }
            finally
            {
                window.Close(); SetCursorPos(pointer.X, pointer.Y); WindowsNative.SetForegroundWindow(foreground);
            }
        }

        static async Task Click(FrameworkElement element)
        {
            var target = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            SetCursorPos((int)target.X, (int)target.Y); await Task.Delay(60);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(80); window.UpdateLayout();
        }

        static void Open()
        {
            window = new TextWindow(controller.Texts, controller.Shelf) { ShowActivated = false };
            window.Show(); window.UpdateLayout();
        }

        static async Task WriteText(string value)
        {
            var data = new DataObject(); data.SetText(value, TextDataFormat.UnicodeText);
            await WindowsClipboard.Write(data); lastSequence = WindowsNative.GetClipboardSequenceNumber();
        }

        static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T) yield return (T)child;
                foreach (var match in Children<T>(child)) yield return match;
            }
        }

        static async Task Until(Func<bool> condition)
        {
            for (int i = 0; i < 80; i++) { if (condition()) return; await Task.Delay(25); }
            throw new Exception("等待界面状态超时");
        }

        static void Export(string name, ShelfWindow shelf, TextWindow popup)
        {
            var drawing = new DrawingVisual();
            double scale = WindowsNative.MonitorScale(shelf.Display);
            var origin = shelf.PointToScreen(new Point()); var location = popup.PointToScreen(new Point());
            int width = (int)Math.Round(shelf.ActualWidth), height = 820;
            using (var painter = drawing.RenderOpen())
            {
                painter.DrawRectangle(LightTheme.Brush("#EDF1F6"), null, new Rect(0, 0, width, height));
                painter.DrawRectangle(new VisualBrush(shelf) { Stretch = Stretch.Fill }, null, new Rect(0, 0, shelf.ActualWidth, shelf.ActualHeight));
                painter.DrawRectangle(new VisualBrush(popup) { Stretch = Stretch.Fill }, null, new Rect((location.X - origin.X) / scale, (location.Y - origin.Y) / scale, popup.ActualWidth, popup.ActualHeight));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string directory = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "previews", "文字信封", "实装"));
            Directory.CreateDirectory(directory);
            using (var output = File.Create(Path.Combine(directory, name + ".png"))) encoder.Save(output);
        }

        static void ExportEnvelope(string name, ShelfWindow shelf)
        {
            var drawing = new DrawingVisual();
            using (var painter = drawing.RenderOpen())
            {
                painter.DrawRectangle(LightTheme.Brush("#EDF1F6"), null, new Rect(0, 0, shelf.ActualWidth, shelf.ActualHeight));
                painter.DrawRectangle(new VisualBrush(shelf) { Stretch = Stretch.Fill }, null, new Rect(0, 0, shelf.ActualWidth, shelf.ActualHeight));
            }
            var bitmap = new RenderTargetBitmap((int)shelf.ActualWidth, (int)shelf.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string directory = name.StartsWith("frame-") ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "envelope-motion-frames")
                : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "previews", "文字信封", "抽出动效"));
            Directory.CreateDirectory(directory);
            using (var output = File.Create(Path.Combine(directory, name + ".png"))) encoder.Save(output);
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++; Console.WriteLine("通过：" + message);
        }
    }
}
