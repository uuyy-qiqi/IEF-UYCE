using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PclLike.Gui.Skins;

/// <summary>
/// 等距 3D 头部渲染控件。
///
/// 从 <see cref="SkinHead"/> 拿到的六块贴图（正面/侧面/顶面 + 各自的帽檐层），
/// 拼成一个等距投影的方块头 —— 效果和 PCL2 启动页那个头像一样。
///
/// 几何（设正面边长 = S）：
///   · 正面    矩形，位于 (S/2, S/4) 大小 S×S
///   · 顶面    贴图沿 X 方向压到 S 宽、沿 Y 压到 S/4 高，再向右斜切 S/2
///   · 右侧面  贴图沿 X 方向压到 S/2 宽、沿 Y 保持 S 高，再向右斜切 S/4 并上移
///   整体包围盒 2S × 1.5S
///
/// 实现上用 MatrixTransform 一次算好每个面的仿射矩阵 ——
/// 比手工切条带简单得多，也不会出现条带之间的接缝。
/// </summary>
public sealed class SkinHeadView : FrameworkElement
{
    public static readonly DependencyProperty HeadProperty =
        DependencyProperty.Register(
            nameof(Head), typeof(SkinHead), typeof(SkinHeadView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(
            nameof(Size), typeof(double), typeof(SkinHeadView),
            new FrameworkPropertyMetadata(64.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public SkinHead? Head
    {
        get => (SkinHead?)GetValue(HeadProperty);
        set => SetValue(HeadProperty, value);
    }

    /// <summary>正面那一块的边长（像素）。3D 模式整体宽度是它的 2 倍、高度 1.5 倍；平铺模式就是边长本身。</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public static readonly DependencyProperty FlatProperty =
        DependencyProperty.Register(
            nameof(Flat), typeof(bool), typeof(SkinHeadView),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// 平铺模式：只画正面那张脸（含帽檐层）填满整个正方形，不画等距的顶面和侧面。
    ///
    /// PCL CE 的头像就是这种平铺的 2D 脸 —— 8×8 像素直接最近邻放大成一块方脸。
    /// 为什么默认用它：等距 3D 在 76px 这种尺寸下，侧面被压到 38px 宽再斜切，
    /// 细节全糊在一起，看上去就是一团脏色；平铺的方脸干净得多。
    /// </summary>
    public bool Flat
    {
        get => (bool)GetValue(FlatProperty);
        set => SetValue(FlatProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var s = Size;
        if (s <= 0) return new Size(0, 0);
        return Flat ? new Size(s, s) : new Size(s * 2, s * 1.5);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var head = Head;
        var s = Size;
        if (head is null || s <= 0) return;

        // 像素画必须最近邻放大，否则 8×8 会糊成一团
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

        if (Flat)
        {
            var rect = new Rect(0, 0, s, s);
            dc.DrawImage(head.Face, rect);
            if (head.HatFace is not null)
                dc.DrawImage(head.HatFace, rect);

            var edge = new Pen(new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)), 1);
            edge.Freeze();
            dc.DrawRectangle(null, edge, rect);
            return;
        }

        var half = s / 2;
        var quarter = s / 4;

        // 正面矩形
        var fx0 = half;            // 正面左
        var fy0 = quarter;         // 正面上
        var fx1 = fx0 + s;         // 正面右
        var fy1 = fy0 + s;         // 正面下

        // ── 顶面 ──
        // 近边 = 正面顶边：从 (fx0, fy0) 到 (fx1, fy0)
        // 远边 = 近边整体平移 (+half, -quarter)：从 (s, 0) 到 (2s, 0)
        //
        // 贴图 8×8 的四个角按下面这样映射：
        //   贴图 (0,8) 左下 → 近边左端 (fx0, fy0)
        //   贴图 (8,8) 右下 → 近边右端 (fx1, fy0)
        //   贴图 (0,0) 左上 → 远边左端 (fx0+half, fy0-quarter)
        //   贴图 (8,0) 右上 → 远边右端 (fx1+half, fy0-quarter)
        DrawMappedFace(dc, head.Top,
            p00: (fx0 + half, fy0 - quarter),   // 贴图左上
            p10: (fx1 + half, fy0 - quarter),   // 贴图右上
            p01: (fx0, fy0),                    // 贴图左下
            p11: (fx1, fy0));                   // 贴图右下

        // 帽檐同理
        DrawMappedFace(dc, head.HatTop,
            p00: (fx0 + half, fy0 - quarter),
            p10: (fx1 + half, fy0 - quarter),
            p01: (fx0, fy0),
            p11: (fx1, fy0));

        // ── 右侧面 ──
        // 近边 = 正面右边：从 (fx1, fy0) 到 (fx1, fy1)
        // 远边 = 近边整体平移 (+half, -quarter)
        //   贴图 (0,0) 左上 → 远边上端 (fx1+half, fy0-quarter)
        //   贴图 (8,0) 右上 → 远边上端偏右，但侧面横向已被压到 half 宽
        // 侧面在等距投影里横向压成 half，所以：
        //   贴图 (0,0) → (fx1, fy0)            近边上端
        //   贴图 (8,0) → (fx1+half, fy0-quarter)  远边上端
        //   贴图 (0,8) → (fx1, fy1)            近边下端
        //   贴图 (8,8) → (fx1+half, fy1-quarter)  远边下端
        DrawMappedFace(dc, head.Side,
            p00: (fx1, fy0),
            p10: (fx1 + half, fy0 - quarter),
            p01: (fx1, fy1),
            p11: (fx1 + half, fy1 - quarter));

        DrawMappedFace(dc, head.HatSide,
            p00: (fx1, fy0),
            p10: (fx1 + half, fy0 - quarter),
            p01: (fx1, fy1),
            p11: (fx1 + half, fy1 - quarter));

        // ── 3. 正面（不斜切，最后画压在最上层）──
        var faceRect = new Rect(fx0, fy0, s, s);
        dc.DrawImage(head.Face, faceRect);
        if (head.HatFace is not null)
            dc.DrawImage(head.HatFace, faceRect);

        var outline = new Pen(new SolidColorBrush(Color.FromArgb(38, 0, 0, 0)), 1);
        outline.Freeze();
        dc.DrawRectangle(null, outline, faceRect);
    }

    /// <summary>
    /// 把一块 8×8 贴图按「四个角映射到哪儿」画成平行四边形。
    ///
    /// 仿射变换由三个点唯一确定，这里用 左上/右上/左下 三点反解矩阵，
    /// 于是任意斜切都能表达，不用手算斜切系数。
    /// </summary>
    private static void DrawMappedFace(
        DrawingContext dc, BitmapSource? tex,
        (double X, double Y) p00, (double X, double Y) p10,
        (double X, double Y) p01, (double X, double Y) p11)
    {
        if (tex is null) return;

        _ = p11;   // 平行四边形由三点确定，第四点自然推出

        // 贴图坐标 u∈[0,8], v∈[0,8] 映射到：
        //   P(u,v) = p00 + (u/8)*(p10-p00) + (v/8)*(p01-p00)
        //
        // 写成矩阵 x' = M11*u + M21*v + OffsetX
        //        y' = M12*u + M22*v + OffsetY
        var m11 = (p10.X - p00.X) / 8.0;
        var m12 = (p10.Y - p00.Y) / 8.0;
        var m21 = (p01.X - p00.X) / 8.0;
        var m22 = (p01.Y - p00.Y) / 8.0;

        var m = new Matrix(m11, m12, m21, m22, p00.X, p00.Y);

        dc.PushTransform(new MatrixTransform(m));
        dc.DrawImage(tex, new Rect(0, 0, 8, 8));
        dc.Pop();
    }
}