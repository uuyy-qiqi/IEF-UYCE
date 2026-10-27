using PclLike.Core.Content;

// 拖入文件分类探针：把路径逐个丢给 DroppedContentClassifier，打印它认成了什么。
// 用法: probe-classify <文件路径> [更多路径...]
Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0)
{
    Console.WriteLine("用法: probe-classify <文件路径> [...]");
    return 1;
}

var bad = 0;

foreach (var p in args)
{
    Console.WriteLine($"── {Path.GetFileName(p)}");

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var r = DroppedContentClassifier.Classify(p);
    sw.Stop();

    Console.WriteLine($"   类型: {r.Kind}");
    Console.WriteLine($"   说明: {r.Describe}");
    Console.WriteLine($"   耗时: {sw.ElapsedMilliseconds} ms");
    Console.WriteLine($"   落地: {r.Path}");

    if (r.Kind == DroppedKind.Unknown)
    {
        Console.WriteLine($"   ❌ 没认出来");
        bad++;
    }
}

Console.WriteLine();
Console.WriteLine(bad == 0 ? "全部认出来了" : $"{bad} 个没认出来");

return bad == 0 ? 0 : 2;