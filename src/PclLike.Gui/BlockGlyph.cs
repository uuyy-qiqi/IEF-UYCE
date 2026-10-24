using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PclLike.Gui;

/// <summary>
/// 等距立方体图标 —— 下载页里代表"一个方块/一个版本"的那个小图标。
///
/// PCL 每个版本分类配一个不同颜色的方块图标，比纯色方块有辨识度。
/// 这里画三个菱形面拼成等距立方体：顶面、左面、右面各一个刷子，
/// 用三个颜色就能得到有立体感的方块。
///
/// 默认样式在 Theme.Pcl.xaml 里（TargetType 无 Key）。
/// </summary>
public sealed class BlockGlyph : Control
{
    static BlockGlyph()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(BlockGlyph),
            new FrameworkPropertyMetadata(typeof(BlockGlyph)));
    }

    // ── 顶面（最亮）────────────────────────────────────────────
    public static readonly DependencyProperty TopBrushProperty =
        DependencyProperty.Register(nameof(TopBrush), typeof(Brush), typeof(BlockGlyph),
            new PropertyMetadata(Brushes.Gray));

    public Brush TopBrush
    {
        get => (Brush)GetValue(TopBrushProperty);
        set => SetValue(TopBrushProperty, value);
    }

    // ── 左面（中间调）──────────────────────────────────────────
    public static readonly DependencyProperty LeftBrushProperty =
        DependencyProperty.Register(nameof(LeftBrush), typeof(Brush), typeof(BlockGlyph),
            new PropertyMetadata(Brushes.DimGray));

    public Brush LeftBrush
    {
        get => (Brush)GetValue(LeftBrushProperty);
        set => SetValue(LeftBrushProperty, value);
    }

    // ── 右面（最暗）────────────────────────────────────────────
    public static readonly DependencyProperty RightBrushProperty =
        DependencyProperty.Register(nameof(RightBrush), typeof(Brush), typeof(BlockGlyph),
            new PropertyMetadata(Brushes.Black));

    public Brush RightBrush
    {
        get => (Brush)GetValue(RightBrushProperty);
        set => SetValue(RightBrushProperty, value);
    }
}