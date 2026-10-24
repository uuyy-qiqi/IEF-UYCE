using System.CommandLine;
using PclLike.Core.Minecraft;
using PclLike.Core.Minecraft.Launch;
using PclLike.Core.Storage;

var rootOption = new Option<string>("--root")
{
    Description = "游戏根目录（.minecraft），默认 %APPDATA%\\.pcllike",
    DefaultValueFactory = _ => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".pcllike"),
};

var versionOption = new Option<string>("--mcversion", "-mc")
{
    Description = "要启动的版本 id",
    DefaultValueFactory = _ => "1.20.1",
};

var userOption = new Option<string>("--username", "-u")
{
    Description = "玩家名（离线模式）",
    DefaultValueFactory = _ => "Player",
};

var memoryOption = new Option<int>("--memory", "-m")
{
    Description = "最大内存 MB",
    DefaultValueFactory = _ => 4096,
};

var javaOption = new Option<string?>("--java")
{
    Description = "指定 java.exe 路径；不传自动探测",
};

var serverOption = new Option<string?>("--server")
{
    Description = "启动后直连的服务器地址 host 或 host:port",
};

var dryRunOption = new Option<bool>("--dry-run")
{
    Description = "只安装并打印启动命令行，不真正启动游戏",
};

var noInstallOption = new Option<bool>("--no-install")
{
    Description = "跳过下载，直接用手头已有文件启动",
};

var rootCommand = new RootCommand("PclLike —— 一个 Minecraft 启动器")
{
    rootOption, versionOption, userOption, memoryOption, javaOption, serverOption,
    dryRunOption, noInstallOption,
};

// ---- 默认动作：安装 + 启动 ----
rootCommand.SetAction(async (parseResult, ct) =>
{
    var root = parseResult.GetValue(rootOption)!;
    var versionId = parseResult.GetValue(versionOption)!;
    var username = parseResult.GetValue(userOption)!;
    var memory = parseResult.GetValue(memoryOption);
    var javaPath = parseResult.GetValue(javaOption);
    var server = parseResult.GetValue(serverOption);
    var dryRun = parseResult.GetValue(dryRunOption);
    var noInstall = parseResult.GetValue(noInstallOption);

    string? serverHost = null;
    int? serverPort = null;
    if (!string.IsNullOrWhiteSpace(server))
    {
        var parts = server.Split(':', 2);
        serverHost = parts[0];
        if (parts.Length == 2 && int.TryParse(parts[1], out var p)) serverPort = p;
    }

    Console.WriteLine($"根目录   : {root}");
    Console.WriteLine($"版本     : {versionId}");
    Console.WriteLine($"玩家     : {username}");
    Console.WriteLine($"内存     : {memory} MB");
    Console.WriteLine();

    using var service = new LaunchService(root);

    var lastStage = "";
    Action<string, int, int> report = (stage, done, total) =>
    {
        if (stage != lastStage)
        {
            if (lastStage.Length > 0) Console.WriteLine();
            lastStage = stage;
            Console.Write($"  {stage}...");
        }
        if (total > 1)
            Console.Write($"\r  {stage} {done}/{total}   ");
    };
    var progress = new Progress<(string Stage, int Done, int Total)>(p => report(p.Stage, p.Done, p.Total));

    try
    {
        if (dryRun)
        {
            // 只安装，不启动
            var result = await service.Installer.InstallAsync(versionId,
                new Progress<InstanceInstaller.InstallProgress>(ip => report(ip.Stage, ip.Done, ip.Total)), ct);

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine($"客户端   : {result.ClientJar}");
            Console.WriteLine($"原生库   : {result.NativesDir}");
            Console.WriteLine($"主类     : {result.MainClass}");
            Console.WriteLine($"资源索引 : {result.AssetIndexId}");
            Console.WriteLine($"classpath: {result.Classpath.Split(ArgumentBuilder.ClasspathSeparator).Length} 个条目");

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("警告：");
                foreach (var w in result.Warnings) Console.WriteLine($"  - {w}");
            }
            return 0;
        }

        var outcome = await service.LaunchAsync(new LaunchService.LaunchOptions
        {
            VersionId = versionId,
            Username = username,
            MaxMemoryMb = memory,
            JavaPath = javaPath,
            ServerAddress = serverHost,
            ServerPort = serverPort,
            SkipInstall = noInstall,
        }, progress, line => Console.WriteLine(line), ct);

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine($"退出码   : {outcome.ExitCode}");
        if (outcome.Crashed)
            Console.WriteLine($"疑似崩溃 : {outcome.Reason}");
        Console.WriteLine($"日志     : {outcome.LogPath}");

        return outcome.Crashed ? 1 : 0;
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n已取消。");
        return 130;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n失败：{ex.Message}");
        return 1;
    }
});

// ---- 子命令：list 列出版本 ----
var listCommand = new Command("list", "列出可安装的 Minecraft 版本");
listCommand.SetAction(async (parseResult, ct) =>
{
    var root = parseResult.GetValue(rootOption)!;
    using var service = new LaunchService(root);
    var manifest = await service.Installer.FetchManifestAsync(ct);

    Console.WriteLine($"最新正式版 : {manifest.Latest?.Release}");
    Console.WriteLine($"最新快照   : {manifest.Latest?.Snapshot}");
    Console.WriteLine();

    foreach (var v in (manifest.Versions ?? []).Take(40))
        Console.WriteLine($"  {v.Id,-18} {v.Type,-10} {v.ReleaseTime:yyyy-MM-dd}");

    Console.WriteLine($"\n共 {manifest.Versions?.Count ?? 0} 个版本，已显示前 40 条。");
    return 0;
});
listCommand.Add(rootOption);
rootCommand.Add(listCommand);

// ---- 子命令：java 检测 ----
var javaCommand = new Command("java", "探测本机可用的 Java");
javaCommand.SetAction((parseResult, _) =>
{
    var root = parseResult.GetValue(rootOption)!;
    var found = JavaLocator.Discover(root);
    if (found.Count == 0)
    {
        Console.WriteLine("没有找到可用的 Java。");
        return Task.FromResult(1);
    }

    foreach (var j in found)
        Console.WriteLine($"  Java {j.MajorVersion,-3} {j.Vendor,-24} {j.Path}");

    return Task.FromResult(0);
});
javaCommand.Add(rootOption);
rootCommand.Add(javaCommand);

return await rootCommand.Parse(args).InvokeAsync();