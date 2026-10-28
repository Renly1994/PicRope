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

        readonly TextBlock details;
        internal readonly Dictionary<ShotAction, Button> Actions = new Dictionary<ShotAction, Button>();

        internal ImageMenu(Action<ShotAction> execute)
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
                    (previous ? LastAction() : Actions[ShotAction.Open]).Focus(); args.Handled = true;
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
        }

        internal void SetDetails(string value) { details.Text = value; }

        // 监控文件夹中的图片不允许删除源文件，隐藏回收站入口，删除统一退化为取下。
        internal void SetTrashEnabled(bool enabled)
        {
            Actions[ShotAction.Trash].Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        }

        Button LastAction()
        {
            Button found = Actions[ShotAction.Open];
            foreach (var button in Actions.Values)
                if (button.Visibility == Visibility.Visible) found = button;
            return found;
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
