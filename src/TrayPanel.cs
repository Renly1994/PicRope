using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ScreenLine
{
    internal sealed class TrayPanel : ContextMenu
    {
        static readonly ResourceDictionary styles = (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='Action' TargetType='Button'>
    <Setter Property='Background' Value='Transparent'/><Setter Property='Foreground' Value='#283343'/>
    <Setter Property='Height' Value='44'/><Setter Property='Padding' Value='12,0'/>
    <Setter Property='HorizontalContentAlignment' Value='Stretch'/><Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
      <Border x:Name='Surface' Background='{TemplateBinding Background}' CornerRadius='8' BorderBrush='Transparent' BorderThickness='1'>
        <ContentPresenter Margin='{TemplateBinding Padding}' VerticalAlignment='Center'/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='Tag' Value='primary'><Setter Property='Background' Value='#EDF4FF'/><Setter Property='Foreground' Value='#2463AE'/></Trigger>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='#F0F3F7'/></Trigger>
        <MultiTrigger><MultiTrigger.Conditions><Condition Property='Tag' Value='primary'/><Condition Property='IsMouseOver' Value='True'/></MultiTrigger.Conditions><Setter TargetName='Surface' Property='Background' Value='#E0ECFF'/></MultiTrigger>
        <Trigger Property='IsPressed' Value='True'><Setter TargetName='Surface' Property='Background' Value='#E5ECF5'/></Trigger>
        <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='#6E97CB'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='Toggle' TargetType='CheckBox'>
    <Setter Property='Foreground' Value='#283343'/><Setter Property='Height' Value='44'/>
    <Setter Property='Cursor' Value='Hand'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='CheckBox'>
      <Border x:Name='Surface' Background='Transparent' CornerRadius='8' Padding='12,0' BorderBrush='Transparent' BorderThickness='1'>
        <Grid>
          <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='36'/></Grid.ColumnDefinitions>
          <ContentPresenter VerticalAlignment='Center'/>
          <Border x:Name='Track' Grid.Column='1' Width='36' Height='21' Background='#B8C2D0' CornerRadius='11' VerticalAlignment='Center'>
            <Ellipse x:Name='Thumb' Width='17' Height='17' Fill='White' HorizontalAlignment='Left' Margin='2,0'/>
          </Border>
        </Grid>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsChecked' Value='True'><Setter TargetName='Track' Property='Background' Value='#2463AE'/><Setter TargetName='Thumb' Property='HorizontalAlignment' Value='Right'/></Trigger>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='#F0F3F7'/></Trigger>
        <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='#6E97CB'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
</ResourceDictionary>");

        internal readonly Button VisibilityButton, BasketButton, TextButton, AppearanceButton, ExitButton, MoreButton;
        internal readonly CheckBox Pictures, Text, AutoHang, ShowBasket;
        internal readonly LightMenu MoreMenu = new LightMenu("更多操作", "");
        internal readonly TextBlock Shortcut = new TextBlock { FontSize = 10, Foreground = LightTheme.Brush("#65717F"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };

        internal TrayPanel()
        {
            Width = 360; FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
            Foreground = LightTheme.Brush("#283343"); HasDropShadow = false; SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            var panel = new StackPanel { Margin = new Thickness(8) };
            KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetDirectionalNavigation(panel, KeyboardNavigationMode.Cycle);
            var heading = new Grid { Height = 42, Margin = new Thickness(8, 0, 12, 5) };
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PicRope.Icon"))
            {
                var logo = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.OrderByDescending(frame => frame.PixelWidth).First();
                heading.Children.Add(new Image { Source = logo, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left });
            }
            heading.Children.Add(new TextBlock { Text = "图绳", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(39, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            heading.Children.Add(new TextBlock { Text = "v" + AppUpdates.CurrentVersion, FontSize = 11, Foreground = LightTheme.Brush("#65717F"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(heading);

            VisibilityButton = Action("显示 / 收起", "show"); VisibilityButton.Tag = "primary";
            ((Grid)VisibilityButton.Content).Children.Add(Shortcut); panel.Children.Add(VisibilityButton);
            BasketButton = Action("图片篮子", "folder"); TextButton = Action("文字信封", "envelope");
            var entries = Pair(BasketButton, TextButton); entries.Margin = new Thickness(0, 5, 0, 2); panel.Children.Add(entries);
            ShowBasket = Toggle("显示图片篮子", "folder", "显示绳子上的图片篮子");
            ShowBasket.ToolTip = "关闭后只隐藏绳子上的篮子，仍可从托盘打开图片篮子";
            panel.Children.Add(ShowBasket);
            panel.Children.Add(Divider());
            panel.Children.Add(new TextBlock { Text = "剪贴板收集", FontSize = 11, Foreground = LightTheme.Brush("#65717F"), Margin = new Thickness(12, 5, 0, 3) });
            Pictures = Toggle("图片", "picture", "收集剪贴板图片"); Text = Toggle("文字", "text", "收集剪贴板文字");
            panel.Children.Add(Pair(Pictures, Text));
            AutoHang = Toggle("新截图自动挂上", "show", "新截图自动挂上"); panel.Children.Add(AutoHang);
            panel.Children.Add(Divider());
            MoreButton = Action("更多操作", "more");
            var arrow = Icon("chevron"); arrow.Width = 13; arrow.Height = 13; arrow.HorizontalAlignment = HorizontalAlignment.Right;
            ((Grid)MoreButton.Content).Children.Add(arrow); panel.Children.Add(MoreButton);
            panel.Children.Add(Divider());
            AppearanceButton = Action("外观", "appearance"); ExitButton = Action("退出", "exit");
            ExitButton.HorizontalAlignment = HorizontalAlignment.Right; ExitButton.Width = 84;
            panel.Children.Add(Pair(AppearanceButton, ExitButton, false));

            var surface = new FrameworkElementFactory(typeof(Border));
            surface.SetValue(Border.MarginProperty, new Thickness(16));
            surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
            surface.SetValue(Border.BackgroundProperty, Brushes.White);
            surface.SetValue(Border.BorderBrushProperty, LightTheme.Brush("#E0E4EA"));
            surface.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            surface.SetValue(Border.EffectProperty, new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(38, 57, 76), BlurRadius = 22, ShadowDepth = 6, Opacity = .16 });
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            Items.Add(panel);
            content.SetBinding(ContentPresenter.ContentProperty, new Binding("Items[0]") { RelativeSource = RelativeSource.TemplatedParent }); surface.AppendChild(content);
            Template = new ControlTemplate(typeof(ContextMenu)) { VisualTree = surface };
            MoreButton.Click += delegate
            {
                MoreMenu.PlacementTarget = MoreButton; MoreMenu.Placement = PlacementMode.Left;
                MoreMenu.HorizontalOffset = -6; MoreMenu.VerticalOffset = -16;
                MoreMenu.IsOpen = !MoreMenu.IsOpen;
            };
            MoreMenu.AddHandler(MenuItem.ClickEvent, new RoutedEventHandler(delegate { IsOpen = false; }));
            MoreMenu.PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key != Key.Escape) return;
                MoreMenu.IsOpen = false; MoreButton.Focus(); args.Handled = true;
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                var toggle = args.OriginalSource as CheckBox;
                if (args.Key == Key.Space && toggle != null)
                {
                    // 避免原生复选框在空格切换时释放鼠标捕获，导致托盘面板关闭。
                    if (!args.IsRepeat) toggle.IsChecked = toggle.IsChecked != true;
                    args.Handled = true; return;
                }
                if (args.Key != Key.Escape) return;
                if (MoreMenu.IsOpen) { MoreMenu.IsOpen = false; MoreButton.Focus(); }
                else Close();
                args.Handled = true;
            };
            Closed += delegate { MoreMenu.IsOpen = false; };
            Opened += delegate { VisibilityButton.Focus(); };
        }

        internal void OnAction(Button button, Action action)
        {
            button.Click += delegate { Close(); action(); };
        }

        internal void OnToggle(CheckBox toggle, bool initial, Action<bool> changed)
        {
            toggle.IsChecked = initial;
            RoutedEventHandler handler = delegate { changed(toggle.IsChecked == true); };
            toggle.Checked += handler; toggle.Unchecked += handler;
        }

        internal void Close() { MoreMenu.IsOpen = false; IsOpen = false; }

        static Button Action(string title, string icon)
        {
            var button = new Button { Content = Label(title, icon), Style = (Style)styles["Action"] };
            AutomationProperties.SetName(button, title); return button;
        }

        static CheckBox Toggle(string title, string icon, string name)
        {
            var toggle = new CheckBox { Content = Label(title, icon), Style = (Style)styles["Toggle"] };
            AutomationProperties.SetName(toggle, name); return toggle;
        }

        static Grid Label(string title, string icon)
        {
            var grid = new Grid();
            var image = Icon(icon); image.HorizontalAlignment = HorizontalAlignment.Left;
            grid.Children.Add(image);
            grid.Children.Add(new TextBlock { Text = title, Margin = new Thickness(34, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            return grid;
        }

        static Viewbox Icon(string name)
        {
            var path = new Path { Data = LightMenu.IconGeometry(name), StrokeThickness = 1.65, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            path.SetBinding(Shape.StrokeProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1) });
            return new Viewbox { Width = 19, Height = 19, Child = path };
        }

        static Grid Pair(UIElement left, UIElement right, bool divider = true)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(left); Grid.SetColumn(right, 1); grid.Children.Add(right);
            if (divider) grid.Children.Add(new Border { Width = 1, Height = 20, Background = LightTheme.Brush("#E0E4EA"), HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false });
            return grid;
        }

        static Border Divider() { return new Border { Height = 1, Background = LightTheme.Brush("#E8EBEF"), Margin = new Thickness(4, 5, 4, 5) }; }
    }
}
