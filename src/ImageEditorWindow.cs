using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace ScreenLine
{
    internal sealed class ImageEditorWindow : Window
    {
        const string Origin = "https://screenline-editor.invalid/";
        readonly StateStore store;
        readonly Func<string, Task> saved;
        readonly Action copied;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 96 * 1024 * 1024 };
        readonly TextBlock failureTitle = new TextBlock { Text = "正在打开图片…", Foreground = LightTheme.Brush("#283343"), FontSize = 16, FontWeight = FontWeights.SemiBold };
        readonly TextBlock failure = new TextBlock { Foreground = LightTheme.Brush("#65717F"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), LineHeight = 21 };
        readonly Border failurePanel;
        readonly Button failureClose;
        readonly TaskCompletionSource<bool> initialized = new TaskCompletionSource<bool>();
        TaskCompletionSource<bool> saveForClose;
        bool dirty, busy, closed, closingCheck, forceClose;
        internal readonly WebView2 Browser = new WebView2();
        internal Task Ready { get { return initialized.Task; } }
        internal Rect ReturnBounds = Rect.Empty;
        internal BitmapSource ReturnPicture;
        public string ImagePath { get; private set; }

        public ImageEditorWindow(string path, StateStore store, Func<string, Task> saved, Action copied)
        {
            ImagePath = path;
            this.store = store;
            this.saved = saved;
            this.copied = copied;
            Title = "图片编辑 · " + Path.GetFileName(path);
            Background = new SolidColorBrush(Color.FromRgb(244, 246, 249));
            Width = Math.Min(1000, SystemParameters.WorkArea.Width - 48);
            Height = Math.Min(700, SystemParameters.WorkArea.Height - 48);
            MinWidth = Math.Min(720, Width);
            MinHeight = Math.Min(480, Height);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var panel = new Grid();
            panel.Children.Add(Browser);
            failure.Text = Path.GetFileName(path);
            failureClose = LightTheme.Button("关闭", Close);
            failureClose.HorizontalAlignment = HorizontalAlignment.Right;
            failureClose.Margin = new Thickness(0, 20, 0, 0);
            failureClose.Visibility = Visibility.Collapsed;
            var failureContent = new StackPanel();
            failureContent.Children.Add(failureTitle); failureContent.Children.Add(failure); failureContent.Children.Add(failureClose);
            failurePanel = LightTheme.Card(failureContent);
            failurePanel.Width = 380;
            failurePanel.HorizontalAlignment = HorizontalAlignment.Center;
            failurePanel.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(failurePanel);
            Content = panel;
            Loaded += Initialize;
            Closing += ClosingEditor;
            Deactivated += delegate { Topmost = false; };
            Closed += delegate
            {
                closed = true;
                initialized.TrySetCanceled();
                if (saveForClose != null) saveForClose.TrySetResult(false);
                Browser.Dispose();
            };
        }

        public void Present()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Topmost = true;
            Activate();
            WindowsNative.ActivateEditor(new WindowInteropHelper(this).Handle);
            Browser.Focus();
        }

        async void Initialize(object sender, RoutedEventArgs args)
        {
            try
            {
                byte[] png = await Task.Run(() => ImageEditFiles.ReadPng(ImagePath));
                if (closed) return;
                var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(store.Root, "EditorCache"),
                    new CoreWebView2EnvironmentOptions("--disk-cache-size=8388608"));
                if (closed) return;
                await Browser.EnsureCoreWebView2Async(environment);
                if (closed) return;
                var core = Browser.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += delegate(object source, CoreWebView2WebResourceRequestedEventArgs e)
                {
                    if (e.Request.Uri == Origin + "source.png")
                    {
                        e.Response = environment.CreateWebResourceResponse(new MemoryStream(png ?? ImageEditFiles.ReadPng(ImagePath)), 200, "OK", "Content-Type: image/png\r\nCache-Control: no-store");
                        png = null; return;
                    }
                    string name = e.Request.Uri.StartsWith(Origin, StringComparison.Ordinal) ? e.Request.Uri.Substring(Origin.Length) : "";
                    if (name == "index.html" || name == "editor.js" || name == "editor.css" || name == "translations.js" || name == "filerobot-image-editor.min.js")
                    {
                        string mime = name.EndsWith(".html") ? "text/html; charset=utf-8" : name.EndsWith(".css") ? "text/css" : "application/javascript";
                        var file = new FileStream(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "editor", name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        e.Response = environment.CreateWebResourceResponse(file, 200, "OK", "Content-Type: " + mime); return;
                    }
                    e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
                };
                core.NavigationStarting += delegate(object source, CoreWebView2NavigationStartingEventArgs e) { e.Cancel = e.Uri != Origin + "index.html"; };
                core.NewWindowRequested += delegate(object source, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; };
                core.WebMessageReceived += Receive;
                core.NavigationCompleted += async delegate(object source, CoreWebView2NavigationCompletedEventArgs e)
                {
                    if (closed) return;
                    if (!e.IsSuccess) { ShowFailure(new IOException("图片编辑器页面加载失败。")); return; }
                    await Browser.ExecuteScriptAsync("window.startEditor(" + json.Serialize(new { name = Path.GetFileName(ImagePath), source = Origin + "source.png" }) + ");");
                };
                failurePanel.Visibility = Visibility.Collapsed;
                core.Navigate(Origin + "index.html");
            }
            catch (Exception error) { if (!closed) ShowFailure(error); }
        }

        void ShowFailure(Exception error)
        {
            store.Log(error);
            failureTitle.Text = "无法打开图片编辑器";
            failure.Text = error is WebView2RuntimeNotFoundException
                ? "请安装 Microsoft Edge WebView2 Runtime 后重新打开编辑器。"
                : "请重新打开图片，或换一张图片重试。";
            Browser.Visibility = Visibility.Collapsed;
            failurePanel.Visibility = Visibility.Visible;
            failureClose.Visibility = Visibility.Visible;
            initialized.TrySetException(error);
        }

        async void Receive(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (closed || args.Source != Origin + "index.html") return;
            bool success = false, didSave = false, closeAfterSave = false;
            string message = "", action = "";
            try
            {
                var request = json.Deserialize<Dictionary<string, object>>(args.WebMessageAsJson);
                action = request["action"] as string;
                if (action == "ready") { initialized.TrySetResult(true); return; }
                if (action == "dirty") { dirty = (bool)request["value"]; return; }
                if (action == "error") { ShowFailure(new InvalidOperationException(request["detail"] as string)); return; }
                if (busy || (action != "save" && action != "saveAs" && action != "copy")) return;
                closeAfterSave = action == "save" && request.ContainsKey("closeAfterSave") && (bool)request["closeAfterSave"];
                busy = true;
                byte[] png = await Task.Run(() => ImageEditFiles.Decode(request["data"] as string));
                if (closed) return;
                if (action == "copy")
                {
                    var payload = new DataObject();
                    using (var stream = new MemoryStream(png))
                        payload.SetImage(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
                    payload.SetData("PNG", new MemoryStream(png));
                    payload.SetData(ClipboardRules.OwnFormat, "1");
                    await WindowsClipboard.Write(payload);
                    copied();
                    success = true;
                    message = "已复制图片";
                }
                else
                {
                    string destination = ImagePath;
                    if (action == "saveAs")
                    {
                        var picker = new SaveFileDialog { Title = "另存图片", InitialDirectory = Path.GetDirectoryName(ImagePath), FileName = Path.GetFileNameWithoutExtension(ImagePath) + "_编辑.png", Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|BMP 图片|*.bmp|TIFF 图片|*.tiff", DefaultExt = ".png", AddExtension = true, OverwritePrompt = true };
                        destination = picker.ShowDialog(this) == true ? picker.FileName : null;
                    }
                    if (destination != null)
                    {
                        await Task.Run(() => ImageEditFiles.Save(destination, png));
                        await saved(destination);
                        ImagePath = destination;
                        Title = "图片编辑 · " + Path.GetFileName(ImagePath);
                        success = didSave = true;
                        dirty = false;
                        message = "已保存";
                    }
                    else { success = true; message = "已取消另存为"; }
                }
            }
            catch (Exception error)
            {
                store.Log(error);
                message = action == "copy" ? "复制未完成，请重试。" : "保存未完成，请确认文件未被其他程序占用。";
            }
            busy = false;
            if (closed) return;
            await Browser.ExecuteScriptAsync("window.completeAction(" + json.Serialize(new { success, saved = didSave, message, name = Path.GetFileName(ImagePath) }) + ");");
            if (saveForClose != null && action == "save") saveForClose.TrySetResult(success);
            if (success && closeAfterSave) Close();
        }

        public async Task<bool> ConfirmClose()
        {
            if (busy) return false;
            if (!dirty) return true;
            var dialog = new SaveChangesDialog(this, ImagePath);
            dialog.ShowDialog();
            var choice = dialog.Choice;
            if (choice == MessageBoxResult.Cancel) return false;
            if (choice == MessageBoxResult.No) return true;
            saveForClose = new TaskCompletionSource<bool>();
            await Browser.ExecuteScriptAsync("window.submit('save');");
            bool result = await saveForClose.Task;
            saveForClose = null;
            return result;
        }

        async void ClosingEditor(object sender, CancelEventArgs args)
        {
            if (forceClose) return;
            args.Cancel = true;
            if (closingCheck) return;
            closingCheck = true;
            if (await ConfirmClose())
            {
                if (initialized.Task.Status == TaskStatus.RanToCompletion && WindowState != WindowState.Minimized)
                {
                    try
                    {
                        var bounds = json.Deserialize<double[]>(await Browser.ExecuteScriptAsync("window.imageBounds()"));
                        ReturnPicture = await Task.Run(() => PinnedImageWindow.ReadPicture(ImagePath));
                        ReturnBounds = new Rect(Browser.PointToScreen(new Point(bounds[0], bounds[1])), Browser.PointToScreen(new Point(bounds[0] + bounds[2], bounds[1] + bounds[3])));
                    }
                    catch (Exception error) { store.Log(error); }
                }
                await Dispatcher.InvokeAsync(new Action(CloseWithoutPrompt));
            }
            closingCheck = false;
        }

        public void CloseWithoutPrompt() { forceClose = true; Close(); }
    }
}
