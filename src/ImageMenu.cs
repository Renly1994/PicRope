using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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

        static readonly Style bulkStyle = (Style)XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='MenuItem'>
  <Setter Property='Foreground' Value='#283343'/><Setter Property='Cursor' Value='Hand'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='MenuItem'>
    <Grid>
      <Border x:Name='Row' Height='32' Padding='10,0' CornerRadius='7' Background='Transparent'>
        <Grid><Grid.ColumnDefinitions><ColumnDefinition Width='18'/><ColumnDefinition Width='*'/><ColumnDefinition Width='12'/></Grid.ColumnDefinitions>
          <Path Data='M4,12 H20' Stroke='#283343' StrokeThickness='1.65' Width='18' Height='18' Stretch='Uniform'/>
          <ContentPresenter Grid.Column='1' ContentSource='Header' Margin='10,0,0,0' VerticalAlignment='Center'/>
          <Path Grid.Column='2' Data='M9,4 L16,12 L9,20' Stroke='#65717F' StrokeThickness='1.65' Width='12' Height='12' Stretch='Uniform'/>
        </Grid>
      </Border>
      <Popup x:Name='PART_Popup' Placement='Right' AllowsTransparency='True' Focusable='False' IsOpen='{TemplateBinding IsSubmenuOpen}'>
        <Border Width='180' Padding='4' Background='White' BorderBrush='#E0E4EA' BorderThickness='1' CornerRadius='9'>
          <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>
        </Border>
      </Popup>
    </Grid>
    <ControlTemplate.Triggers>
      <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Row' Property='Background' Value='#F0F3F7'/></Trigger>
      <Trigger Property='IsSubmenuOpen' Value='True'><Setter TargetName='Row' Property='Background' Value='#F0F3F7'/></Trigger>
      <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Row' Property='Background' Value='#EDF4FF'/></Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
</Style>");

        static readonly Style bulkActionStyle = (Style)XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='MenuItem'>
  <Setter Property='Foreground' Value='#283343'/><Setter Property='Cursor' Value='Hand'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='MenuItem'>
    <Border x:Name='Row' Height='32' Padding='12,0' CornerRadius='7' Background='Transparent'>
      <ContentPresenter ContentSource='Header' VerticalAlignment='Center'/>
    </Border>
    <ControlTemplate.Triggers>
      <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Row' Property='Background' Value='#F0F3F7'/></Trigger>
      <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
</Style>");

        readonly TextBlock details;
        internal readonly Dictionary<ShotAction, Button> Actions = new Dictionary<ShotAction, Button>();
        internal readonly MenuItem BulkMenu = new MenuItem { Header = "批量取下" };
        internal readonly Dictionary<ShotAction, MenuItem> BulkActions = new Dictionary<ShotAction, MenuItem>();
        readonly DispatcherTimer bulkClose = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        internal ImageMenu(Action<ShotAction> execute, bool managed)
        {
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
            var bulk = new Menu { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
            BulkMenu.Style = bulkStyle; BulkMenu.Width = 180;
            BulkMenu.FontFamily = FontFamily; BulkMenu.FontSize = FontSize;
            foreach (var entry in new[] {
                new KeyValuePair<ShotAction, string>(ShotAction.RemoveLeft, "取下左侧全部"),
                new KeyValuePair<ShotAction, string>(ShotAction.RemoveRight, "取下右侧全部"),
                new KeyValuePair<ShotAction, string>(ShotAction.RemoveOthers, "仅保留这一张"),
                new KeyValuePair<ShotAction, string>(ShotAction.RemoveAll, "取下全部") })
            {
                ShotAction action = entry.Key;
                var child = new MenuItem { Header = entry.Value, Style = bulkActionStyle };
                child.Click += delegate { IsOpen = false; execute(action); };
                child.MouseEnter += delegate { bulkClose.Stop(); };
                child.MouseLeave += delegate { bulkClose.Start(); };
                BulkActions.Add(action, child); BulkMenu.Items.Add(child);
            }
            BulkMenu.MouseEnter += delegate { bulkClose.Stop(); BulkMenu.IsSubmenuOpen = true; };
            BulkMenu.MouseLeave += delegate { if (BulkMenu.IsSubmenuOpen) bulkClose.Start(); };
            BulkMenu.PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.OriginalSource == BulkMenu && (args.Key == Key.Right || args.Key == Key.Enter || args.Key == Key.Space))
                {
                    BulkMenu.IsSubmenuOpen = true;
                    foreach (var child in BulkActions.Values) if (child.IsEnabled) { child.Focus(); break; }
                    args.Handled = true;
                }
            };
            bulkClose.Tick += delegate { bulkClose.Stop(); BulkMenu.IsSubmenuOpen = false; };
            bulk.Items.Add(BulkMenu); panel.Children.Add(bulk);
            panel.Children.Add(Action(managed ? "移到回收站" : "移除记录（保留原图）", "trash", ShotAction.Trash, execute));
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
            Opened += delegate { Focus(); };
            Closed += delegate { bulkClose.Stop(); BulkMenu.IsSubmenuOpen = false; };
        }

        internal void SetDetails(string value) { details.Text = value; }

        internal void SetBulk(int index, int count)
        {
            BulkActions[ShotAction.RemoveLeft].IsEnabled = index > 0;
            BulkActions[ShotAction.RemoveRight].IsEnabled = index >= 0 && index < count - 1;
            BulkActions[ShotAction.RemoveOthers].IsEnabled = index >= 0 && count > 1;
            BulkActions[ShotAction.RemoveAll].IsEnabled = count > 0;
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
