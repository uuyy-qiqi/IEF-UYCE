using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PclLike.Gui.Skins;

/// <summary>
/// 把一张 64×64（或旧版 64×32）皮肤 PNG 切成等距 3D 头部需要的几块贴图。
///
/// Minecraft 皮肤布局（64×64），和头部有关的区域：
///   头部本体   头部正面 (8,8,8,8)   头部侧面 (0,8,8,8)  头部顶面 (8,0,8,8)
///   帽檐/第二层 正面 (40,8,8,8)      侧面 (32,8,8,8)     顶面 (40,0,8,8)
///
/// 渲染用「斜二测」投影：正面保持正方形，顶面往上斜、侧面往右斜，
/// 这样一块 8×8 的头部本体画出来就是 PCL2 那种 3D 方块头。
/// </summary>
public sealed class SkinHead
{
    /// <summary>皮肤可解析时才为 true；旧版 64×32 皮肤没有帽檐层。</summary>
    public bool HasHatLayer { get; private init; }

    /// <summary>纤细模型（Alex）：手臂宽 3 像素而不是 4。头部贴图本身一样。</summary>
    public bool SlimModel { get; private init; }

    /// <summary>正面贴图（8×8 → 放大后使用）。</summary>
    public BitmapSource Face { get; private init; } = null!;

    /// <summary>右侧面贴图。</summary>
    public BitmapSource Side { get; private init; } = null!;

    /// <summary>顶面贴图。</summary>
    public BitmapSource Top { get; private init; } = null!;

    /// <summary>帽檐正面（没帽檐时为 null）。</summary>
    public BitmapSource? HatFace { get; private init; }

    /// <summary>帽檐右侧面。</summary>
    public BitmapSource? HatSide { get; private init; }

    /// <summary>帽檐顶面。</summary>
    public BitmapSource? HatTop { get; private init; }

    // ────────────────────────────────────────────────────────────
    // 解析
    // ────────────────────────────────────────────────────────────

    /// <summary>解析一张皮肤 PNG。失败返回 false，不会抛。</summary>
    public static bool TryParse(byte[] png, out SkinHead head)
    {
        head = null!;
        if (png is null || png.Length < 64) return false;

        try
        {
            var bmp = Decode(png);
            if (bmp is null) return false;

            // 只接受标准尺寸。其他尺寸（比如某些高清皮肤）不做兼容 —— 宁可回退默认，
            // 也不要画出错位的贴图。
            const int S = 64;      // 标准皮肤边长
            const int CELL = 8;    // 每块贴图 8×8
            if (bmp.PixelWidth != S || bmp.PixelHeight != S) return false;

            var src = new WriteableBitmap(bmp);

            var face = Cut(src, 8, 8, CELL, CELL);    // 正面
            var side = Cut(src, 0, 8, CELL, CELL);    // 右侧面
            var top = Cut(src, 8, 0, CELL, CELL);    // 顶面

            var hatFace = Cut(src, 40, 8, CELL, CELL);
            var hatSide = Cut(src, 32, 8, CELL, CELL);
            var hatTop = Cut(src, 40, 0, CELL, CELL);

            // 帽檐如果整块都是全透明，就等于没有 —— 免得画出一层空气
            var hasHat = !IsFullyTransparent(hatFace);

            head = new SkinHead
            {
                Face = face,
                Side = side,
                Top = top,
                HatFace = hasHat ? hatFace : null,
                HatSide = hasHat ? hatSide : null,
                HatTop = hasHat ? hatTop : null,
                HasHatLayer = hasHat,
                SlimModel = false,
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static BitmapSource? Decode(byte[] png)
    {
        try
        {
            using var ms = new MemoryStream(png);
            var decoder = BitmapDecoder.Create(
                ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0) return null;
            return decoder.Frames[0];
        }
        catch { return null; }
    }

    /// <summary>从皮肤上裁一块，转成带 alpha 的位图。</summary>
    private static BitmapSource Cut(BitmapSource src, int x, int y, int w, int h)
    {
        var cropped = new CroppedBitmap(src, new Int32Rect(x, y, w, h));

        // 统一转成 Bgra32：渲染时要做 alpha 混合，源格式五花八门
        var converted = new FormatConvertedBitmap(cropped, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>整块贴图是不是全透明（用来判断帽檐存在与否）。</summary>
    private static bool IsFullyTransparent(BitmapSource bmp)
    {
        const int w = 8, h = 8;
        var stride = w * 4;                      // Bgra32
        var pixels = new byte[stride * h];
        bmp.CopyPixels(pixels, stride, 0);

        for (int i = 3; i < pixels.Length; i += 4)   // 只看 alpha
            if (pixels[i] != 0) return false;

        return true;
    }
}