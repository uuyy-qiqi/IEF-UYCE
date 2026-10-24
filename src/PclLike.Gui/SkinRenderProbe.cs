using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PclLike.Gui.Skins;

namespace PclLike.Gui;

/// <summary>
/// 把 SkinHeadView 的渲染结果落成 PNG，用来肉眼验收 3D 头部几何。
///
/// 为什么需要它：等距投影的斜边偏移量全靠手算，光看代码判断不了对不对，
/// 必须真的画出来看。这个探针在无窗口环境下用 RenderTargetBitmap 出图。
///
/// 跑法（GUI 项目里带上 --render-skin 参数）：
///     PclLike.exe --render-skin &lt;尺寸&gt; &lt;输出png&gt; [皮肤png]
/// </summary>
internal static class SkinRenderProbe
{
    public static int Run(string[] args)
    {
        // args[0] = --render-skin, [1] = size, [2] = out.png, [3] = 可选皮肤路径
        var size = args.Length > 1 && double.TryParse(args[1], out var s) ? s : 64.0;
        var outPath = args.Length > 2 ? args[2] : "skin_head.png";
        var skinPath = args.Length > 3 ? args[3] : null;

        // 默认用内置 Steve；给了路径就用那张
        byte[] png;
        if (skinPath is not null && File.Exists(skinPath))
        {
            png = File.ReadAllBytes(skinPath);
            Console.WriteLine($"皮肤来源: {skinPath}");
        }
        else
        {
            png = LoadEmbeddedDefault();
            Console.WriteLine("皮肤来源: 内置默认 Steve");
        }

        if (!SkinHead.TryParse(png, out var head))
        {
            Console.Error.WriteLine("皮肤解析失败");
            return 2;
        }

        Console.WriteLine($"解析成功: 帽檐层={head.HasHatLayer} 纤细={head.SlimModel}");

        // 画布按控件的度量尺寸开，留一点边
        var w = size * 2;
        var h = size * 1.5;

        var view = new SkinHeadView { Head = head, Size = size };
        view.Measure(new Size(w, h));
        view.Arrange(new Rect(0, 0, w, h));
        view.UpdateLayout();

        // 浅色背景，模拟启动页的实际底色
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(0xF2, 0xF6, 0xFC)), null,
                new Rect(0, 0, w, h));
        }

        var host = new VisualHost { Child = view };
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(w), (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);

        // 先铺背景，再叠控件
        rtb.Render(visual);
        rtb.Render(host);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using (var fs = File.Create(outPath))
            encoder.Save(fs);

        Console.WriteLine($"已写出 {outPath}  ({rtb.PixelWidth}x{rtb.PixelHeight})");
        return 0;
    }

    private static byte[] LoadEmbeddedDefault()
    {
        var uri = new Uri("pack://application:,,,/Assets/default_skin.png");
        var res = Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException("找不到内嵌的 default_skin.png");

        using var ms = new MemoryStream();
        res.Stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>把 FrameworkElement 塞进可视树，RenderTargetBitmap 才肯画它。</summary>
    private sealed class VisualHost : FrameworkElement
    {
        public Visual? Child { get; set; }

        protected override int VisualChildrenCount => Child is null ? 0 : 1;

        protected override Visual GetVisualChild(int index)
            => index == 0 && Child is not null
                ? Child
                : throw new ArgumentOutOfRangeException(nameof(index));
    }
}