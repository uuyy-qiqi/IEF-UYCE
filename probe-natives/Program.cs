// 诊查原生库（natives）计划：把某个版本解析出来的库逐个过 Plan，
// 报告哪些进了 classpath、哪些带 natives、natives jar 在不在盘上。
//
// 存在的理由：启动器启动时的日志在探针路径下可能拿不到（onLog 为 null），
// 而「LWJGL3 的 natives jar 没下下来」这种问题只能从计划表看出来。
using PclLike.Core.Minecraft;
using PclLike.Core.Minecraft.Install;

var root = Environment.ExpandEnvironmentVariables(args.Length > 1 ? args[1] : @"%APPDATA%\.pcllike");
var versionId = args.Length > 2 ? args[2] : "1.12.2-cleanroom-0.7.12-alpha";

Console.WriteLine($"根目录 : {root}");
Console.WriteLine($"版本   : {versionId}");

var installer = new InstanceInstaller(root);
var resolver = new VersionResolver((id, ct) =>
{
    var p = Path.Combine(root, "versions", id, id + ".json");
    return File.Exists(p)
        ? File.ReadAllTextAsync(p, ct)
        : throw new FileNotFoundException(p);
});

VersionJson json;
try
{
    json = await resolver.ResolveAsync(versionId);
}
catch (Exception ex)
{
    Console.WriteLine($"解析失败：{ex.Message}");
    return 1;
}

var all = json.Libraries ?? new List<Library>();
var plans = ClasspathBuilder.PlanAll(all);

Console.WriteLine($"库条目 : {all.Count} 个 → 成计划 {plans.Count} 个");
Console.WriteLine($"带主 jar : {plans.Count(p => p.Jar is not null)}");
Console.WriteLine($"带 natives : {plans.Count(p => p.NativeJar is not null)}");

Console.WriteLine();
Console.WriteLine("── 带 natives 的计划（前 16）──");
foreach (var p in plans.Where(p => p.NativeJar is not null).Take(16))
{
    var a = p.NativeJar!;
    var rel = (a.Path ?? p.Library.ToMavenPath()).Replace('/', Path.DirectorySeparatorChar);
    var jar = Path.Combine(installer.LibrariesDir, rel);
    Console.WriteLine($"  {p.Library.Name,-38} {(File.Exists(jar) ? "在盘上" : "缺 jar ")}  {a.Url ?? "(无 url)"}");
}

Console.WriteLine();
Console.WriteLine("── /lwjgl 相关的计划（含主 jar）──");
foreach (var p in plans.Where(p => (p.Library.Name ?? "").Contains(":lwjgl")).Take(20))
{
    Console.WriteLine($"  {p.Library.Name,-38} jar={(p.Jar is not null ? "有" : "无")} natives={(p.NativeJar is not null ? "有" : "无")}");
}

Console.WriteLine();
Console.WriteLine("── org.lwjgl:lwjgl:3.4.3 的每一条（原始 + Plan 结果）──");
var idx = 0;
foreach (var l in all.Where(l => l.Name == "org.lwjgl:lwjgl:3.4.3"))
{
    idx++;
    var nat = l.Natives is { Count: > 0 }
        ? string.Join(",", l.Natives.Select(kv => $"{kv.Key}={kv.Value}"))
        : "-";
    var cls = l.Downloads?.Classifiers is { } d ? string.Join(",", d.Keys) : "-";
    var plan = ClasspathBuilder.Plan(l);
    var verdict = plan is null ? "Plan=null" : $"jar={(plan.Jar is not null ? "有" : "无")} natives={(plan.NativeJar is not null ? "有" : "无")}";
    Console.WriteLine($"  [{idx,2}] natives={nat,-28} classifiers={cls,-52} rules={l.Rules?.Count ?? 0} → {verdict}");

    if (l.Rules is { Count: > 0 })
    {
        foreach (var r in l.Rules)
        {
            var feat = r.Features is null ? "-" : string.Join(",", r.Features.Select(kv => $"{kv.Key}={kv.Value}"));
            Console.WriteLine($"        rule action={r.Action} os.name={r.Os?.Name ?? "-"} os.arch={r.Os?.Arch ?? "-"} os.version={r.Os?.Version ?? "-"} features={feat}");
        }
        Console.WriteLine($"        IsAllowed={RuleEvaluator.IsAllowed(l.Rules)}  当前 os={RuleEvaluator.CurrentOsName}/{RuleEvaluator.CurrentOsArch}");
    }
}

Console.WriteLine();
Console.WriteLine("── 有 natives 声明但没成计划（前 12）──");
var dropped = all.Where(l => l.Natives is { Count: > 0 } && l.Name is not null
                             && plans.All(p => p.Library.Name != l.Name)).ToList();
Console.WriteLine($"  共 {dropped.Count} 条");
foreach (var l in dropped.Take(12))
{
    var cls = RuleEvaluator.SelectNativeClassifier(l.Natives!);
    var has = l.Downloads?.Classifiers is { } d && d.ContainsKey(cls ?? "");
    Console.WriteLine($"  {l.Name,-38} 分类器={cls ?? "(null)"} classifiers里有={has} rules={l.Rules?.Count ?? 0}");
}

Console.WriteLine();
Console.WriteLine("── 实际解压 ──");
var nativesDir = Path.Combine(root, "versions", versionId, "natives");
var n = await installer.EnsureNativesAsync(versionId, nativesDir, Console.WriteLine);
Console.WriteLine($"EnsureNativesAsync 返回 {n}；natives 目录现有 {Directory.GetFiles(nativesDir).Length} 个文件");
return 0;