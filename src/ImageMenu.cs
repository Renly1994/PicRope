using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ScreenLine
{
    internal sealed class ImageMenu : ContextMenu
    {
        static readonly Style actionStyle = (Style)XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
  <Setter Property='Background' Value='Transparent'/><Setter Property='Height' Value='32'/><Setter Property='Padding' Value='10,0'/>
  <Setter Property='Cursor' Value='Hand'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
    <Border x:Name='Surface' Background='{TemplateBinding Background}' CornerRadius='7' BorderBrush='Transparent' BorderThickness='1'>
      <ContentPresenter Margin='{TemplateBinding Padding}' VerticalAlignment='Center'/>
    </Border>
    <ControlTemplate.Triggers>
      <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='#F0F3F7'/></Trigger>
      <MultiTrigger><MultiTrigger.Conditions><Condition Property='Tag' Value='danger'/><Condition Property='IsMouseOver' Value='True'/></MultiTrigger.Conditions><Setter TargetName='Surface' Property='Background' Value='#FFF0ED'/></MultiTrigger>
      <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='#6E97CB'/></Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
</Style>");

        static readonly Style bulkParentStyle = (Style)XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='MenuItem'>
  <Setter Property='Foreground' Value='#283343'/><Setter Property='Cursor' Value='Hand'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='MenuItem'>
    <Grid>
      <Border x:Name='Surface' Height='32' Padding='10,0' CornerRadius='7' BorderBrush='Transparent' BorderThickness='1' Background='Transparent'>
        <Grid>
          <Grid.ColumnDefinitions><ColumnDefinition Width='18'/><ColumnDefinition Width='*'/><ColumnDefinition Width='12'/></Grid.ColumnDefinitions>
          <Rectangle x:Name='Minus' Width='14' Height='1.6' RadiusX='0.8' RadiusY='0.8' Fill='#283343' VerticalAlignment='Center' HorizontalAlignment='Center'/>
          <ContentPresenter Grid.Column='1' ContentSource='Header' Margin='10,0,0,0' VerticalAlignment='Center'/>
          <Path x:Name='Chevron' Grid.Column='2' Width='12' Height='12' Stretch='Uniform' Data='M8,5 L15,12 L8,19' Stroke='#8A96A6' StrokeThickness='1.6' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' VerticalAlignment='Center' HorizontalAlignment='Right'/>
        </Grid>
      </Border>
      <Popup x:Name='PART_Popup' Placement='Right' HorizontalOffset='5' VerticalOffset='-6' AllowsTransparency='True' Focusable='False' PopupAnimation='None' IsOpen='{TemplateBinding IsSubmenuOpen}'>
        <Border Margin='8' Padding='4' Width='188' Background='White' BorderBrush='#E0E4EA' BorderThickness='1' CornerRadius='12'>
          <Border.Effect><DropShadowEffect Color='#26394C' BlurRadius='20' ShadowDepth='5' Opacity='0.16'/></Border.Effect>
          <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>
        </Border>
      </Popup>
    </Grid>
    <ControlTemplate.Triggers>
      <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Surface' Property='Background' Value='#F0F3F7'/></Trigger>
      <Trigger Property='IsSubmenuOpen' Value='True'><Setter TargetName='Surface' Property='Background' Value='#F0F3F7'/></Trigger>
      <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/><Setter Property='Cursor' Value='Arrow'/></Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
</Style>");

        static readonly Style bulkActionStyle = (Style)XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='MenuItem'>
  <Setter Property='Foreground' Value='#283343'/><Setter Property='Cursor' Value='Hand'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='MenuItem'>
    <Border x:Name='Row' Height='32' Padding='10,0' CornerRadius='7' Background='Transparent'>
      <Grid>
        <Grid.ColumnDefinitions><ColumnDefinition Width='18'/><ColumnDefinition Width='10'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
        <ContentPresenter ContentSource='Icon' VerticalAlignment='Center'/>
        <ContentPresenter Grid.Column='2' ContentSource='Header' VerticalAlignment='Center'/>
      </Grid>
    </Border>
    <ControlTemplate.Triggers>
      <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Row' Property='Background' Value='#F0F3F7'/></Trigger>
      <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Row' Property='Opacity' Value='0.38'/></Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
</Style>");

        static readonly Style bulkSeparatorStyle = (Style)XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='MenuItem'>
  <Setter Property='Focusable' Value='False'/><Setter Property='IsEnabled' Value='False'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='MenuItem'>
    <Border Height='1' Margin='10,4' Background='#E8EBEF'/>
  </ControlTemplate></Setter.Value></Setter>
</Style>");

        readonly TextBlock details;
        readonly Func<BulkCounts> bulkCounts;
        readonly MenuItem removeLeft, removeRight, removeOthers, removeAll;
        readonly DispatcherTimer bulkClose = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        bool bulkHover, submenuHover;
        internal readonly MenuItem BulkMenu;
        internal readonly Dictionary<ShotAction, Button> Actions = new Dictionary<ShotAction, Button>();
        internal readonly Dictionary<ShotAction, MenuItem> BulkActions = new Dictionary<ShotAction, MenuItem>();

        internal ImageMenu(Action<ShotAction> execute, Func<BulkCounts> bulkCounts)
        {
            this.bulkCounts = bulkCounts;
            Width = 220; FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
            Foreground = LightTheme.Brush("#283343"); HasDropShadow = false; SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            var panel = new StackPanel();
            KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetDirectionalNavigation(panel, KeyboardNavigationMode.Cycle);
            var heading = new Grid { Margin = new Thickness(10, 5, 10, 7) };
            heading.Children.Add(new TextBlock { Text = "图片", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = LightTheme.Brush("#536172") });
            details = new TextBlock { FontSize = 11, Foreground = LightTheme.Brush("#65717F"), HorizontalAlignment = HorizontalAlignment.Right };
            heading.Children.Add(details); panel.Children.Add(heading);
            panel.Children.Add(Action("打开", "open", ShotAction.Open, execute));
            panel.Children.Add(Action("编辑", "edit", ShotAction.Edit, execute));
            panel.Children.Add(Action("保存副本…", "save", ShotAction.Save, execute));
            panel.Children.Add(Action("打开位置", "folder", ShotAction.Locate, execute));
            panel.Children.Add(new Border { Height = 1, Background = LightTheme.Brush("#E8EBEF"), Margin = new Thickness(6, 4, 6, 4) });
            panel.Children.Add(Action("取下放篮子", "folder", ShotAction.Remove, execute));
            BulkMenu = new MenuItem { Header = "批量取下", Style = bulkParentStyle };
            AutomationProperties.SetName(BulkMenu, "批量取下");
            removeLeft = BulkItem("取下左侧全部", "left", ShotAction.RemoveLeft, execute);
            removeRight = BulkItem("取下右侧全部", "right", ShotAction.RemoveRight, execute);
            removeOthers = BulkItem("仅保留这一张", "picture", ShotAction.RemoveOthers, execute);
            removeAll = BulkItem("取下全部", "clear", ShotAction.RemoveAll, execute);
            BulkMenu.Items.Add(removeLeft);
            BulkMenu.Items.Add(removeRight);
            BulkMenu.Items.Add(removeOthers);
            BulkMenu.Items.Add(new MenuItem { Style = bulkSeparatorStyle, Focusable = false, IsEnabled = false });
            BulkMenu.Items.Add(removeAll);
            BulkMenu.MouseEnter += delegate { bulkHover = true; bulkClose.Stop(); OpenBulk(); };
            BulkMenu.MouseLeave += delegate { bulkHover = false; ScheduleBulkClose(); };
            BulkMenu.PreviewMouseLeftButtonUp += delegate { OpenBulk(); };
            BulkMenu.SubmenuOpened += delegate { PlaceBulkPopup(); };
            BulkMenu.PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Right || args.Key == Key.Enter || args.Key == Key.Space)
                {
                    OpenBulk();
                    MenuItem target = removeLeft.IsEnabled ? removeLeft
                        : removeRight.IsEnabled ? removeRight
                        : removeOthers.IsEnabled ? removeOthers : removeAll;
                    if (target.IsEnabled) target.Focus();
                    args.Handled = true;
                }
                else if (args.Key == Key.Left)
                {
                    BulkMenu.IsSubmenuOpen = false;
                    args.Handled = true;
                }
            };
            foreach (MenuItem entry in BulkActions.Values)
            {
                entry.MouseEnter += delegate { submenuHover = true; bulkClose.Stop(); };
                entry.MouseLeave += delegate { submenuHover = false; ScheduleBulkClose(); };
            }
            bulkClose.Tick += delegate
            {
                if (bulkHover || submenuHover) return;
                BulkMenu.IsSubmenuOpen = false;
                bulkClose.Stop();
            };
            panel.Children.Add(BulkMenu);
            panel.Children.Add(Action("移到回收站", "trash", ShotAction.Trash, execute));
            var surface = LightTheme.Card(panel); surface.Padding = new Thickness(4);
            Items.Add(surface);
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetBinding(ContentPresenter.ContentProperty, new Binding("Items[0]") { RelativeSource = RelativeSource.TemplatedParent });
            Template = new ControlTemplate(typeof(ContextMenu)) { VisualTree = content };
            PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Escape) { IsOpen = false; args.Handled = true; }
                else if (args.OriginalSource == this && (args.Key == Key.Down || args.Key == Key.Up || args.Key == Key.Tab))
                {
                    bool previous = args.Key == Key.Up || (args.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                    Actions[previous ? ShotAction.Trash : ShotAction.Open].Focus(); args.Handled = true;
                }
                else if (args.Key == Key.Space)
                {
                    var button = args.OriginalSource as Button;
                    if (button == null) return;
                    if (!args.IsRepeat) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    args.Handled = true;
                }
            };
            Opened += delegate { UpdateBulk(); BulkMenu.IsSubmenuOpen = false; Focus(); };
            Closed += delegate { bulkClose.Stop(); BulkMenu.IsSubmenuOpen = false; };
        }

        internal void SetDetails(string value) { details.Text = value; }

        void ScheduleBulkClose()
        {
            if (!BulkMenu.IsSubmenuOpen) return;
            bulkClose.Stop();
            bulkClose.Start();
        }

        void OpenBulk()
        {
            if (!BulkMenu.IsEnabled || BulkMenu.IsSubmenuOpen) return;
            PlaceBulkPopup();
            BulkMenu.IsSubmenuOpen = true;
        }

        void PlaceBulkPopup()
        {
            var popup = BulkMenu.Template.FindName("PART_Popup", BulkMenu) as Popup;
            if (popup == null) return;
            Point anchor = BulkMenu.PointToScreen(new Point(BulkMenu.ActualWidth, 0));
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)anchor.X, (int)anchor.Y));
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            bool fitsRight = screen.WorkingArea.Right - anchor.X >= 204 * scale;
            popup.Placement = fitsRight ? PlacementMode.Right : PlacementMode.Left;
            popup.HorizontalOffset = fitsRight ? 5 : -5;
        }

        void UpdateBulk()
        {
            BulkCounts counts = bulkCounts();
            int otherCount = counts.Others;
            BulkMenu.IsEnabled = otherCount > 0;
            if (!BulkMenu.IsEnabled) BulkMenu.IsSubmenuOpen = false;
            AutomationProperties.SetHelpText(BulkMenu, BulkMenu.IsEnabled ? (otherCount + 1) + " 张图片" : "至少需要两张图片才能批量取下");
            removeLeft.IsEnabled = counts.Left > 0;
            removeRight.IsEnabled = counts.Right > 0;
            removeOthers.IsEnabled = otherCount > 0;
            removeAll.IsEnabled = otherCount > 0;
            AutomationProperties.SetHelpText(removeLeft, counts.Left + " 张图片");
            AutomationProperties.SetHelpText(removeRight, counts.Right + " 张图片");
            AutomationProperties.SetHelpText(removeOthers, otherCount + " 张图片");
            AutomationProperties.SetHelpText(removeAll, (otherCount + 1) + " 张图片");
        }

        MenuItem BulkItem(string title, string icon, ShotAction action, Action<ShotAction> execute)
        {
            var item = new MenuItem
            {
                Header = title, Style = bulkActionStyle,
                Icon = new Viewbox
                {
                    Width = 18, Height = 18,
                    Child = new Path { Data = LightMenu.IconGeometry(icon), Stroke = LightTheme.Brush("#667487"), StrokeThickness = 1.65, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round }
                }
            };
            AutomationProperties.SetName(item, title);
            item.Click += delegate { IsOpen = false; execute(action); };
            BulkActions.Add(action, item);
            return item;
        }

        Button Action(string title, string icon, ShotAction action, Action<ShotAction> execute)
        {
            var color = LightTheme.Brush(action == ShotAction.Trash ? "#BB514E" : "#283343");
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new Viewbox { Width = 18, Height = 18, Child = new Path { Data = LightMenu.IconGeometry(icon), Stroke = color, StrokeThickness = 1.65, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round } });
            label.Children.Add(new TextBlock { Text = title, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var button = new Button {
                Content = label, Style = actionStyle, Tag = action == ShotAction.Trash ? "danger" : null,
                Foreground = color,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(button, title);
            button.Click += delegate { IsOpen = false; execute(action); };
            Actions.Add(action, button); return button;
        }

    }
}
