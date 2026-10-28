using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shapes = System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace ScreenLine
{
    // 监控文件夹管理窗口：总开关、目录增删与子文件夹范围设置。
    internal sealed class WatchFolderWindow : Window
    {
        readonly CapturePipeline captures;
        readonly Gallery gallery;
        readonly CheckBox enabledToggle;
        readonly TextBlock emptyHint;
        readonly TextBlock statusLine;
        readonly Panel rows;
        string signature;
        bool syncing;

        public WatchFolderWindow(CapturePipeline captures, Gallery gallery)
        {
            this.captures = captures;
            this.gallery = gallery;
            Title = "监控文件夹";
            Width = 520; MinHeight = 300; MaxHeight = 640;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; Topmost = true;
            FontFamily = new FontFamily("Segoe UI");
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var body = new StackPanel();

            var heading = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            heading.Children.Add(new TextBlock { Text = "监控文件夹", FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = LightTheme.Brush("#283343") });
            var close = LightTheme.Button("", Close);
            close.Content = LightTheme.CloseIcon(); close.Width = close.Height = 28; close.MinWidth = 0;
            close.Padding = new Thickness(0); close.Tag = "quiet"; close.HorizontalAlignment = HorizontalAlignment.Right;
            AutomationProperties.SetName(close, "关闭监控文件夹");
            heading.Children.Add(close);
            heading.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args) { if (args.OriginalSource is TextBlock) DragMove(); };
            body.Children.Add(heading);
            body.Children.Add(new TextBlock { Text = "文件夹中新产生的图片按「新图片自动上绳」挂上绳子或进入图片篮子", FontSize = 12, Foreground = LightTheme.Brush("#65717F"), Margin = new Thickness(0, 0, 0, 14) });

            enabledToggle = new CheckBox { Style = TrayPanel.ToggleStyle, Content = SwitchLabel("监控开关", "开启后自动收集新图片") };
            AutomationProperties.SetName(enabledToggle, "监控开关");
            enabledToggle.Checked += delegate { SetEnabled(true); };
            enabledToggle.Unchecked += delegate { SetEnabled(false); };
            body.Children.Add(enabledToggle);
            body.Children.Add(new Border { Height = 1, Background = LightTheme.Brush("#E8EBEF"), Margin = new Thickness(0, 6, 0, 6) });

            rows = new StackPanel();
            emptyHint = new TextBlock { Text = "尚未添加监控文件夹，点击下方按钮选择文件夹。", FontSize = 12, Foreground = LightTheme.Brush("#65717F"), Margin = new Thickness(10, 10, 0, 10) };
            var list = new StackPanel();
            list.Children.Add(emptyHint);
            list.Children.Add(rows);
            body.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 280 });

            var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            var add = LightTheme.Button("添加文件夹…", AddFolder, "primary");
            add.Width = 150; add.HorizontalAlignment = HorizontalAlignment.Left;
            AutomationProperties.SetName(add, "添加监控文件夹");
            footer.Children.Add(add);
            statusLine = new TextBlock { FontSize = 11, Foreground = LightTheme.Brush("#65717F"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
            footer.Children.Add(statusLine);
            body.Children.Add(footer);
            body.Children.Add(new TextBlock { Text = "勾选「子文件夹」同时监控其下所有子目录；开启监控时已有的图片不会重复上绳。", FontSize = 11, Foreground = LightTheme.Brush("#65717F"), Margin = new Thickness(0, 10, 0, 0) });

            Content = LightTheme.Card(body);
            PreviewKeyDown += delegate(object sender, KeyEventArgs args) { if (args.Key == Key.Escape) Close(); };
            gallery.Changed += Refresh;
            Closed += delegate { gallery.Changed -= Refresh; };
            Refresh();
            add.Focus();
        }

        void SetEnabled(bool value)
        {
            if (syncing || captures.WatchFoldersEnabled == value) return;
            if (value && captures.WatchFolders.Count == 0)
            {
                statusLine.Text = "请先添加监控文件夹";
                SyncToggle();
                return;
            }
            try
            {
                if (value) captures.EnableWatchFolders();
                else captures.DisableWatchFolders();
            }
            catch (Exception error)
            {
                gallery.Store.Log(error);
                statusLine.Text = "监控设置保存失败，请重试";
                SyncToggle();
            }
        }

        void AddFolder()
        {
            using (var picker = new Forms.FolderBrowserDialog { Description = "选择要监控的文件夹", ShowNewFolderButton = false })
            {
                var current = captures.WatchFolders.Select(item => WatchFolderRules.Normalize(item.Path)).FirstOrDefault(Directory.Exists);
                if (current != null) picker.SelectedPath = current;
                if (picker.ShowDialog() != Forms.DialogResult.OK) return;
                try
                {
                    if (captures.AddWatchFolder(picker.SelectedPath, false)) statusLine.Text = null;
                    else statusLine.Text = "文件夹路径无效";
                }
                catch (Exception error)
                {
                    gallery.Store.Log(error);
                    statusLine.Text = "监控设置保存失败，请重试";
                }
            }
        }

        void RemoveFolder(string path)
        {
            try { captures.RemoveWatchFolder(path); }
            catch (Exception error)
            {
                gallery.Store.Log(error);
                statusLine.Text = "监控设置保存失败，请重试";
            }
        }

        void SetChildren(string path, CheckBox toggle)
        {
            if (syncing) return;
            try { captures.SetWatchFolderChildren(path, toggle.IsChecked == true); }
            catch (Exception error)
            {
                gallery.Store.Log(error);
                statusLine.Text = "监控设置保存失败，请重试";
                syncing = true;
                toggle.IsChecked = toggle.IsChecked != true;
                syncing = false;
            }
        }

        void Refresh()
        {
            SyncToggle();
            string mark = captures.WatchFoldersEnabled + "|" + string.Join(";", captures.WatchFolders.Select(item => WatchFolderRules.Normalize(item.Path) + ":" + item.IncludeChildren).ToArray());
            if (mark == signature) return;
            signature = mark;
            rows.Children.Clear();
            emptyHint.Visibility = captures.WatchFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var item in captures.WatchFolders.ToArray()) rows.Children.Add(Row(item));
        }

        void SyncToggle()
        {
            if (enabledToggle.IsChecked != captures.WatchFoldersEnabled)
            {
                syncing = true;
                enabledToggle.IsChecked = captures.WatchFoldersEnabled;
                syncing = false;
            }
        }

        Border Row(WatchFolderItem item)
        {
            string path = WatchFolderRules.Normalize(item.Path);
            bool present = path.Length > 0 && Directory.Exists(path);
            var row = new Grid { Height = 48 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(122) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            row.Children.Add(new Viewbox
            {
                Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center,
                Child = new Shapes.Path { Data = LightMenu.IconGeometry("folder"), Stroke = LightTheme.Brush(present ? "#667487" : "#BB514E"), StrokeThickness = 1.65, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round }
            });
            var name = new TextBlock { Text = WatchFolderRules.Compact(path, 36), FontSize = 12, Foreground = LightTheme.Brush(present ? "#283343" : "#BB514E"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            name.ToolTip = new ToolTip { Content = present ? path : path + "（目录不存在）" };
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var children = new CheckBox { Style = TrayPanel.ToggleStyle, Content = new TextBlock { Text = "子文件夹", FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, IsChecked = item.IncludeChildren, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(children, "监控子文件夹：" + WatchFolderRules.Brief(path));
            children.Checked += delegate { SetChildren(path, children); };
            children.Unchecked += delegate { SetChildren(path, children); };
            Grid.SetColumn(children, 2); row.Children.Add(children);
            var remove = LightTheme.Button("移除", delegate { RemoveFolder(path); });
            remove.Foreground = LightTheme.Brush("#BB514E"); remove.Height = 30; remove.MinWidth = 0; remove.Padding = new Thickness(10, 0, 10, 0);
            AutomationProperties.SetName(remove, "移除监控文件夹：" + WatchFolderRules.Brief(path));
            Grid.SetColumn(remove, 3); row.Children.Add(remove);
            return new Border { Child = row, BorderBrush = LightTheme.Brush("#EDF0F4"), BorderThickness = new Thickness(0, 0, 0, 1) };
        }

        static Grid SwitchLabel(string title, string hint)
        {
            var grid = new Grid();
            grid.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
            grid.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = LightTheme.Brush("#65717F"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0) });
            return grid;
        }
    }
}
