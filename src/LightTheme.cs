using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ScreenLine
{
    internal static class LightTheme
    {
        static readonly ResourceDictionary styles = (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='Action' TargetType='Button'>
    <Setter Property='FontFamily' Value='Microsoft YaHei UI'/><Setter Property='FontSize' Value='13'/>
    <Setter Property='Foreground' Value='#343D49'/><Setter Property='Background' Value='White'/>
    <Setter Property='BorderBrush' Value='#DCE2EA'/><Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='14,0'/><Setter Property='Height' Value='34'/><Setter Property='MinWidth' Value='80'/>
    <Setter Property='Cursor' Value='Hand'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
      <Border x:Name='Surface' CornerRadius='7' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>
        <ContentPresenter Margin='{TemplateBinding Padding}' HorizontalAlignment='Center' VerticalAlignment='Center' RecognizesAccessKey='True'/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='#F0F2F5'/></Trigger>
        <Trigger Property='IsPressed' Value='True'><Setter TargetName='Surface' Property='Background' Value='#E8EDF3'/></Trigger>
        <Trigger Property='Tag' Value='primary'><Setter Property='Background' Value='#2463AE'/><Setter Property='BorderBrush' Value='#2463AE'/><Setter Property='Foreground' Value='White'/><Setter Property='FontWeight' Value='SemiBold'/></Trigger>
        <MultiTrigger><MultiTrigger.Conditions><Condition Property='Tag' Value='primary'/><Condition Property='IsMouseOver' Value='True'/></MultiTrigger.Conditions><Setter TargetName='Surface' Property='Background' Value='#1B5498'/></MultiTrigger>
        <Trigger Property='Tag' Value='quiet'><Setter Property='BorderBrush' Value='Transparent'/><Setter Property='Background' Value='Transparent'/></Trigger>
        <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='#6E97CB'/><Setter TargetName='Surface' Property='BorderThickness' Value='2'/></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Surface' Property='Opacity' Value='0.4'/><Setter Property='Cursor' Value='Arrow'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='Hint' TargetType='ToolTip'>
    <Setter Property='FontFamily' Value='Microsoft YaHei UI'/><Setter Property='FontSize' Value='12'/><Setter Property='Foreground' Value='#536172'/>
    <Setter Property='MaxWidth' Value='320'/><Setter Property='HasDropShadow' Value='False'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ToolTip'>
      <Border Background='White' BorderBrush='#E0E4EA' BorderThickness='1' CornerRadius='7' Padding='10,7' Margin='8'>
        <Border.Effect><DropShadowEffect BlurRadius='12' ShadowDepth='2' Opacity='0.12'/></Border.Effect>
        <TextBlock Text='{TemplateBinding Content}' TextWrapping='Wrap'/>
      </Border>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
</ResourceDictionary>");

        public static Style ButtonStyle { get { return (Style)styles["Action"]; } }
        public static Style HintStyle { get { return (Style)styles["Hint"]; } }
        public static SolidColorBrush Brush(string value) { return (SolidColorBrush)new BrushConverter().ConvertFromString(value); }
        public static void Install(Application app)
        {
            // 小型透明挂件使用软件绘制，避免反复开关面板积累大块图形缓存。
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            app.Resources[typeof(ToolTip)] = styles["Hint"];
        }

        public static UIElement CloseIcon()
        {
            return new System.Windows.Shapes.Path {
                Width = 8, Height = 8, Data = Geometry.Parse("M0,0 L8,8 M8,0 L0,8"),
                Stroke = Brush("#343D49"), StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            };
        }

        public static Button Button(string title, Action action, string tag = null)
        {
            var button = new Button { Content = title, Style = ButtonStyle, Tag = tag };
            button.Click += delegate { action(); };
            return button;
        }

        public static Border Card(UIElement content)
        {
            return new Border {
                Child = content, Margin = new Thickness(16), Padding = new Thickness(24), CornerRadius = new CornerRadius(14),
                Background = Brushes.White, BorderBrush = Brush("#E0E4EA"), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { Color = Color.FromRgb(38, 57, 76), BlurRadius = 22, ShadowDepth = 6, Opacity = 0.16 }
            };
        }
    }
}
