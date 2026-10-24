using PclLike.Core.Minecraft;
using PclLike.Core.Minecraft.Loaders;
using PclLike.Core.Minecraft.Launch;

Console.OutputEncoding = System.Text.Encoding.UTF8;

const string GameRoot = @"C:\Users\yuwei\AppData\Roaming\.pcllike";
const string JavaPath = @"C:\Users\yuwei\.pcllike-runtimes\jdk-17.0.20.1+1\bin\java.exe";
const string McVersion = "1.20.1";
const string LoaderVersion = "47.1.106";

Console.WriteLine("══════ NeoForge processor 链实测 ══════");

// ── 0. 先看缺失的三个产物在不在 ──
void ShowArtifacts(string tag)
{
    Console.WriteLine($"  ▸ 关键产物 [{tag}]");
    var paths = new[]
    {
        $"libraries/net/minecraft/client/{McVersion}-20230612.114412/client-{McVersion}-20230612.114412-srg.jar",
        $"libraries/net/minecraft/client/{McVersion}-20230612.114412/client-{McVersion}-20230612.114412-extra.jar",
        $"libraries/net/minecraft/client/{McVersion}-20230612.114412/client-{McVersion}-20230612.114412-slim.jar",
        $"libraries/net/neoforged/forge/{McVersion}-{LoaderVersion}/forge-{McVersion}-{LoaderVersion}-client.jar",
    };
    foreach (var rel in paths)
    {
        var full = Path.Combine(GameRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        var name = Path.GetFileName(full);
        if (File.Exists(full))
            Console.WriteLine($"      ✓ {name,-52} {new FileInfo(full).Length / 1024} KB");
        else
            Console.WriteLine($"      ✗ {name,-52} 缺失");
    }
}

ShowArtifacts("修复前");
Console.WriteLine();

// ── 1. 跑 processor 链 ──
Console.WriteLine("  ▸ 执行 processor 链");
try
{
    var provider = new NeoForgeProvider();
    var json = await provider.BuildVersionJsonAsync(
        McVersion, LoaderVersion, GameRoot, JavaPath,
        onLog: line => Console.WriteLine("    " + line),
        ct: default);

    Console.WriteLine();
    Console.WriteLine($"  ✓ 版本 JSON 生成成功，{json.Length} 字符");
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine($"  ✗ {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException is { } i) Console.WriteLine($"    inner: {i.Message}");
}

Console.WriteLine();
ShowArtifacts("修复后");
Console.WriteLine();

// ── 2. 校验 sha1 是否与 install_profile 声明一致 ──
Console.WriteLine("  ▸ 产物 sha1 校验（对照 install_profile 的 MC_SLIM_SHA / MC_EXTRA_SHA / PATCHED_SHA）");
{
    var expect = new Dictionary<string, string>
    {
        [$"client-{McVersion}-20230612.114412-slim.jar"] = "de86b035d2da0f78940796bb95c39a932ed84834",
        [$"client-{McVersion}-20230612.114412-extra.jar"] = "8c5a95cbce940cfdb304376ae9fea47968d02587",
        [$"forge-{McVersion}-{LoaderVersion}-client.jar"] = "b6153073abfb19dc7373f1e09f9b51b8ffc72b38",
    };
    foreach (var (name, want) in expect)
    {
        var full = Path.Combine(GameRoot, "libraries", "net",
            name.StartsWith("forge-") ? "neoforged/forge" : "minecraft/client",
            name.StartsWith("forge-") ? $"{McVersion}-{LoaderVersion}" : $"{McVersion}-20230612.114412",
            name);
        if (!File.Exists(full)) { Console.WriteLine($"      · {name} 不存在，跳过"); continue; }
        using var fs = File.OpenRead(full);
        var got = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(fs)).ToLowerInvariant();
        Console.WriteLine($"      {(got == want ? "✓" : "✗")} {name}");
        Console.WriteLine($"          期望 {want}");
        Console.WriteLine($"          实际 {got}");
    }
}

Console.WriteLine();
Console.WriteLine("══════ 结束 ══════");