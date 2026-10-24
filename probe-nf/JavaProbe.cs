using PclLike.Core.Minecraft.Launch;

Console.OutputEncoding = System.Text.Encoding.UTF8;

const string GameRoot = @"C:\Users\yuwei\AppData\Roaming\.pcllike";

Console.WriteLine("══════ Java 发现与选择 ══════");
Console.WriteLine();

// 1. 不传额外路径，看启动器自己能找到什么
Console.WriteLine("▸ Discover(gameRoot, null) —— 启动器默认搜索范围：");
var found = JavaLocator.Discover(GameRoot, null);
if (found.Count == 0)
{
    Console.WriteLine("    （一个都没找到）");
}
else
{
    foreach (var j in found)
    {
        Console.WriteLine($"    Java {j.MajorVersion,-3} 64bit={j.Is64Bit,-5} {j.Path}");
    }
}
Console.WriteLine($"    共 {found.Count} 个");
Console.WriteLine();

// 2. 显式传 Temurin 路径
const string Temurin = @"C:\Users\yuwei\.jdks\jdk-17.0.20.1+1\bin\java.exe";
Console.WriteLine($"▸ Discover(gameRoot, \"{Temurin}\")：");
var withHint = JavaLocator.Discover(GameRoot, Temurin);
foreach (var j in withHint)
{
    Console.WriteLine($"    Java {j.MajorVersion,-3} 64bit={j.Is64Bit,-5} {j.Path}");
}
Console.WriteLine($"    共 {withHint.Count} 个");
Console.WriteLine();

// 3. 选 Java 17 —— 这是 1.20.1 需要的
foreach (var req in new[] { 8, 17, 21 })
{
    var pick = JavaLocator.SelectFor(found, req);
    Console.WriteLine($"▸ 需要 Java {req,-3} → {(pick is null ? "无可用候选" : $"Java {pick.MajorVersion} @ {pick.Path}")}");
}
Console.WriteLine();

// 4. 直接探测 Temurin 文件本身能否被识别
Console.WriteLine("▸ 单文件探测 Temurin JDK 17：");
var probe = JavaLocator.Probe(Temurin);
Console.WriteLine(probe is null
    ? "    探测失败（返回 null）"
    : $"    Java {probe.MajorVersion} 64bit={probe.Is64Bit} {probe.Path}");
Console.WriteLine();

Console.WriteLine("══════ 结束 ══════");