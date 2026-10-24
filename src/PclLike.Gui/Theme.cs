using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PclLike.Gui;

/// <summary>启动器整体配色的集中定义，跟 App.xaml 里的资源保持一致。</summary>
public static class Theme
{
    // 暗色底：参照 PCL2 的深灰蓝
    public static readonly Brush Background = Freeze("#1B1B1F");
    public static readonly Brush Surface = Freeze("#25252B");
    public static readonly Brush SurfaceHi = Freeze("#2E2E36");
    public static readonly Brush Border = Freeze("#3A3A44");
    public static readonly Brush Foreground = Freeze("#E8E8EC");
    public static readonly Brush Muted = Freeze("#9A9AA6");

    // 强调色：PCL2 用的那种青蓝
    public static readonly Brush Accent = Freeze("#22A2C2");
    public static readonly Brush AccentHi = Freeze("#2FBCE0");
    public static readonly Brush Danger = Freeze("#E05252");
    public static readonly Brush Warning = Freeze("#D9A03C");
    public static readonly Brush Ok = Freeze("#4CC26A");

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>布尔取反，给"没有选择版本时禁用启动按钮"这类绑定用。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

/// <summary>bool → Visibility：true 显示，false 折叠。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>版本类型 → 中文标签（release / snapshot / old_beta ...）。</summary>
public sealed class VersionTypeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value as string) switch
        {
            "release" => "正式版",
            "snapshot" => "快照",
            "old_beta" => "远古 Beta",
            "old_alpha" => "远古 Alpha",
            null or "" => "",
            var s => s,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}