using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PclLike.Core.Content;
using PclLike.Core.Storage;

// probe-content：Content 能力的端到端探针。
// 全部在临时目录里跑真文件，不看门面（不信"编译过就行"）。

Console.OutputEncoding = Encoding.UTF8;

var fail = 0;

void Case(string name, bool ok, string detail = "")
{
    if (!ok) fail++;
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
}

void Info(string text) => Console.WriteLine("       " + text);

static void Write(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content, Encoding.UTF8);
}

static string P(params string[] parts) => Path.Combine(parts);

// 按 `/` 分隔的相对路径建 zip（模拟真实压缩包里的路径写法）
static void AddZip(string zipPath, params (string Entry, string Content)[] entries)
{
    using var fs = File.Create(zipPath);
    using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
    foreach (var (entryName, content) in entries)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }
}

static bool IsJsonArray(string text)
{
    try
    {
        using var d = JsonDocument.Parse(text);
        return d.RootElement.ValueKind == JsonValueKind.Array;
    }
    catch { return false; }
}

var temp = P(Path.GetTempPath(), "pcllike-probe-" + Guid.NewGuid().ToString("N"));
Console.WriteLine($"临时目录：{temp}");
Console.WriteLine();

try
{
    // ---------------------------------------------------------------- 1
    Console.WriteLine("=== 1. ContentPaths.TargetDir 六种类型 ===");

    var instance = new GameInstance
    {
        Id = "probe",
        Name = "探针实例",
        VersionId = "1.20.1",
        Isolated = true,
        Directory = temp,
        LauncherRoot = temp,
    };

    var gameDir = instance.GameDir;
    var saved = P(temp, ".minecraft");

    Case("GameDir = <temp>\\.minecraft", gameDir == saved, gameDir);

    var dirMod = ContentPaths.TargetDir(instance, ContentKind.Mod);
    var dirPack = ContentPaths.TargetDir(instance, ContentKind.ModPack);
    var dirData = ContentPaths.TargetDir(instance, ContentKind.Datapack);
    var dirRes = ContentPaths.TargetDir(instance, ContentKind.ResourcePack);
    var dirShader = ContentPaths.TargetDir(instance, ContentKind.Shader);
    var dirWorld = ContentPaths.TargetDir(instance, ContentKind.World);
    var dirDataWorld = ContentPaths.TargetDir(instance, ContentKind.Datapack, "MyWorld");

    Info($"Mod          -> {dirMod}");
    Info($"ModPack      -> {dirPack}");
    Info($"Datapack     -> {dirData}");
    Info($"Datapack(W)  -> {dirDataWorld}");
    Info($"ResourcePack -> {dirRes}");
    Info($"Shader       -> {dirShader}");
    Info($"World        -> {dirWorld}");

    Case("Mod -> <GameDir>\\mods", dirMod == P(gameDir, "mods"), dirMod);
    Case("ModPack -> <GameDir> 本身", dirPack == gameDir, dirPack);
    Case("Datapack(无世界名) -> <GameDir>\\datapacks", dirData == P(gameDir, "datapacks"), dirData);
    Case("Datapack(MyWorld) -> <GameDir>\\saves\\MyWorld\\datapacks", dirDataWorld == P(gameDir, "saves", "MyWorld", "datapacks"), dirDataWorld);
    Case("ResourcePack -> <GameDir>\\resourcepacks", dirRes == P(gameDir, "resourcepacks"), dirRes);
    Case("Shader -> <GameDir>\\shaderpacks", dirShader == P(gameDir, "shaderpacks"), dirShader);
    Case("World -> <GameDir>\\saves", dirWorld == P(gameDir, "saves"), dirWorld);
    Case("上述目录均已创建", Directory.Exists(dirMod) && Directory.Exists(dirData) && Directory.Exists(dirRes) && Directory.Exists(dirShader) && Directory.Exists(dirWorld));
    Case("Datapack 无世界名时不落进某个世界的 saves 下",
        !dirData.StartsWith(P(gameDir, "saves"), StringComparison.OrdinalIgnoreCase), dirData);

    Info($"DisplayName：{string.Join(" / ", ContentPaths.All.Select(ContentPaths.DisplayName))}");
    Case("DisplayName 全部非空且互不相同",
        ContentPaths.All.All(k => ContentPaths.DisplayName(k).Length > 0) &&
        ContentPaths.All.Select(ContentPaths.DisplayName).Distinct().Count() == 6);

    Info($"ModrinthProjectType：{string.Join(" / ", ContentPaths.All.Select(k => $"{k}={ContentPaths.ModrinthProjectType(k) ?? "null"}"))}");
    Case("World 的 ModrinthProjectType 为 null", ContentPaths.ModrinthProjectType(ContentKind.World) is null);
    Case("Mod 的 ModrinthProjectType 为 mod", ContentPaths.ModrinthProjectType(ContentKind.Mod) == "mod");
    Case("ModPack 的 ModrinthProjectType 为 modpack", ContentPaths.ModrinthProjectType(ContentKind.ModPack) == "modpack");
    Console.WriteLine();

    // ---------------------------------------------------------------- 2
    Console.WriteLine("=== 2. ContentInstaller.InstallFileAsync ===");

    var srcJar = P(temp, "src", "sodium-fabric-0.5.13.jar");
    var srcOther = P(temp, "src", "other.jar");
    var srcHot = P(temp, "src", "faithful-32x.zip");
    var srcShader = P(temp, "src", "complementary-reimagined.zip");
    var srcData = P(temp, "src", "terralith-datapack.zip");

    Write(srcJar, "FAKE-JAR-CONTENT");
    Write(srcOther, "FAKE-JAR-CONTENT-V2");
    Write(srcHot, "FAKE-RESOURCEPACK");
    Write(srcShader, "FAKE-SHADERPACK");
    Write(srcData, "FAKE-DATAPACK");

    var rMod = await ContentInstaller.InstallFileAsync(instance, ContentKind.Mod, srcJar);
    Case("装 Mod 返回 Ok", rMod.Ok, rMod.Message);
    Case("Mod 落到 <GameDir>\\mods\\sodium-fabric-0.5.13.jar",
        File.Exists(P(gameDir, "mods", "sodium-fabric-0.5.13.jar")), rMod.InstalledPath ?? "null");
    Info($"RequiredLoader = {rMod.RequiredLoader ?? "null"}");

    var rOver = await ContentInstaller.InstallFileAsync(
        instance, ContentKind.Mod, srcOther, "sodium-fabric-0.5.13.jar");
    Case("Mod 重名覆盖（同一 Mod 换版本）成功", rOver.Ok, rOver.Message);
    Case("覆盖后内容来自新文件",
        File.ReadAllText(P(gameDir, "mods", "sodium-fabric-0.5.13.jar")) == "FAKE-JAR-CONTENT-V2");

    var rTraversal = await ContentInstaller.InstallFileAsync(
        instance, ContentKind.Mod, srcOther, @"..\..\evil-mod.jar");
    Info($"preferredFileName=\"..\\..\\evil-mod.jar\" -> {rTraversal.InstalledPath ?? "null"}");
    Case("preferredFileName 里的 ..\\ 被剥掉，仍落在 mods 内",
        rTraversal.Ok && Path.GetDirectoryName(rTraversal.InstalledPath!) == P(gameDir, "mods"),
        rTraversal.Message);
    Case("剥落后文件名 = evil-mod.jar", Path.GetFileName(rTraversal.InstalledPath ?? "") == "evil-mod.jar");
    Case("<temp>\\evil-mod.jar 未被创建", !File.Exists(P(temp, "evil-mod.jar")));

    var rRes = await ContentInstaller.InstallFileAsync(instance, ContentKind.ResourcePack, srcHot);
    Case("资源包落到 <GameDir>\\resourcepacks", rRes.Ok && File.Exists(P(gameDir, "resourcepacks", "faithful-32x.zip")), rRes.Message);
    Case("资源包 RequiredLoader 为 null", rRes.RequiredLoader is null);

    var rShader = await ContentInstaller.InstallFileAsync(instance, ContentKind.Shader, srcShader);
    Case("光影包落到 <GameDir>\\shaderpacks", rShader.Ok && File.Exists(P(gameDir, "shaderpacks", "complementary-reimagined.zip")), rShader.Message);
    Case("光影包 RequiredLoader 非 null", rShader.RequiredLoader is not null);

    var rDataNoWorld = await ContentInstaller.InstallFileAsync(
        instance, ContentKind.Datapack, srcData, null, "NoSuchWorld");
    Case("数据包给不存在的世界 -> Ok=false", !rDataNoWorld.Ok, rDataNoWorld.Message);
    Case("失败 Message 说明了找不到世界", rDataNoWorld.Message.Contains("找不到世界"));
    Case("不存在的世界未被凭空创建", !Directory.Exists(P(gameDir, "saves", "NoSuchWorld")));

    // 这一节只用 DataPackWorld，把 MyWorld 留给第 3 节由 WorldStore 真正创建，
    // 否则第 3 节的“导入世界 zip”会因为世界已存在而永远失败（这不是被测代码的问题）。
    var dataWorld = "DataPackWorld";
    Directory.CreateDirectory(P(gameDir, "saves", dataWorld));
    Write(P(gameDir, "saves", dataWorld, "level.dat"), "LEVEL");

    var rData = await ContentInstaller.InstallFileAsync(
        instance, ContentKind.Datapack, srcData, null, dataWorld);
    Case("数据包装进已有世界 -> Ok", rData.Ok, rData.Message);
    Case("数据包落到 <GameDir>\\saves\\DataPackWorld\\datapacks",
        File.Exists(P(gameDir, "saves", dataWorld, "datapacks", "terralith-datapack.zip")));
    Case("数据包 Message 里带世界名", rData.Message.Contains(dataWorld), rData.Message);

    var rPack = await ContentInstaller.InstallFileAsync(instance, ContentKind.ModPack, srcJar);
    Case("ModPack 走 InstallFileAsync -> Ok=false 且提示走 InstallModPack",
        !rPack.Ok && rPack.Message.Contains("整合包请走 InstallModPack"), rPack.Message);

    var rMissing = await ContentInstaller.InstallFileAsync(instance, ContentKind.Mod, P(temp, "nope.jar"));
    Case("文件不存在 -> Ok=false", !rMissing.Ok, rMissing.Message);
    Console.WriteLine();

    // ---------------------------------------------------------------- 3
    Console.WriteLine("=== 3. WorldStore：导入 / 列表 / 导出 / 删除 / 防穿越 ===");

    var zipWorld = P(temp, "MyWorld.zip");
    // 容器级调用 ContentPaths.TargetDir(..., Datapack, "MyWorld") 会顺手把 saves\MyWorld\datapacks 建出来，
    // 那是"数据包目录"的正常副作用，但会让"导入同名世界"判定为重名。这里显式清干净再测导入。
    if (Directory.Exists(P(gameDir, "saves", "MyWorld")))
        Directory.Delete(P(gameDir, "saves", "MyWorld"), true);
    using (var fs = File.Create(zipWorld))
    using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
    {
        // 故意混入 mac 垃圾条目，验证会被跳过
        foreach (var (entryName, content) in new[]
        {
            ("MyWorld/level.dat", "LEVEL-DATA"),
            ("MyWorld/region/r.0.0.mca", new string('R', 4096)),
            ("__MACOSX/MyWorld/._level.dat", "JUNK"),
            ("MyWorld/.DS_Store", "JUNK"),
        })
        {
            var e = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var s = e.Open();
            var b = Encoding.UTF8.GetBytes(content);
            s.Write(b, 0, b.Length);
        }
    }

    var rImp = await WorldStore.ImportZipAsync(instance, zipWorld);
    Case("导入世界 zip -> Ok", rImp.Ok, rImp.Message);
    Case("<GameDir>\\saves\\MyWorld\\level.dat 存在", File.Exists(P(gameDir, "saves", "MyWorld", "level.dat")));
    Case("region 文件也在", File.Exists(P(gameDir, "saves", "MyWorld", "region", "r.0.0.mca")));
    Case("__MACOSX/ 被跳过", !Directory.Exists(P(gameDir, "saves", "MyWorld", "__MACOSX")));
    Case(".DS_Store 被跳过", !File.Exists(P(gameDir, "saves", "MyWorld", ".DS_Store")));

    var list = WorldStore.List(instance);
    Info($"List 到 {list.Count} 个世界：{string.Join("、", list.Select(w => $"{w.Name}[{w.SizeText}|{w.LastWriteText}]"))}");
    var mine = list.FirstOrDefault(w => w.Name == "MyWorld");
    Case("List 能列出 MyWorld", mine is not null);
    Case("MyWorld 的 SizeText 非空", mine is not null && mine.SizeText.Length > 0, mine?.SizeText ?? "");
    Case("MyWorld 的 LastWriteText 非空", mine is not null && mine.LastWriteText.Length > 0, mine?.LastWriteText ?? "");
    Case("SizeBytes > 0", mine is not null && mine.SizeBytes > 0, (mine?.SizeBytes ?? 0).ToString());

    // 顺便验一下空世界的 LastWriteText 显示 "—"
    var emptyWorld = new WorldInfo("x", "x", 0, null);
    Case("没有时间戳时 LastWriteText 显示 —", emptyWorld.LastWriteText == "—", emptyWorld.LastWriteText);
    Case("末位大小显示带单位", emptyWorld.SizeText == "0 B", emptyWorld.SizeText);

    // 往刚导入的 MyWorld 里再装一个数据包，验证导出会带上嵌套子目录
    var rDataMy = await ContentInstaller.InstallFileAsync(
        instance, ContentKind.Datapack, srcData, null, "MyWorld");
    Case("往导入的世界里装数据包 -> Ok", rDataMy.Ok, rDataMy.Message);

    var zipExport = P(temp, "export.zip");
    var rExp = await WorldStore.ExportZipAsync(instance, "MyWorld", zipExport);
    Case("导出世界 -> Ok", rExp.Ok, rExp.Message);
    Case("导出的 zip 存在", File.Exists(zipExport));

    using (var zip = ZipFile.OpenRead(zipExport))
    {
        var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
        Info($"导出 zip 条目：{string.Join("、", names)}");
        Case("导出条目以 MyWorld/ 为顶层", names.Contains("MyWorld/level.dat"));
        Case("导出条目用 / 分隔", names.All(n => !n.Contains('\\')));
        Case("导出包含 region 文件", names.Contains("MyWorld/region/r.0.0.mca"));
        Case("导出也带上了嵌套的 datapacks 子目录（递归导出）",
            names.Contains("MyWorld/datapacks/terralith-datapack.zip"));
    }

    // 重新解压验证内容确实还原
    var recheckDir = P(temp, "recheck");
    ZipFile.ExtractToDirectory(zipExport, recheckDir);
    Case("重新解压后 level.dat 内容一致",
        File.ReadAllText(P(recheckDir, "MyWorld", "level.dat")) == "LEVEL-DATA");

    Case("重复导入同名世界 -> 明确失败", !(await WorldStore.ImportZipAsync(instance, zipWorld)).Ok);
    Case("Delete 非法名（含 ..）返回 false", !WorldStore.Delete(instance, "..\\..\\evil"));
    Case("Delete 非法名（含分隔符）返回 false", !WorldStore.Delete(instance, "My/World"));
    Case("Delete 存在的世界 -> true", WorldStore.Delete(instance, "MyWorld"));
    Case("删除后目录消失", !Directory.Exists(P(gameDir, "saves", "MyWorld")));
    Case("再删同一个 -> false", !WorldStore.Delete(instance, "MyWorld"));

    // 防穿越 1：zip 里全是越界条目（无顶层世界目录）
    var zipEvil = P(temp, "evil.zip");
    AddZip(zipEvil, ("../../evil.txt", "PWNED"));
    // 防穿越 2：一个"看起来正常"的世界里夹带越界条目 —— 更真实的攻击形态
    var zipEvil2 = P(temp, "evil2.zip");
    AddZip(zipEvil2, ("../../evil.txt", "PWNED"), ("EvilWorld/level.dat", "LEVEL-EVIL"));
    // 防穿越 3：绝对路径条目
    var zipEvil3 = P(temp, "evil3.zip");
    AddZip(zipEvil3, ("EvilAbs/level.dat", "LEVEL-ABS"), ("C:/pwn-abs.txt", "PWNED"));

    var outside = Path.GetFullPath(P(temp, "..", "evil.txt"));
    InstallResult rEvilAll = new(false, "(未执行)");
    var outside2 = Path.GetFullPath(P(temp, "..", "..", "evil.txt"));
    var absPwn = "C:\\pwn-abs.txt";
    foreach (var f in new[] { outside, outside2, absPwn })
        if (File.Exists(f)) File.Delete(f);

    foreach (var (label, evilZip) in new[] { ("全部越界", zipEvil), ("正常世界夹带越界", zipEvil2), ("绝对路径", zipEvil3) })
    {
        var rEvil = await WorldStore.ImportZipAsync(instance, evilZip);
        if (label == "全部越界") rEvilAll = rEvil;
        Info($"恶意 zip（{label}）导入结果：Ok={rEvil.Ok} / {rEvil.Message}");
        Case($"恶意 zip（{label}）越界条目未写出 <temp> 之外（..\\evil.txt）", !File.Exists(outside), outside);
        Case($"恶意 zip（{label}）越界条目未写出 <temp>\\..\\..（..\\..\\evil.txt）", !File.Exists(outside2), outside2);
        Case($"恶意 zip（{label}）绝对路径条目未写出 {absPwn}", !File.Exists(absPwn));
        Case($"恶意 zip（{label}）<temp> 内的 evil.txt 也不存在（不是简单改名）", !File.Exists(P(temp, "evil.txt")));
        Case($"恶意 zip（{label}）实例目录未被写出 evil.txt", !File.Exists(P(gameDir, "evil.txt")));
    }
    Case("夹带越界的 zip 只落下了合法世界目录", Directory.Exists(P(gameDir, "saves", "EvilWorld")));
    Case("夹带越界的 zip 里合法文件内容正确",
        File.ReadAllText(P(gameDir, "saves", "EvilWorld", "level.dat")) == "LEVEL-EVIL");
    Case("合法世界目录里没有 evil.txt 残留（越界条目被跳过而非改名）",
        !File.Exists(P(gameDir, "saves", "EvilWorld", "evil.txt")));
    Case("越界条目被丢弃后，合法的 level.dat 照常落地",
        File.ReadAllText(P(gameDir, "saves", "EvilWorld", "level.dat")) == "LEVEL-EVIL");
    Case("全部条目越界的 zip 整体被拒（不会留下空世界）", !rEvilAll.Ok, rEvilAll.Message);
    foreach (var f in new[] { outside, outside2, absPwn })
        if (File.Exists(f)) File.Delete(f);
    Console.WriteLine();

    // ---------------------------------------------------------------- 4
    Console.WriteLine("=== 4. ModPackInstaller：ReadPlan + InstallAsync ===");

    var mrpack = P(temp, "test-pack.mrpack");
    var indexJson = """
    {
      "formatVersion": 1,
      "game": "minecraft",
      "versionId": "1.3.0",
      "name": "探针整合包",
      "files": [
        { "path": "mods/aaa.jar", "hashes": { "sha1": "aa11" }, "fileSize": 123,
          "downloads": ["https://cdn.modrinth.com/aaa.jar"],
          "env": { "client": "required", "server": "optional" } },
        { "path": "mods/bbb.jar", "hashes": { "sha1": "bb22" }, "fileSize": 456,
          "downloads": ["https://cdn.modrinth.com/bbb.jar"],
          "env": { "client": "required", "server": "required" } },
        { "path": "mods/serveronly.jar", "hashes": { "sha1": "cc33" }, "fileSize": 789,
          "downloads": ["https://cdn.modrinth.com/serveronly.jar"],
          "env": { "client": "unsupported", "server": "required" } }
      ],
      "dependencies": { "minecraft": "1.20.1", "fabric-loader": "0.15.0" }
    }
    """;

    using (var fs = File.Create(mrpack))
    using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
    {
        void Put(string entryName, string content)
        {
            var e = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var s = e.Open();
            var b = Encoding.UTF8.GetBytes(content);
            s.Write(b, 0, b.Length);
        }

        Put("modrinth.index.json", indexJson);
        Put("overrides/config/test.txt", "OVERRIDE");
        Put("client-overrides/config/client.txt", "CLIENT-OVERRIDE");
    }

    var plan = ModPackInstaller.ReadPlan(mrpack);
    Case("ReadPlan 返回非 null", plan is not null);

    if (plan is not null)
    {
        Info($"Name={plan.Name}");
        Info($"McVersion={plan.McVersion}");
        Info($"LoaderName={plan.LoaderName ?? "null"} / LoaderVersion={plan.LoaderVersion ?? "null"}");
        Info($"Files.Count={plan.Files.Count}（unsupported 已被剔除）");
        Info($"HasOverrides={plan.HasOverrides}");
        foreach (var f in plan.Files)
            Info($"  - {f.Path} | {f.FileName} | {f.Url} | sha1={f.Sha1 ?? "null"} | {f.SizeBytes}B | ClientRequired={f.ClientRequired}");

        Case("Name 解析正确", plan.Name == "探针整合包");
        Case("McVersion = 1.20.1", plan.McVersion == "1.20.1");
        Case("fabric-loader 映射成 fabric", plan.LoaderName == "fabric", plan.LoaderName ?? "null");
        Case("LoaderVersion = 0.15.0", plan.LoaderVersion == "0.15.0");
        Case("unsupported 文件被跳过（只剩 2 个）", plan.Files.Count == 2, plan.Files.Count.ToString());
        Case("不含 serveronly.jar", plan.Files.All(f => f.FileName != "serveronly.jar"));
        Case("HasOverrides = true", plan.HasOverrides);
        Case("ClientRequired 正确（两个都是 required）", plan.Files.All(f => f.ClientRequired));
        Case("SizeBytes / Sha1 / Url 解析正确",
            plan.Files.First(f => f.FileName == "aaa.jar") is { SizeBytes: 123, Sha1: "aa11", Url: "https://cdn.modrinth.com/aaa.jar" });
    }

    // 坏文件 -> null
    var badPack = P(temp, "bad.mrpack");
    AddZip(badPack, ("readme.txt", "not a modpack"));
    Case("ReadPlan(非整合包 zip) -> null", ModPackInstaller.ReadPlan(badPack) is null);
    Case("ReadPlan(不存在的路径) -> null", ModPackInstaller.ReadPlan(P(temp, "nope.mrpack")) is null);

    var mpInstance = new GameInstance
    {
        Id = "mp", Name = "整合包目标", VersionId = "1.20.1", Isolated = true,
        Directory = P(temp, "mp"), LauncherRoot = P(temp, "mp"),
    };

    var fetched = new List<string>();
    var mpLog = new List<string>();
    var progress = new Progress<string>(s => { mpLog.Add(s); Console.WriteLine("       [log] " + s); });

    ModPackFileFetcher fetcher = (file, destPath, ct) =>
    {
        fetched.Add(file.FileName);

        // 让 bbb.jar 失败，验证"单个失败不中断"
        if (file.FileName == "bbb.jar") return Task.FromResult(false);

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        File.WriteAllText(destPath, "DOWNLOADED:" + file.FileName, Encoding.UTF8);
        return Task.FromResult(true);
    };

    var mpResult = await ModPackInstaller.InstallAsync(mrpack, mpInstance, fetcher, progress);

    Console.WriteLine();
    Info($"InstallAsync Message 原文：{mpResult.Message}");
    Info($"fetcher 被调用顺序：{string.Join("、", fetched)}");
    Console.WriteLine();

    Case("mods/aaa.jar 落地", File.Exists(P(mpInstance.GameDir, "mods", "aaa.jar")));
    Case("落地内容来自 fetcher", File.ReadAllText(P(mpInstance.GameDir, "mods", "aaa.jar")) == "DOWNLOADED:aaa.jar");
    Case("unsupported 的 serveronly.jar 没落地", !File.Exists(P(mpInstance.GameDir, "mods", "serveronly.jar")));
    Case("overrides/config/test.txt 覆盖到位",
        File.Exists(P(mpInstance.GameDir, "config", "test.txt")) &&
        File.ReadAllText(P(mpInstance.GameDir, "config", "test.txt")) == "OVERRIDE");
    Case("client-overrides 也覆盖到位",
        File.Exists(P(mpInstance.GameDir, "config", "client.txt")) &&
        File.ReadAllText(P(mpInstance.GameDir, "config", "client.txt")) == "CLIENT-OVERRIDE");
    Case("两个文件都尝试下载（单个失败不中断）", fetched.Count == 2, string.Join("/", fetched));
    Case("整体 Ok=false（有失败）", !mpResult.Ok);
    Case("Message 含 \"成功 1 / 失败 1\"", mpResult.Message.Contains("成功 1 / 失败 1"), mpResult.Message);
    Case("Message 点名失败文件 bbb.jar", mpResult.Message.Contains("bbb.jar"), mpResult.Message);
    Case("RequiredLoader 从 dependencies 带出 fabric", mpResult.RequiredLoader == "fabric", mpResult.RequiredLoader ?? "null");
    Console.WriteLine();

    // ---------------------------------------------------------------- 5
    Console.WriteLine("=== 5. FavoritesStore ===");

    var favRoot = P(temp, "favorites-root");
    var favs = new FavoritesStore(favRoot);
    Info($"FilePath = {favs.FilePath}");

    Case("初始 Load 为空", favs.Load().Count == 0);

    var added1 = favs.Toggle(new FavoriteItem
    {
        ProjectId = "AANobbMI", Slug = "sodium", Name = "钠优化", ProjectType = "mod",
        Summary = "现代渲染优化", IconUrl = "https://cdn.modrinth.com/icon.png", Kind = "Mod",
    });
    var added2 = favs.Toggle(new FavoriteItem
    {
        ProjectId = "1KVo5zza", Slug = "fabulously-optimized", Name = "极致优化整合包",
        ProjectType = "modpack", Summary = "开箱即用", Kind = "ModPack",
    });

    Case("Toggle 新条目返回 true（在收藏里）", added1 && added2);
    Case("Load 拿到 2 条", favs.Load().Count == 2, favs.Load().Count.ToString());
    Case("Has 认识的 id -> true", favs.Has("AANobbMI"));
    Case("Has 不认识的 id -> false", !favs.Has("NOPE"));

    var toggledOff = favs.Toggle(new FavoriteItem { ProjectId = "AANobbMI", Name = "钠优化", Kind = "Mod" });
    Case("Toggle 同一条变 false（取消收藏）", !toggledOff);
    Case("取消后少一条", favs.Load().Count == 1);

    Case("Remove 存在的 id -> true", favs.Remove("1KVo5zza"));
    Case("Remove 不存在的 id -> false", !favs.Remove("1KVo5zza"));
    Case("Load 剩 0 条", favs.Load().Count == 0);

    // 再存一条，用来检查 JSON 文件内容
    favs.Toggle(new FavoriteItem
    {
        ProjectId = "AANobbMI", Slug = "sodium", Name = "钠优化", Kind = "Mod", Summary = "现代渲染优化",
    });
    favs.Toggle(new FavoriteItem { ProjectId = "P7dR8mSH", Slug = "fabric-api", Name = "Fabric API", Kind = "Mod" });
    Case("最终又剩 2 条", favs.Load().Count == 2);

    var favJson = File.ReadAllText(favs.FilePath);
    Console.WriteLine("       --- favorites.json 前几行 ---");
    foreach (var line in favJson.Split('\n').Take(8)) Console.WriteLine("       " + line.TrimEnd('\r'));
    Console.WriteLine("       ---");

    Case("文件内容是合法 JSON", IsJsonArray(favJson));
    Case("中文未被转义", favJson.Contains("钠优化") && !favJson.Contains("\\u94"));
    Case("JSON 是缩进的（WriteIndented）", favJson.Contains("\n  {"));

    // 损坏文件 -> 空列表不抛
    File.WriteAllText(favs.FilePath, "{ this is not json ][", Encoding.UTF8);
    Case("文件损坏 -> Load 返回空列表且不抛", favs.Load().Count == 0);
    Case("文件损坏 -> Has 返回 false", !favs.Has("AANobbMI"));

    File.Delete(favs.FilePath);
    Case("文件不存在 -> Load 返回空列表", favs.Load().Count == 0);
    Console.WriteLine();
}
catch (Exception ex)
{
    fail++;
    Console.WriteLine($"[FATAL] {ex}");
}
finally
{
    // 清掉越界测试可能留下的文件，再删临时目录
    foreach (var stray in new[]
    {
        Path.GetFullPath(P(temp, "..", "evil.txt")),
        Path.GetFullPath(P(temp, "..", "..", "evil.txt")),
    })
    {
        try { if (File.Exists(stray)) { File.Delete(stray); Console.WriteLine($"清理越界残留：{stray}"); } }
        catch { }
    }

    try
    {
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        Console.WriteLine($"临时目录已删除：{temp}（还存在={Directory.Exists(temp)}）");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"临时目录删除失败：{ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine(fail == 0 ? "RESULT: ALL PASS" : $"RESULT: {fail} FAIL");
return fail == 0 ? 0 : 1;