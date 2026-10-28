using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ScreenLine
{
    internal static class TextEnvelope
    {
        static readonly Dictionary<string, BitmapSource> images = new Dictionary<string, BitmapSource>();
        static readonly Int32Rect[] bounds = {
            new Int32Rect(112, 73, 1031, 1103), new Int32Rect(136, 112, 982, 1031),
            new Int32Rect(184, 195, 887, 892), new Int32Rect(127, 90, 1001, 1062),
            new Int32Rect(112, 72, 1031, 1103)
        };

        public static BitmapSource Source(LineTheme theme)
        {
            BitmapSource image;
            if (images.TryGetValue(theme.Id, out image)) return image;
            image = SkinImages.Crop("PicRope.Envelope." + theme.Id, bounds[Array.IndexOf(LineTheme.All, theme)]);
            images.Add(theme.Id, image);
            return image;
        }
    }

    public sealed class TextTime : IValueConverter
    {
        public object Convert(object value, Type type, object parameter, CultureInfo culture)
        {
            var time = ((DateTime)value).ToLocalTime();
            if (DateTime.Now - time < TimeSpan.FromMinutes(1)) return "刚刚";
            return (time.Date == DateTime.Today ? "今天 " : time.ToString("MM-dd ")) + time.ToString("HH:mm");
        }
        public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) { throw new NotSupportedException(); }
    }

    internal sealed class TextWindow : Window
    {
        readonly TextCollection texts;
        readonly ShelfWindow shelf;
        readonly TextBlock count = new TextBlock();
        readonly TextBlock feedback = new TextBlock { Foreground = LightTheme.Brush("#65717F"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock empty = new TextBlock { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = LightTheme.Brush("#65717F"), Margin = new Thickness(20) };
        readonly Polygon pointer = new Polygon { Points = new PointCollection { new Point(0, 12), new Point(12, 0), new Point(24, 12) }, Fill = Brushes.White, Width = 24, Height = 12, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        readonly DispatcherTimer feedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        internal readonly TextBox Search = new TextBox();
        internal readonly ListBox List = new ListBox();
        internal readonly Button SelectAllButton, DeleteSelectedButton;
        bool refreshing, confirming;
        string checkedSignature;
        internal int VisibleCount { get { return List.Items.Count; } }
        internal string Feedback { get { return feedback.Text; } }
        bool closed;

        public TextWindow(TextCollection texts, ShelfWindow shelf)
        {
            this.texts = texts; this.shelf = shelf;
            foreach (var item in texts.Items) item.IsMarked = false;
            Title = "文字 · 图绳"; Owner = shelf;
            Width = 460; Height = 456;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; Topmost = true;
            FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            AutomationProperties.SetAutomationId(this, "TextCollection");

            var root = new Grid(); Content = root;
            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            body.RowDefinitions.Add(new RowDefinition());
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            var card = LightTheme.Card(body); card.Margin = new Thickness(16, 11, 16, 16); card.Padding = new Thickness(0); card.CornerRadius = new CornerRadius(12);
            root.Children.Add(card); root.Children.Add(pointer);

            var header = new Grid { Margin = new Thickness(18, 0, 14, 0) };
            var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(new TextBlock { Text = "文字", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = LightTheme.Brush("#283343") });
            count.FontSize = 12; count.Foreground = LightTheme.Brush("#536172");
            heading.Children.Add(new Border { Child = count, Background = LightTheme.Brush("#F0F3F7"), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
            heading.Children.Add(feedback); header.Children.Add(heading);
            var close = LightTheme.Button("", Close, "quiet"); close.Content = LightTheme.CloseIcon();
            close.Width = close.Height = 28; close.MinWidth = 0; close.Padding = new Thickness(0); close.HorizontalAlignment = HorizontalAlignment.Right;
            AutomationProperties.SetName(close, "关闭文字"); header.Children.Add(close); body.Children.Add(header);

            var searchPanel = new Grid { Margin = new Thickness(18, 0, 18, 10) };
            Search.BorderThickness = new Thickness(0); Search.Background = Brushes.Transparent; Search.Padding = new Thickness(10, 6, 10, 6);
            Search.VerticalContentAlignment = VerticalAlignment.Center; Search.FontSize = 13; Search.Foreground = LightTheme.Brush("#283343");
            AutomationProperties.SetName(Search, "搜索文字");
            searchPanel.Children.Add(new Border { CornerRadius = new CornerRadius(7), BorderBrush = LightTheme.Brush("#DCE2EA"), BorderThickness = new Thickness(1), Child = Search });
            var placeholder = new TextBlock { Text = "搜索文字", IsHitTestVisible = false, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = LightTheme.Brush("#8390A1") };
            searchPanel.Children.Add(placeholder);
            Search.TextChanged += delegate { confirming = false; placeholder.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Refresh(); };
            Grid.SetRow(searchPanel, 1); body.Children.Add(searchPanel);

            List.BorderThickness = new Thickness(0); List.Background = Brushes.Transparent; List.Margin = new Thickness(10, 0, 10, 10);
            List.FontFamily = FontFamily; List.FontSize = 14;
            List.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            List.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            List.SetValue(ScrollViewer.CanContentScrollProperty, true);
            VirtualizingPanel.SetIsVirtualizing(List, true); VirtualizingPanel.SetVirtualizationMode(List, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(List, ScrollUnit.Pixel);
            List.ItemContainerStyle = (Style)XamlReader.Parse(@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ListBoxItem'>
              <Setter Property='HorizontalContentAlignment' Value='Stretch'/><Setter Property='Padding' Value='0'/><Setter Property='Margin' Value='0'/><Setter Property='Background' Value='Transparent'/>
              <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBoxItem'><Border x:Name='Row' Background='{TemplateBinding Background}' CornerRadius='7'><ContentPresenter/></Border>
              <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Row' Property='Background' Value='#F4F7FB'/></Trigger>
              <Trigger Property='IsSelected' Value='True'><Setter TargetName='Row' Property='Background' Value='#EDF4FF'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
            List.ItemTemplate = (DataTemplate)XamlReader.Parse(@"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:local='clr-namespace:ScreenLine;assembly=__ASSEMBLY__'>
              <DataTemplate.Resources><local:TextTime x:Key='TextTime'/><Geometry x:Key='CopyIcon'>__COPY__</Geometry><Geometry x:Key='TrashIcon'>__TRASH__</Geometry><Style x:Key='IconButton' TargetType='Button'>
                <Setter Property='Width' Value='28'/><Setter Property='Height' Value='30'/><Setter Property='Cursor' Value='Hand'/><Setter Property='Foreground' Value='#647386'/><Setter Property='Background' Value='Transparent'/>
                <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'><Border x:Name='Surface' Background='{TemplateBinding Background}' CornerRadius='5'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>
                <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='#E7EEF8'/><Setter Property='Foreground' Value='#2463AE'/></Trigger>
                <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='#6E97CB'/><Setter TargetName='Surface' Property='BorderThickness' Value='1'/></Trigger>
                <MultiTrigger><MultiTrigger.Conditions><Condition Property='Tag' Value='delete'/><Condition Property='IsMouseOver' Value='True'/></MultiTrigger.Conditions><Setter Property='Foreground' Value='#B74E4A'/><Setter TargetName='Surface' Property='Background' Value='#FFF0ED'/></MultiTrigger>
                </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style></DataTemplate.Resources>
              <Border BorderBrush='#EDF0F4' BorderThickness='0,0,0,1' Padding='12,10,8,10'><Grid><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='64'/></Grid.ColumnDefinitions>
                <CheckBox IsChecked='{Binding IsMarked, Mode=TwoWay}' Tag='select' AutomationProperties.Name='选择文字' Width='22' Height='22' HorizontalAlignment='Left' VerticalAlignment='Top' Cursor='Hand'>
                  <CheckBox.Template><ControlTemplate TargetType='CheckBox'>
                    <Border Background='Transparent'>
                      <Border x:Name='Mark' Width='18' Height='18' CornerRadius='9' Background='White' BorderBrush='#8BA4C4' BorderThickness='1' Opacity='0' HorizontalAlignment='Center' VerticalAlignment='Center'>
                        <Path x:Name='Tick' Width='10' Height='7' Data='M0,3 L3,6 L9,0' Stroke='White' StrokeThickness='1.6' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' HorizontalAlignment='Center' VerticalAlignment='Center' Visibility='Collapsed'/>
                      </Border>
                    </Border>
                    <ControlTemplate.Triggers>
                      <DataTrigger Binding='{Binding IsMouseOver, RelativeSource={RelativeSource AncestorType=ListBoxItem}}' Value='True'><Setter TargetName='Mark' Property='Opacity' Value='1'/></DataTrigger>
                      <DataTrigger Binding='{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}' Value='True'><Setter TargetName='Mark' Property='Opacity' Value='1'/></DataTrigger>
                      <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Mark' Property='Opacity' Value='1'/></Trigger>
                      <Trigger Property='IsChecked' Value='True'><Setter TargetName='Mark' Property='Opacity' Value='1'/><Setter TargetName='Mark' Property='Background' Value='#2463AE'/><Setter TargetName='Mark' Property='BorderBrush' Value='#2463AE'/><Setter TargetName='Tick' Property='Visibility' Value='Visible'/></Trigger>
                      <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate></CheckBox.Template>
                </CheckBox>
                <StackPanel Margin='28,0,8,0'>
                  <Grid><TextBlock x:Name='Preview' Text='{Binding Content}' TextWrapping='Wrap' TextTrimming='CharacterEllipsis' MaxHeight='44' LineHeight='22' Foreground='#283343'/>
                  <TextBox x:Name='FullText' Text='{Binding Content, Mode=OneWay}' IsReadOnly='True' TextWrapping='Wrap' VerticalScrollBarVisibility='Disabled' BorderThickness='0' Padding='0' Background='Transparent' Foreground='#283343' Visibility='Collapsed'/></Grid>
                  <TextBlock Text='{Binding CreatedUtc, Converter={StaticResource TextTime}}' FontSize='11' Foreground='#8390A1' Margin='0,5,0,0'/>
                </StackPanel>
                <StackPanel Grid.Column='1' Orientation='Horizontal' VerticalAlignment='Top'>
                  <Button Tag='copy' ToolTip='复制' AutomationProperties.Name='复制文字' Style='{StaticResource IconButton}' Margin='0,0,4,0'><Viewbox Width='16' Height='16'><Path Data='{StaticResource CopyIcon}' Stroke='{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}' StrokeThickness='1.65' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'/></Viewbox></Button>
                  <Button Tag='delete' ToolTip='删除' AutomationProperties.Name='删除文字' Style='{StaticResource IconButton}'><Viewbox Width='16' Height='16'><Path Data='{StaticResource TrashIcon}' Stroke='{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}' StrokeThickness='1.65' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'/></Viewbox></Button>
                </StackPanel>
              </Grid></Border>
              <DataTemplate.Triggers><DataTrigger Binding='{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}' Value='True'><Setter TargetName='Preview' Property='Visibility' Value='Collapsed'/><Setter TargetName='FullText' Property='Visibility' Value='Visible'/></DataTrigger></DataTemplate.Triggers>
            </DataTemplate>".Replace("__ASSEMBLY__", Assembly.GetExecutingAssembly().GetName().Name)
                .Replace("__COPY__", LightMenu.IconGeometry("copy").ToString(CultureInfo.InvariantCulture))
                .Replace("__TRASH__", LightMenu.IconGeometry("trash").ToString(CultureInfo.InvariantCulture)));
            List.AddHandler(Button.ClickEvent, new RoutedEventHandler(RowAction));
            List.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler(SelectionChanged));
            List.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler(SelectionChanged));
            var listPanel = new Grid(); listPanel.Children.Add(List); listPanel.Children.Add(empty);
            Grid.SetRow(listPanel, 2); body.Children.Add(listPanel);
            var batch = new Grid { Margin = new Thickness(18, 0, 18, 6) };
            SelectAllButton = LightTheme.Button("全选", SelectAll, "quiet"); SelectAllButton.HorizontalAlignment = HorizontalAlignment.Left;
            DeleteSelectedButton = LightTheme.Button("删除所选", DeleteSelected, "quiet");
            DeleteSelectedButton.HorizontalAlignment = HorizontalAlignment.Right; DeleteSelectedButton.Foreground = LightTheme.Brush("#B74E4A");
            SelectAllButton.FontSize = DeleteSelectedButton.FontSize = 14;
            batch.Children.Add(SelectAllButton); batch.Children.Add(DeleteSelectedButton); Grid.SetRow(batch, 3); body.Children.Add(batch);

            feedbackTimer.Tick += delegate { feedback.Text = ""; feedbackTimer.Stop(); };
            SourceInitialized += delegate { Position(); };
            Loaded += delegate { Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Position)); };
            DpiChanged += delegate { Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Position)); };
            shelf.SizeChanged += ShelfMoved; shelf.LocationChanged += ShelfMoved; shelf.RopeHidden += Close;
            Deactivated += delegate { if (IsVisible) Close(); };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
            texts.Changed += Refresh;
            Closed += delegate { closed = true; texts.Changed -= Refresh; shelf.SizeChanged -= ShelfMoved; shelf.LocationChanged -= ShelfMoved; shelf.RopeHidden -= Close; feedbackTimer.Stop(); };
            Refresh();
        }

        void ShelfMoved(object sender, EventArgs e) { Position(); }

        internal void Position()
        {
            if (closed) return;
            var area = shelf.Display.WorkingArea;
            double dpi = WindowsNative.MonitorScale(shelf.Display);
            var anchor = shelf.TextAnchor;
            Width = Math.Min(460, area.Width / dpi - 16);
            Height = Math.Min(456, Math.Max(180, (area.Bottom - Math.Max(area.Top, anchor.Y)) / dpi - 8));
            double left = Math.Max(area.Left + 8 * dpi, Math.Min(area.Right - (Width + 8) * dpi, anchor.X - (Width - 76) * dpi));
            double top = Math.Max(area.Top + 4 * dpi, Math.Min(area.Bottom - Height * dpi, anchor.Y));
            pointer.Margin = new Thickness(Math.Max(30, Math.Min(Width - 54, (anchor.X - left) / dpi - 12)), 0, 0, 0);
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) WindowsNative.SetWindowPos(handle, new IntPtr(-1), (int)Math.Round(left), (int)Math.Round(top), (int)Math.Round(Width * dpi), (int)Math.Round(Height * dpi), 0x10);
        }

        void Refresh()
        {
            if (closed) return;
            var selected = List.SelectedItem;
            var scroll = FindScroll(List);
            double offset = scroll == null ? 0 : scroll.VerticalOffset;
            string query = Search.Text.Trim();
            refreshing = true;
            var visible = texts.Items.Where(item => item.Content.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0).ToArray();
            foreach (var item in texts.Items.Except(visible)) item.IsMarked = false;
            List.ItemsSource = visible;
            refreshing = false; UpdateBatch();
            List.SelectedItem = selected;
            count.Text = texts.Items.Count.ToString();
            empty.Text = texts.Items.Count == 0 ? texts.CollectClipboard
                ? "还没有文字\n复制的文字会自动出现在这里"
                : "尚未开启文字收集\n在托盘「剪贴板收集」中开启「文字」" : "没有找到匹配的文字";
            empty.Visibility = List.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (scroll != null) scroll.ScrollToVerticalOffset(offset);
        }

        void SelectionChanged(object sender, RoutedEventArgs e) { if (!refreshing) UpdateBatch(); }

        void UpdateBatch()
        {
            var items = List.Items.Cast<TextItem>().ToArray();
            int selected = items.Count(item => item.IsMarked);
            string signature = string.Join("|", items.Where(item => item.IsMarked).Select(item => item.Id).OrderBy(id => id));
            if (signature != checkedSignature) confirming = false;
            checkedSignature = signature;
            SelectAllButton.IsEnabled = items.Length > 0;
            SelectAllButton.Content = selected == items.Length && selected > 0 ? "取消全选" : "全选";
            DeleteSelectedButton.IsEnabled = selected > 0;
            DeleteSelectedButton.Content = confirming ? "确认删除 " + selected + " 条" : selected == 0 ? "删除所选" : "删除所选（" + selected + "）";
        }

        void SelectAll()
        {
            var items = List.Items.Cast<TextItem>().ToArray();
            bool mark = !items.All(item => item.IsMarked);
            refreshing = true; foreach (var item in items) item.IsMarked = mark; refreshing = false;
            UpdateBatch();
        }

        void DeleteSelected()
        {
            var items = List.Items.Cast<TextItem>().Where(item => item.IsMarked).ToArray();
            if (items.Length == 0) return;
            if (!confirming) { confirming = true; DeleteSelectedButton.Content = "确认删除 " + items.Length + " 条"; return; }
            try { texts.RemoveMany(items); ShowFeedback("已删除 " + items.Length + " 条"); }
            catch (Exception error) { texts.Store.Log(error); ShowFeedback("删除未完成，请重试"); UpdateBatch(); }
        }

        static ScrollViewer FindScroll(DependencyObject value)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++)
            {
                var child = VisualTreeHelper.GetChild(value, i);
                var scroll = child as ScrollViewer;
                if (scroll != null) return scroll;
                scroll = FindScroll(child); if (scroll != null) return scroll;
            }
            return null;
        }

        async void RowAction(object sender, RoutedEventArgs e)
        {
            var button = e.OriginalSource as Button;
            if (button == null) return;
            var item = button.DataContext as TextItem;
            if (item == null) return;
            e.Handled = true;
            button.IsEnabled = false;
            try
            {
                if ((string)button.Tag == "delete") { texts.Remove(item); ShowFeedback("已删除"); }
                else
                {
                    var data = new DataObject(); data.SetText(item.Content, TextDataFormat.UnicodeText);
                    await WindowsClipboard.Write(data); ShowFeedback("已复制");
                }
            }
            catch (Exception error) { texts.Store.Log(error); ShowFeedback("操作未完成，请重试"); }
            finally { button.IsEnabled = true; }
        }

        void ShowFeedback(string value)
        {
            if (closed) return;
            feedback.Text = value; feedbackTimer.Stop(); feedbackTimer.Start();
        }
    }
}
