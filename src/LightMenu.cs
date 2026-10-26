using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ScreenLine
{
    internal sealed class LightMenu : ContextMenu
    {
        static readonly ResourceDictionary theme = (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='Menu' TargetType='ContextMenu'>
    <Setter Property='Width' Value='276'/><Setter Property='FontFamily' Value='Segoe UI'/>
    <Setter Property='FontSize' Value='13'/><Setter Property='Foreground' Value='#283343'/>
    <Setter Property='HasDropShadow' Value='False'/><Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ContextMenu'>
      <Border Margin='16' Padding='6' Background='White' BorderBrush='#E0E4EA' BorderThickness='1' CornerRadius='14'>
        <Border.Effect><DropShadowEffect Color='#26394C' BlurRadius='22' ShadowDepth='6' Opacity='0.16'/></Border.Effect>
        <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>
      </Border>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='Row' TargetType='MenuItem'>
    <Setter Property='Background' Value='Transparent'/><Setter Property='Foreground' Value='#283343'/>
    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='MenuItem'>
      <Border x:Name='Row' Height='36' Padding='11,0' CornerRadius='7' Background='{TemplateBinding Background}'>
        <Grid VerticalAlignment='Center'>
          <Grid.ColumnDefinitions><ColumnDefinition Width='18'/><ColumnDefinition Width='12'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
          <ContentPresenter ContentSource='Icon' VerticalAlignment='Center'/>
          <ContentPresenter Grid.Column='2' ContentSource='Header' RecognizesAccessKey='True' VerticalAlignment='Center'/>
          <StackPanel Grid.Column='3' Orientation='Horizontal' VerticalAlignment='Center'>
            <TextBlock Text='{TemplateBinding InputGestureText}' Foreground='#65717F' FontSize='10' Margin='10,0,0,0' VerticalAlignment='Center'/>
            <Border x:Name='CheckBox' Visibility='Collapsed' Width='15' Height='15' Margin='12,0,0,0' CornerRadius='4' BorderThickness='1' BorderBrush='#CAD3DF' Background='#F5F7FA'>
              <Path x:Name='Check' Visibility='Collapsed' Data='M3,8 L6,11 L12,4' Stroke='White' StrokeThickness='1.5' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>
            </Border>
          </StackPanel>
        </Grid>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='Tag' Value='primary'><Setter TargetName='Row' Property='Background' Value='#EDF4FF'/><Setter Property='Foreground' Value='#2463AE'/></Trigger>
        <Trigger Property='Tag' Value='danger'><Setter Property='Foreground' Value='#BB514E'/></Trigger>
        <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Row' Property='Background' Value='#F0F3F7'/></Trigger>
        <MultiTrigger><MultiTrigger.Conditions><Condition Property='Tag' Value='primary'/><Condition Property='IsHighlighted' Value='True'/></MultiTrigger.Conditions><Setter TargetName='Row' Property='Background' Value='#E0ECFF'/></MultiTrigger>
        <MultiTrigger><MultiTrigger.Conditions><Condition Property='Tag' Value='danger'/><Condition Property='IsHighlighted' Value='True'/></MultiTrigger.Conditions><Setter TargetName='Row' Property='Background' Value='#FFF0ED'/></MultiTrigger>
        <Trigger Property='IsCheckable' Value='True'><Setter TargetName='CheckBox' Property='Visibility' Value='Visible'/></Trigger>
        <Trigger Property='IsChecked' Value='True'><Setter TargetName='CheckBox' Property='Background' Value='#2463AE'/><Setter TargetName='CheckBox' Property='BorderBrush' Value='#2463AE'/><Setter TargetName='Check' Property='Visibility' Value='Visible'/></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Row' Property='Opacity' Value='0.45'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='Separator' TargetType='Separator'>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Separator'><Border Height='1' Background='#E8EBEF' Margin='8,7'/></ControlTemplate></Setter.Value></Setter>
  </Style>
  <ControlTemplate x:Key='Note' TargetType='MenuItem'><ContentPresenter ContentSource='Header'/></ControlTemplate>
</ResourceDictionary>");

        static readonly Dictionary<string, string> icons = new Dictionary<string, string>
        {
            { "copy", "M10,8 H18 Q20,8 20,10 V19 Q20,21 18,21 H10 Q8,21 8,19 V10 Q8,8 10,8 Z M16,8 V5 Q16,3 14,3 H5 Q3,3 3,5 V14 Q3,16 5,16 H8" },
            { "open", "M9,4 H5 Q3,4 3,6 V19 Q3,21 5,21 H18 Q20,21 20,19 V15 M14,3 H21 V10 M21,3 L10,14" },
            { "edit", "M15,5 L19,9 M4,20 L8,19 L20,7 Q22,5 20,3 Q18,1 16,3 L4,15 L3,21 L8,19" },
            { "save", "M12,3 V15 M8,11 L12,15 L16,11 M4,15 V19 Q4,21 6,21 H18 Q20,21 20,19 V15" },
            { "folder", "M3,7 V5 Q3,3 5,3 H9 L12,6 H19 Q21,6 21,8 V9 M3,7 H19 Q21,7 21,9 L19,19 Q19,21 17,21 H5 Q3,21 3,19 Z" },
            { "trash", "M3,6 H21 M9,6 V4 Q9,3 10,3 H14 Q15,3 15,4 V6 M5,6 L6,20 Q6,21 7,21 H17 Q18,21 18,20 L19,6 M10,10 V17 M14,10 V17" },
            { "show", "M3,5 H21 M6,5 V19 H18 V5 M9,10 H15 M9,14 H13" },
            { "monitor", "M4,3 H20 Q22,3 22,5 V15 Q22,17 20,17 H4 Q2,17 2,15 V5 Q2,3 4,3 Z M12,17 V21 M7,21 H17" },
            { "clipboard", "M9,3 H15 V7 H9 Z M9,5 H6 Q4,5 4,7 V19 Q4,21 6,21 H18 Q20,21 20,19 V7 Q20,5 18,5 H15 M8,12 H16 M8,16 H14" },
            { "add", "M14,3 H5 Q3,3 3,5 V19 Q3,21 5,21 H19 Q21,21 21,19 V12 M3,17 L8,12 L12,16 L15,13 L21,19 M17,6 H23 M20,3 V9" },
            { "clear", "M6,3 H19 Q21,3 21,5 V16 M5,7 H15 Q17,7 17,9 V19 Q17,21 15,21 H5 Q3,21 3,19 V9 Q3,7 5,7 Z M7,14 H13" },
            { "startup", "M12,3 L19,10 H14 V21 H10 V10 H5 Z" },
            { "appearance", "M12,3 A9,9 0 1 0 12,21 H14 Q17,21 17,18 Q17,16 15,16 H14 Q12,16 12,14 Q12,12 14,12 H19 Q22,12 21,9 Q20,3 12,3 Z M7,8 H7.1 M11,6 H11.1 M16,7 H16.1 M6,13 H6.1" },
            { "exit", "M6,6 A8,8 0 1 0 18,6 M12,3 V12" },
            { "remove", "M4,12 H20" },
            { "envelope", "M5,4 H19 Q21,4 21,6 V18 Q21,20 19,20 H5 Q3,20 3,18 V6 Q3,4 5,4 Z M3,6 L12,13 L21,6" },
            { "picture", "M5,3 H19 Q21,3 21,5 V19 Q21,21 19,21 H5 Q3,21 3,19 V5 Q3,3 5,3 Z M3,16 L8,11 L13,16 L17,12 L21,16 M16,7 H18" },
            { "text", "M6,3 H18 Q20,3 20,5 V19 Q20,21 18,21 H6 Q4,21 4,19 V5 Q4,3 6,3 Z M8,9 H16 M8,14 H13" },
            { "more", "M21,12 A9,9 0 1 0 3,12 A9,9 0 1 0 21,12 M7,12 H7.1 M12,12 H12.1 M17,12 H17.1" },
            { "chevron", "M9,4 L16,12 L9,20" },
            { "left", "M15,5 L8,12 L15,19 M8,12 H20" },
            { "right", "M9,5 L16,12 L9,19 M4,12 H16" }
        };

        readonly TextBlock details;

        internal static Geometry IconGeometry(string name)
        {
            var geometry = Geometry.Parse(icons[name]);
            geometry.Freeze();
            return geometry;
        }

        public LightMenu(string title, string detail)
        {
            Style = (Style)theme["Menu"];
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            var heading = new Grid { Margin = new Thickness(11, 9, 11, 13) };
            heading.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brush("#536172") });
            details = new TextBlock { Text = detail, FontSize = 11, Foreground = Brush("#65717F"), HorizontalAlignment = HorizontalAlignment.Right };
            heading.Children.Add(details);
            Note(heading);
        }

        public void SetDetails(string value) { details.Text = value; }

        public MenuItem AddAction(string title, string icon, Action action, bool primary = false, bool danger = false)
        {
            var entry = new MenuItem
            {
                Header = title, Style = (Style)theme["Row"], Tag = danger ? "danger" : (primary ? "primary" : null),
                Icon = new Viewbox
                {
                    Width = 18, Height = 18,
                    Child = new Path { Data = Geometry.Parse(icons[icon]), Stroke = Brush(danger ? "#BB514E" : (primary ? "#2463AE" : "#667487")), StrokeThickness = 1.65, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round }
                }
            };
            entry.Click += delegate { action(); };
            Items.Add(entry);
            return entry;
        }

        public void Separator() { Items.Add(new Separator { Style = (Style)theme["Separator"] }); }

        public void Footer(string text)
        {
            Note(new TextBlock { Text = text, FontSize = 10, Foreground = Brush("#65717F"), Margin = new Thickness(11, 12, 11, 8) });
        }

        void Note(UIElement content)
        {
            Items.Add(new MenuItem { Header = content, IsEnabled = false, Focusable = false, Template = (ControlTemplate)theme["Note"] });
        }

        static SolidColorBrush Brush(string value) { return (SolidColorBrush)new BrushConverter().ConvertFromString(value); }
    }
}
